module Platform.Access.Tests.EntraTests

open System.Text.Json
open Expecto
open Platform.Access
open Platform.Access.Desired

let private rules =
    Entra.policyRules
        [ "lead@example.com" ]
        {
            role = GlobalAdministrator
            maxHours = 1
            eligible = []
        }
    |> List.map (fun (id, body) -> id, JsonDocument.Parse(body).RootElement)

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

            test "eligibility may be permanent" {
                Expect.equal (text "isExpirationRequired" (rule "Expiration_Admin_Eligibility")) "False" "permanent"
            }

            test "each body names its rule and Graph type, as Graph's PATCH requires" {
                for id, body in rules do
                    Expect.equal (text "id" body) id id
                    Expect.stringStarts (text "@odata.type" body) "#microsoft.graph.unifiedRoleManagementPolicy" id
            }
        ]
