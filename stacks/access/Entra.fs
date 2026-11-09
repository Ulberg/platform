/// Turns the Entra part of a Desired.Plan into resources: groups, memberships, PIM for Groups, PIM for
/// directory roles and Conditional Access (PLAN.md §2.2, §7, §12). No decisions are made here; they are
/// all in Desired.fs.
module Platform.Access.Entra

open System.Text.Json
open System.Text.Json.Serialization
open Pulumi
open Pulumi.FSharp
open Pulumi.AzureAD
open Pulumi.AzureAD.Inputs
open Pulumi.Command.Local
open Platform.Access.People
open Platform.Access.Desired

type Deployed =
    {
        groups: Map<GroupName, Group>
        members: GroupMember list
        pimPolicies: GroupRoleManagementPolicy list
        eligibilities: PrivilegedAccessGroupEligibilitySchedule list
        directoryRolePolicies: Command list
        directoryRoleEligibilities: DirectoryRoleEligibilityScheduleRequest list
        conditionalAccess: ConditionalAccessPolicy list
    }

// ---------------------------------------------------------------------------------------------
// Graph's PIM policy rules for directory roles, in Graph's wire format (DTOs).

type RuleTargetDto =
    {
        [<JsonPropertyName("@odata.type")>]
        odataType: string
        caller: string
        operations: string list
        level: string
        inheritableSettings: string list
        enforcedSettings: string list
    }

type ExpirationRuleDto =
    {
        [<JsonPropertyName("@odata.type")>]
        odataType: string
        id: string
        isExpirationRequired: bool
        maximumDuration: string
        target: RuleTargetDto
    }

type EnablementRuleDto =
    {
        [<JsonPropertyName("@odata.type")>]
        odataType: string
        id: string
        enabledRules: string list
        target: RuleTargetDto
    }

let private target caller level =
    {
        odataType = "microsoft.graph.unifiedRoleManagementPolicyRuleTarget"
        caller = caller
        operations = [ "All" ]
        level = level
        inheritableSettings = []
        enforcedSettings = []
    }

let private expiration id required maximumDuration target =
    id,
    JsonSerializer.Serialize
        {
            odataType = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule"
            id = id
            isExpirationRequired = required
            maximumDuration = maximumDuration
            target = target
        }

let private mfaAndJustification id target =
    id,
    JsonSerializer.Serialize
        {
            odataType = "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule"
            id = id
            enabledRules = [ "MultiFactorAuthentication"; "Justification" ]
            target = target
        }

/// The rules to PATCH on a directory role's PIM policy, as (rule ID, JSON body). The same settings as the
/// group PIM policies: activation for at most `maxHours` with MFA + justification; permanent eligibility;
/// direct active assignments need MFA + justification and expire after 15 days (the shortest PIM allows),
/// so nobody can be given the role permanently outside PIM.
let policyRules (pim: DirectoryRolePim) : (string * string) list =
    [
        expiration "Expiration_EndUser_Assignment" true $"PT{pim.maxHours}H" (target "EndUser" "Assignment")
        mfaAndJustification "Enablement_EndUser_Assignment" (target "EndUser" "Assignment")
        expiration "Expiration_Admin_Eligibility" false "P365D" (target "Admin" "Eligibility")
        expiration "Expiration_Admin_Assignment" true "P15D" (target "Admin" "Assignment")
        mfaAndJustification "Enablement_Admin_Assignment" (target "Admin" "Assignment")
    ]

// ---------------------------------------------------------------------------------------------
// Resources

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

/// A directory role's PIM policy, set by `script` (scripts/pim-directory-role-policy.sh) on apply. A changed
/// rule or script replaces the resource, which runs it again; removing it from the plan leaves the policy as
/// it is. One resource per role, because the rules have to be set one after another.
let private rolePolicy (script: string) (pim: DirectoryRolePim) =
    let rules = policyRules pim |> List.map snd |> String.concat "," |> sprintf "[%s]"

    Command(
        $"{DirectoryRole.value pim.role}-pim-policy",
        CommandArgs(
            Create = script,
            Interpreter = inputList [ input "/bin/bash"; input "-c" ],
            Environment = inputMap [ "ROLE_ID", input (DirectoryRole.templateId pim.role); "RULES", input rules ],
            Triggers = inputList [ input (box rules); input (box script) ]
        )
    )

let private roleEligibility (policy: Command) (pim: DirectoryRolePim) (p: Person) =
    DirectoryRoleEligibilityScheduleRequest(
        $"{DirectoryRole.value pim.role}-eligible-{Person.key p}",
        DirectoryRoleEligibilityScheduleRequestArgs(
            RoleDefinitionId = DirectoryRole.templateId pim.role,
            PrincipalId = EntraObjectId.string p.entra,
            DirectoryScopeId = "/",
            Justification = "Generated from access/people.yaml"
        ),
        // The policy must allow permanent eligibility before this request is made.
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

/// `policyScript` is the text of scripts/pim-directory-role-policy.sh.
let deploy (policyScript: string) (plan: Plan) : Deployed =
    let groups = plan.groups |> List.map (fun name -> name, group name) |> Map.ofList
    let policies = plan.pim |> List.map (fun rule -> rule, pimPolicy groups rule)

    let rolePolicies =
        plan.directoryRoles |> List.map (fun pim -> pim, rolePolicy policyScript pim)

    {
        groups = groups
        members = plan.memberships |> List.map (groupMember groups)
        pimPolicies = policies |> List.map snd
        eligibilities =
            policies
            |> List.collect (fun (rule, policy) -> rule.eligible |> List.map (eligibility groups policy rule))
        directoryRolePolicies = rolePolicies |> List.map snd
        directoryRoleEligibilities =
            rolePolicies
            |> List.collect (fun (pim, policy) -> pim.eligible |> List.map (roleEligibility policy pim))
        conditionalAccess = plan.conditionalAccess |> List.map (caPolicy groups)
    }
