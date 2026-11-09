/// Workforce-tenant groups, PIM for Groups and Conditional Access (PLAN.md §2.2, §7, §12).
module Platform.Access.Entra

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureAD
open Pulumi.AzureAD.Inputs
open Platform.Access.People

type Groups =
    {
        infraDevelopers: Group
        appDevelopers: Group
        /// Standing read access. Infra developers are members.
        platformRead: Group
        /// Break-glass write. Only reachable by activating PIM, max 2 h.
        platformWrite: Group
        /// Admin. Only reachable by activating PIM, max 1 h, platform leads only.
        platformAdmin: Group
    }

let private group (name: string) (description: string) =
    Group(
        name,
        GroupArgs(
            DisplayName = name,
            Description = description,
            SecurityEnabled = true,
            MailEnabled = false,
            PreventDuplicateNames = true
        )
    )

let private memberOf (group: Group) (resourceName: string) (memberObjectId: Input<string>) =
    GroupMember(resourceName, GroupMemberArgs(GroupObjectId = group.ObjectId, MemberObjectId = memberObjectId))
    |> ignore

let private nest (outer: Group) (inner: Group) (innerName: string) =
    memberOf outer $"{innerName}-in-{outer.GetResourceName()}" (io inner.ObjectId)

let groups (access: Access) =
    let g =
        {
            infraDevelopers = group "infra-developers" "Infra developers (access/people.yaml). Standing read only."
            appDevelopers = group "app-developers" "App developers (access/people.yaml). App logs, metrics and traces."
            platformRead = group "platform-read" "Read on Azure, AKS and the DB. Contains platform-write."
            platformWrite = group "platform-write" "Break-glass write. PIM only (max 2 h). Contains platform-admin."
            platformAdmin = group "platform-admin" "Platform admin. PIM only (max 1 h), platform leads only."
        }

    for p in access.infraDevelopers do
        memberOf g.infraDevelopers $"infra-developers-{Person.key p}" (input (EntraObjectId.string p.entra))

    for p in access.appDevelopers do
        memberOf g.appDevelopers $"app-developers-{Person.key p}" (input (EntraObjectId.string p.entra))

    // read ⊃ write ⊃ admin, so each consumer only maps three levels.
    nest g.platformRead g.infraDevelopers "infra-developers"
    nest g.platformRead g.platformWrite "platform-write"
    nest g.platformWrite g.platformAdmin "platform-admin"
    g

/// PIM for Groups: membership is eligible only, activated with MFA + justification, time-boxed.
let private pimGroup (group: Group) (name: string) maxHours (eligible: Person list) =
    let policy =
        GroupRoleManagementPolicy(
            $"{name}-pim-policy",
            GroupRoleManagementPolicyArgs(
                GroupId = group.ObjectId,
                RoleId = "member",
                ActivationRules =
                    GroupRoleManagementPolicyActivationRulesArgs(
                        MaximumDuration = $"PT{maxHours}H",
                        RequireMultifactorAuthentication = true,
                        RequireJustification = true,
                        RequireApproval = false
                    ),
                // Eligibility may be permanent (it is the "who may ask" list). Direct active assignments must
                // expire, after at least 15 days (the shortest PIM allows). Normal access is activation only.
                EligibleAssignmentRules =
                    GroupRoleManagementPolicyEligibleAssignmentRulesArgs(ExpirationRequired = false),
                ActiveAssignmentRules =
                    GroupRoleManagementPolicyActiveAssignmentRulesArgs(
                        ExpirationRequired = true,
                        ExpireAfter = "P15D",
                        RequireMultifactorAuthentication = true,
                        RequireJustification = true
                    )
            )
        )

    for p in eligible do
        PrivilegedAccessGroupEligibilitySchedule(
            $"{name}-eligible-{Person.key p}",
            PrivilegedAccessGroupEligibilityScheduleArgs(
                GroupId = group.ObjectId,
                PrincipalId = EntraObjectId.string p.entra,
                AssignmentType = "member",
                PermanentAssignment = true,
                Justification = "Generated from access/people.yaml"
            ),
            CustomResourceOptions(DependsOn = InputList.op_Implicit (policy :> Resource))
        )
        |> ignore

let pim (access: Access) (g: Groups) =
    pimGroup g.platformWrite "platform-write" 2 access.infraDevelopers
    pimGroup g.platformAdmin "platform-admin" 1 access.platformLeads

type ConditionalAccessMode =
    | Off
    | ReportOnly
    | Enforced

let parseMode =
    function
    | "off" -> Off
    | "report-only" -> ReportOnly
    | "on" -> Enforced
    | other -> failwith $"access:conditionalAccess must be off, report-only or on, not '{other}'"

/// Windows Azure Service Management API (portal, az CLI, ARM).
let private azureManagementApp = "797f4846-ba00-4fd7-ba43-dac1f8f63013"

/// Built-in authentication strength "Phishing-resistant MFA" (passkeys, FIDO2, Windows Hello).
let private phishingResistant =
    "/policies/authenticationStrengthPolicies/00000000-0000-0000-0000-000000000004"

/// Replaces security defaults. Emergency-access accounts are excluded from every policy.
let conditionalAccess (mode: ConditionalAccessMode) (access: Access) (g: Groups) =
    let state =
        match mode with
        | Off -> None
        | ReportOnly -> Some "enabledForReportingButNotEnforced"
        | Enforced -> Some "enabled"

    let excluded = access.emergencyAccess |> List.map EntraObjectId.string

    let policy
        name
        (conditions: ConditionalAccessPolicyConditionsArgs)
        (grant: ConditionalAccessPolicyGrantControlsArgs)
        =
        state
        |> Option.iter (fun state ->
            ConditionalAccessPolicy(
                name,
                ConditionalAccessPolicyArgs(
                    DisplayName = name,
                    State = state,
                    Conditions = conditions,
                    GrantControls = grant
                )
            )
            |> ignore)

    let users (included: ConditionalAccessPolicyConditionsUsersArgs) =
        included.ExcludedUsers <- InputList.op_Implicit (Array.ofList excluded)
        included

    let allApps =
        ConditionalAccessPolicyConditionsApplicationsArgs(IncludedApplications = InputList.op_Implicit [| "All" |])

    policy
        "CA01 Require MFA for everyone"
        (ConditionalAccessPolicyConditionsArgs(
            ClientAppTypes = InputList.op_Implicit [| "all" |],
            Applications = allApps,
            Users =
                users (ConditionalAccessPolicyConditionsUsersArgs(IncludedUsers = InputList.op_Implicit [| "All" |]))
        ))
        (ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", BuiltInControls = InputList.op_Implicit [| "mfa" |]))

    policy
        "CA02 Block legacy authentication"
        (ConditionalAccessPolicyConditionsArgs(
            ClientAppTypes = InputList.op_Implicit [| "exchangeActiveSync"; "other" |],
            Applications = allApps,
            Users =
                users (ConditionalAccessPolicyConditionsUsersArgs(IncludedUsers = InputList.op_Implicit [| "All" |]))
        ))
        (ConditionalAccessPolicyGrantControlsArgs(
            Operator = "OR",
            BuiltInControls = InputList.op_Implicit [| "block" |]
        ))

    policy
        "CA03 Phishing-resistant MFA for Azure management"
        (ConditionalAccessPolicyConditionsArgs(
            ClientAppTypes = InputList.op_Implicit [| "all" |],
            Applications =
                ConditionalAccessPolicyConditionsApplicationsArgs(
                    IncludedApplications = InputList.op_Implicit [| azureManagementApp |]
                ),
            Users =
                users (ConditionalAccessPolicyConditionsUsersArgs(IncludedUsers = InputList.op_Implicit [| "All" |]))
        ))
        (ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", AuthenticationStrengthPolicyId = phishingResistant))

    policy
        "CA04 Risky sign-ins by infra developers need MFA"
        (ConditionalAccessPolicyConditionsArgs(
            ClientAppTypes = InputList.op_Implicit [| "all" |],
            Applications = allApps,
            SignInRiskLevels = InputList.op_Implicit [| "medium"; "high" |],
            Users =
                users (
                    ConditionalAccessPolicyConditionsUsersArgs(
                        IncludedGroups = inputList [ io g.infraDevelopers.ObjectId ]
                    )
                )
        ))
        (ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", BuiltInControls = InputList.op_Implicit [| "mfa" |]))
