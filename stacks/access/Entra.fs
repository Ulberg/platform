/// Turns the Entra part of a Desired.Plan into resources: groups, memberships, PIM for Groups and
/// Conditional Access (PLAN.md §2.2, §7, §12). No decisions are made here; they are all in Desired.fs.
module Platform.Access.Entra

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureAD
open Pulumi.AzureAD.Inputs
open Platform.Access.People
open Platform.Access.Desired

type Deployed =
    {
        groups: Map<GroupName, Group>
        members: GroupMember list
        pimPolicies: GroupRoleManagementPolicy list
        eligibilities: PrivilegedAccessGroupEligibilitySchedule list
        conditionalAccess: ConditionalAccessPolicy list
    }

let private group (name: GroupName) =
    Group(
        GroupName.value name,
        GroupArgs(
            DisplayName = GroupName.value name,
            Description = GroupName.description name,
            SecurityEnabled = true,
            MailEnabled = false,
            PreventDuplicateNames = true
        )
    )

let private groupMember (groups: Map<GroupName, Group>) (m: Membership) =
    let memberId =
        match m.includes with
        | Member.Person p -> input (EntraObjectId.string p.entra)
        | Member.Group inner -> io groups[inner].ObjectId

    GroupMember(
        Membership.resourceName m,
        GroupMemberArgs(GroupObjectId = groups[m.group].ObjectId, MemberObjectId = memberId)
    )

let private pimPolicy (groups: Map<GroupName, Group>) (rule: PimRule) =
    GroupRoleManagementPolicy(
        $"{GroupName.value rule.group}-pim-policy",
        GroupRoleManagementPolicyArgs(
            GroupId = groups[rule.group].ObjectId,
            RoleId = "member",
            ActivationRules =
                GroupRoleManagementPolicyActivationRulesArgs(
                    MaximumDuration = $"PT{rule.maxHours}H",
                    RequireMultifactorAuthentication = true,
                    RequireJustification = true,
                    RequireApproval = false
                ),
            // Eligibility may be permanent (it is the "who may ask" list). Direct active assignments must
            // expire, after at least 15 days (the shortest PIM allows). Normal access is activation only.
            EligibleAssignmentRules = GroupRoleManagementPolicyEligibleAssignmentRulesArgs(ExpirationRequired = false),
            ActiveAssignmentRules =
                GroupRoleManagementPolicyActiveAssignmentRulesArgs(
                    ExpirationRequired = true,
                    ExpireAfter = "P15D",
                    RequireMultifactorAuthentication = true,
                    RequireJustification = true
                )
        )
    )

let private eligibility
    (groups: Map<GroupName, Group>)
    (policy: GroupRoleManagementPolicy)
    (rule: PimRule)
    (p: Person)
    =
    PrivilegedAccessGroupEligibilitySchedule(
        $"{GroupName.value rule.group}-eligible-{Person.key p}",
        PrivilegedAccessGroupEligibilityScheduleArgs(
            GroupId = groups[rule.group].ObjectId,
            PrincipalId = EntraObjectId.string p.entra,
            AssignmentType = "member",
            PermanentAssignment = true,
            Justification = "Generated from access/people.yaml"
        ),
        CustomResourceOptions(DependsOn = InputList.op_Implicit (policy :> Resource))
    )

let private caPolicy (groups: Map<GroupName, Group>) (p: CaPolicy) =
    let users =
        match p.users with
        | AllUsers ->
            ConditionalAccessPolicyConditionsUsersArgs(
                IncludedUsers = inputList [ input "All" ],
                ExcludedUsers = inputList [ for id in p.excludedUsers -> input (EntraObjectId.string id) ]
            )
        | Groups names ->
            ConditionalAccessPolicyConditionsUsersArgs(
                IncludedGroups = inputList [ for name in names -> io groups[name].ObjectId ],
                ExcludedUsers = inputList [ for id in p.excludedUsers -> input (EntraObjectId.string id) ]
            )

    let grant =
        match p.grant with
        | RequireMfa ->
            ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", BuiltInControls = inputList [ input "mfa" ])
        | Block ->
            ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", BuiltInControls = inputList [ input "block" ])
        | RequireStrength id ->
            ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", AuthenticationStrengthPolicyId = id)

    ConditionalAccessPolicy(
        p.name,
        ConditionalAccessPolicyArgs(
            DisplayName = p.name,
            State =
                (match p.state with
                 | ReportOnly -> "enabledForReportingButNotEnforced"
                 | Enforced -> "enabled"),
            Conditions =
                ConditionalAccessPolicyConditionsArgs(
                    ClientAppTypes = inputList [ for t in p.clientAppTypes -> input t ],
                    Applications =
                        ConditionalAccessPolicyConditionsApplicationsArgs(
                            IncludedApplications = inputList [ for a in p.applications -> input a ]
                        ),
                    SignInRiskLevels = inputList [ for r in p.signInRisk -> input r ],
                    Users = users
                ),
            GrantControls = grant
        )
    )

let deploy (plan: Plan) : Deployed =
    let groups = plan.groups |> List.map (fun name -> name, group name) |> Map.ofList
    let policies = plan.pim |> List.map (fun rule -> rule, pimPolicy groups rule)

    {
        groups = groups
        members = plan.memberships |> List.map (groupMember groups)
        pimPolicies = policies |> List.map snd
        eligibilities =
            policies
            |> List.collect (fun (rule, policy) -> rule.eligible |> List.map (eligibility groups policy rule))
        conditionalAccess = plan.conditionalAccess |> List.map (caPolicy groups)
    }
