module Platform.Access.Tests.EntraTests

open System.Text.Json
open Expecto
open Platform.Access
open Platform.Access.Desired

let private rulesWith activation =
    Entra.policyRules
        [ "lead@example.com" ]
        {
            role = GlobalAdministrator
            maxHours = 1
            activation = activation
            eligible = []
        }
    |> List.map (fun (id, body) -> id, JsonDocument.Parse(body).RootElement)

let private rules = rulesWith Mfa

let private rule id =
    rules |> List.find (fst >> (=) id) |> snd

let private text (name: string) (e: JsonElement) = e.GetProperty(name).ToString()

[<Tests>]
let tests =
    testList
        "directory-role PIM policy"
        [
            test "sets activation, eligibility and direct-assignment rules" {
                Expect.equal
                    (rules |> List.map fst)
                    [
                        "Expiration_EndUser_Assignment"
                        "AuthenticationContext_EndUser_Assignment"
                        "Enablement_EndUser_Assignment"
                        "Expiration_Admin_Eligibility"
                        "Expiration_Admin_Assignment"
                        "Enablement_Admin_Assignment"
                        "Notification_Admin_EndUser_Assignment"
                        "Notification_Admin_Admin_Eligibility"
                        "Notification_Admin_Admin_Assignment"
                    ]
                    "rule IDs"
            }

            test "activation lasts at most maxHours" {
                let r = rule "Expiration_EndUser_Assignment"
                Expect.equal (text "maximumDuration" r) "PT1H" "duration"
                Expect.equal (text "isExpirationRequired" r) "True" "required"
            }

            test "activation needs MFA and a justification" {
                Expect.equal
                    (text "enabledRules" (rule "Enablement_EndUser_Assignment"))
                    """["MultiFactorAuthentication","Justification"]"""
                    "enabled rules"
            }

            test "a direct active assignment can't be permanent and needs MFA and a justification" {
                let r = rule "Expiration_Admin_Assignment"
                Expect.equal (text "isExpirationRequired" r) "True" "expiry required"
                Expect.equal (text "maximumDuration" r) "P15D" "at most 15 days"

                Expect.equal
                    (text "enabledRules" (rule "Enablement_Admin_Assignment"))
                    """["MultiFactorAuthentication","Justification"]"""
                    "enabled rules"
            }

            test "every activation and every new assignment emails the alert list" {
                for id in
                    [
                        "Notification_Admin_EndUser_Assignment"
                        "Notification_Admin_Admin_Eligibility"
                        "Notification_Admin_Admin_Assignment"
                    ] do
                    let r = rule id
                    Expect.equal (text "notificationLevel" r) "All" id
                    Expect.equal (text "notificationRecipients" r) """["lead@example.com"]""" id
            }

            test "while Conditional Access isn't on, activation uses PIM's MFA and no authentication context" {
                Expect.equal (text "isEnabled" (rule "AuthenticationContext_EndUser_Assignment")) "False" "context off"

                Expect.isFalse
                    ((rule "AuthenticationContext_EndUser_Assignment").TryGetProperty "claimValue"
                     |> fst)
                    "no claim value sent"
            }

            test "with the authentication context, activation needs it instead of MFA, which is switched off first" {
                let ctx = rulesWith (AuthenticationContext "c1")

                let activationRules =
                    ctx
                    |> List.map fst
                    |> List.filter (fun id -> id.EndsWith "_EndUser_Assignment" && not (id.StartsWith "Notification"))

                Expect.equal
                    activationRules
                    [
                        "Expiration_EndUser_Assignment"
                        "Enablement_EndUser_Assignment"
                        "AuthenticationContext_EndUser_Assignment"
                    ]
                    "MFA off before the context goes on (Entra refuses both at once)"

                let find id = ctx |> List.find (fst >> (=) id) |> snd
                Expect.equal (text "claimValue" (find "AuthenticationContext_EndUser_Assignment")) "c1" "claim"
                Expect.equal (text "isEnabled" (find "AuthenticationContext_EndUser_Assignment")) "True" "enabled"

                Expect.equal
                    (text "enabledRules" (find "Enablement_EndUser_Assignment"))
                    """["Justification"]"""
                    "justification only"
            }

            test "eligibility may be permanent" {
                Expect.equal (text "isExpirationRequired" (rule "Expiration_Admin_Eligibility")) "False" "permanent"
            }

            test "each body names its rule and Graph type, as Graph's PATCH requires" {
                for id, body in rules do
                    Expect.equal (text "id" body) id id
                    Expect.stringStarts (text "@odata.type" body) "#microsoft.graph.unifiedRoleManagementPolicy" id
            }
        ]

[<Tests>]
let tenantDefaults =
    testList
        "tenant defaults"
        [
            test "members can't create groups, apps or tenants, consent to apps, invite guests or sign up" {
                let policy =
                    JsonDocument.Parse(Entra.authorizationPolicyBody TenantDefaults.locked).RootElement

                let user = policy.GetProperty "defaultUserRolePermissions"

                for name in
                    [
                        "allowedToCreateSecurityGroups"
                        "allowedToCreateApps"
                        "allowedToCreateTenants"
                    ] do
                    Expect.equal (text name user) "False" name

                Expect.equal (text "permissionGrantPoliciesAssigned" user) "[]" "no user consent"
                Expect.equal (text "allowInvitesFrom" policy) "adminsAndGuestInviters" "guests only by admins"
                Expect.equal (text "allowEmailVerifiedUsersToJoinOrganization" policy) "False" "no joining by email"
                Expect.equal (text "allowedToSignUpEmailBasedSubscriptions" policy) "False" "no self-service sign-ups"
            }

            test "members can still read other users (people pickers and PIM need it)" {
                let user =
                    JsonDocument.Parse(Entra.authorizationPolicyBody TenantDefaults.locked).RootElement.GetProperty
                        "defaultUserRolePermissions"

                Expect.equal (text "allowedToReadOtherUsers" user) "True" "read other users"
            }
        ]
