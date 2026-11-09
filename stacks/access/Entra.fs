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
        /// Conditional Access policies the azuread provider can't express (authentication-context targets).
        contextPolicies: Command list
    }

/// The Graph scripts the stack runs as Pulumi Commands, read in Program's input step (scripts/).
type Scripts =
    {
        pimDirectoryRolePolicy: string
        caContextPolicy: string
        caContextPolicyDelete: string
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

type NotificationRuleDto =
    {
        [<JsonPropertyName("@odata.type")>]
        odataType: string
        id: string
        notificationType: string
        recipientType: string
        notificationLevel: string
        isDefaultRecipientsEnabled: bool
        notificationRecipients: string list
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

/// Option fields that are None are left out of the JSON instead of being sent as null.
let private json =
    JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

type AuthenticationContextRuleDto =
    {
        [<JsonPropertyName("@odata.type")>]
        odataType: string
        id: string
        isEnabled: bool
        claimValue: string option
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

let private enablement id enabledRules target =
    id,
    JsonSerializer.Serialize
        {
            odataType = "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule"
            id = id
            enabledRules = enabledRules
            target = target
        }

let private mfaAndJustification id target =
    enablement id [ "MultiFactorAuthentication"; "Justification" ] target

let private authenticationContext (context: string option) target =
    "AuthenticationContext_EndUser_Assignment",
    JsonSerializer.Serialize(
        {
            odataType = "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule"
            id = "AuthenticationContext_EndUser_Assignment"
            isEnabled = Option.isSome context
            claimValue = context
            target = target
        },
        json
    )

/// Activation asks for MFA, or for the authentication context instead (they exclude each other). The order
/// keeps one of them on at every moment while switching.
let private activationRules (activation: ActivationCheck) =
    let endUser = target "EndUser" "Assignment"

    match activation with
    | Mfa ->
        [
            mfaAndJustification "Enablement_EndUser_Assignment" endUser
            authenticationContext None endUser
        ]
    | AuthenticationContext id ->
        [
            authenticationContext (Some id) endUser
            enablement "Enablement_EndUser_Assignment" [ "Justification" ] endUser
        ]

/// Emails the admins (the role's default recipients plus `alertEmails`) about every event of `target`.
let private notifyAdmins (alertEmails: string list) id target =
    id,
    JsonSerializer.Serialize
        {
            odataType = "#microsoft.graph.unifiedRoleManagementPolicyNotificationRule"
            id = id
            notificationType = "Email"
            recipientType = "Admin"
            notificationLevel = "All"
            isDefaultRecipientsEnabled = true
            notificationRecipients = alertEmails
            target = target
        }

/// The rules to PATCH on a directory role's PIM policy, as (rule ID, JSON body). The same settings as the
/// group PIM policies: activation for at most `maxHours` with `activation` + justification; permanent eligibility;
/// direct active assignments need MFA + justification and expire after 15 days (the shortest PIM allows),
/// so nobody can be given the role permanently outside PIM. `alertEmails` hear about every activation and
/// every new assignment (D40).
let policyRules (alertEmails: string list) (pim: DirectoryRolePim) : (string * string) list =
    [
        expiration "Expiration_EndUser_Assignment" true $"PT{pim.maxHours}H" (target "EndUser" "Assignment")
        yield! activationRules pim.activation
        expiration "Expiration_Admin_Eligibility" false "P365D" (target "Admin" "Eligibility")
        expiration "Expiration_Admin_Assignment" true "P15D" (target "Admin" "Assignment")
        mfaAndJustification "Enablement_Admin_Assignment" (target "Admin" "Assignment")
        notifyAdmins alertEmails "Notification_Admin_EndUser_Assignment" (target "EndUser" "Assignment")
        notifyAdmins alertEmails "Notification_Admin_Admin_Eligibility" (target "Admin" "Eligibility")
        notifyAdmins alertEmails "Notification_Admin_Admin_Assignment" (target "Admin" "Assignment")
    ]

// ---------------------------------------------------------------------------------------------
// Conditional Access policies that target an authentication context, in Graph's wire format (DTOs).

type CaApplicationsDto =
    {
        includeAuthenticationContextClassReferences: string list
    }

type CaUsersDto =
    {
        includeUsers: string list
        includeGroups: string list
        excludeUsers: string list
    }

type CaConditionsDto =
    {
        clientAppTypes: string list
        applications: CaApplicationsDto
        users: CaUsersDto
        signInRiskLevels: string list
    }

type AuthenticationStrengthDto = { id: string }

type CaGrantDto =
    {
        operator: string
        builtInControls: string list
        authenticationStrength: AuthenticationStrengthDto option
    }

type SignInFrequencyDto =
    {
        isEnabled: bool
        frequencyInterval: string
        [<JsonPropertyName("type")>]
        unit: string option
        value: int option
        authenticationType: string
    }

type PersistentBrowserDto = { isEnabled: bool; mode: string }

type CaSessionDto =
    {
        signInFrequency: SignInFrequencyDto
        persistentBrowser: PersistentBrowserDto option
    }

type CaPolicyDto =
    {
        displayName: string
        state: string
        conditions: CaConditionsDto
        grantControls: CaGrantDto option
        sessionControls: CaSessionDto option
    }

let private caState =
    function
    | ReportOnly -> "enabledForReportingButNotEnforced"
    | Enforced -> "enabled"

let private grantDto =
    function
    | RequireMfa ->
        {
            operator = "OR"
            builtInControls = [ "mfa" ]
            authenticationStrength = None
        }
    | Block ->
        {
            operator = "OR"
            builtInControls = [ "block" ]
            authenticationStrength = None
        }
    | RequireStrength policyId ->
        {
            operator = "OR"
            builtInControls = []
            // Graph wants the bare ID; the provider's form is the path ".../authenticationStrengthPolicies/<id>".
            authenticationStrength =
                Some
                    {
                        id = policyId.Split('/') |> Array.last
                    }
        }

let private sessionDto (s: CaSession) =
    {
        signInFrequency =
            match s.signIn with
            | Hours h ->
                {
                    isEnabled = true
                    frequencyInterval = "timeBased"
                    unit = Some "hours"
                    value = Some h
                    authenticationType = "primaryAndSecondaryAuthentication"
                }
            | EveryTime ->
                {
                    isEnabled = true
                    frequencyInterval = "everyTime"
                    unit = None
                    value = None
                    authenticationType = "primaryAndSecondaryAuthentication"
                }
        persistentBrowser =
            if s.persistentBrowser then
                None
            else
                Some { isEnabled = true; mode = "never" }
    }

/// The Graph body of a policy that targets authentication context `contextId`. `groupIds` resolves the
/// Entra object IDs of the groups it applies to.
let contextPolicyBody (groupIds: Map<GroupName, string>) (contextId: string) (p: CaPolicy) : string =
    JsonSerializer.Serialize(
        {
            displayName = p.name
            state = caState p.state
            conditions =
                {
                    clientAppTypes = p.clientAppTypes
                    applications =
                        {
                            includeAuthenticationContextClassReferences = [ contextId ]
                        }
                    users =
                        {
                            includeUsers =
                                match p.users with
                                | AllUsers -> [ "All" ]
                                | Guests -> [ "GuestsOrExternalUsers" ]
                                | Groups _ -> []
                            includeGroups =
                                match p.users with
                                | AllUsers
                                | Guests -> []
                                | Groups names -> names |> List.map (fun n -> groupIds[n])
                            excludeUsers = p.excludedUsers |> List.map EntraObjectId.string
                        }
                    signInRiskLevels = p.signInRisk
                }
            grantControls =
                match p.controls with
                | Grant g
                | GrantAndSession(g, _) -> Some(grantDto g)
                | Session _ -> None
            sessionControls =
                match p.controls with
                | Session s
                | GrantAndSession(_, s) -> Some(sessionDto s)
                | Grant _ -> None
        },
        json
    )

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

let private pimPolicy
    (groups: Map<GroupName, Group>)
    (alertEmails: Output<string list>)
    (after: Resource list)
    (rule: PimRule)
    =
    let recipients () : InputList<string> =
        alertEmails.Apply(fun emails -> List.toArray emails) |> InputList.op_Implicit

    let activation =
        match rule.activation with
        | Mfa ->
            GroupRoleManagementPolicyActivationRulesArgs(
                MaximumDuration = $"PT{rule.maxHours}H",
                RequireMultifactorAuthentication = true,
                RequireJustification = true,
                RequireApproval = false
            )
        | AuthenticationContext id ->
            GroupRoleManagementPolicyActivationRulesArgs(
                MaximumDuration = $"PT{rule.maxHours}H",
                RequiredConditionalAccessAuthenticationContext = id,
                RequireJustification = true,
                RequireApproval = false
            )

    GroupRoleManagementPolicy(
        $"{GroupName.value rule.group}-pim-policy",
        GroupRoleManagementPolicyArgs(
            GroupId = groups[rule.group].ObjectId,
            RoleId = "member",
            ActivationRules = activation,
            // Eligibility may be permanent (it is the "who may ask" list). Direct active assignments must
            // expire, after at least 15 days (the shortest PIM allows). Normal access is activation only.
            EligibleAssignmentRules = GroupRoleManagementPolicyEligibleAssignmentRulesArgs(ExpirationRequired = false),
            ActiveAssignmentRules =
                GroupRoleManagementPolicyActiveAssignmentRulesArgs(
                    ExpirationRequired = true,
                    ExpireAfter = "P15D",
                    RequireMultifactorAuthentication = true,
                    RequireJustification = true
                ),
            // D40: every activation, and every new eligible or active member, emails the admins.
            NotificationRules =
                GroupRoleManagementPolicyNotificationRulesArgs(
                    EligibleActivations =
                        GroupRoleManagementPolicyNotificationRulesEligibleActivationsArgs(
                            AdminNotifications =
                                GroupRoleManagementPolicyNotificationRulesEligibleActivationsAdminNotificationsArgs(
                                    NotificationLevel = "All",
                                    DefaultRecipients = true,
                                    AdditionalRecipients = recipients ()
                                )
                        ),
                    EligibleAssignments =
                        GroupRoleManagementPolicyNotificationRulesEligibleAssignmentsArgs(
                            AdminNotifications =
                                GroupRoleManagementPolicyNotificationRulesEligibleAssignmentsAdminNotificationsArgs(
                                    NotificationLevel = "All",
                                    DefaultRecipients = true,
                                    AdditionalRecipients = recipients ()
                                )
                        ),
                    ActiveAssignments =
                        GroupRoleManagementPolicyNotificationRulesActiveAssignmentsArgs(
                            AdminNotifications =
                                GroupRoleManagementPolicyNotificationRulesActiveAssignmentsAdminNotificationsArgs(
                                    NotificationLevel = "All",
                                    DefaultRecipients = true,
                                    AdditionalRecipients = recipients ()
                                )
                        )
                )
        ),
        // The authentication context policy (CA05) must exist before PIM asks for it.
        CustomResourceOptions(DependsOn = inputList [ for r in after -> input r ])
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
let private rolePolicy
    (script: string)
    (alertEmails: Output<string list>)
    (after: Resource list)
    (pim: DirectoryRolePim)
    =
    // Secret, because it carries the alert addresses.
    let rules =
        alertEmails.Apply(fun emails -> policyRules emails pim |> List.map snd |> String.concat "," |> sprintf "[%s]")

    Command(
        $"{DirectoryRole.value pim.role}-pim-policy",
        CommandArgs(
            Create = script,
            Interpreter = inputList [ input "/bin/bash"; input "-c" ],
            Environment = inputMap [ "ROLE_ID", input (DirectoryRole.templateId pim.role); "RULES", io rules ],
            Triggers = inputList [ io (rules.Apply box); input (box script) ]
        ),
        CustomResourceOptions(DependsOn = inputList [ for r in after -> input r ])
    )

/// A Conditional Access policy that targets an authentication context, plus the context itself, set by
/// scripts/ca-context-policy.sh on apply. Deleting the resource deletes the policy (the context stays).
let private contextPolicy
    (scripts: Scripts)
    (groupIds: Output<Map<GroupName, string>>)
    (after: Resource list)
    (contextId: string, p: CaPolicy)
    =
    let body = groupIds.Apply(fun ids -> contextPolicyBody ids contextId p)

    Command(
        p.name,
        CommandArgs(
            Create = scripts.caContextPolicy,
            Delete = scripts.caContextPolicyDelete,
            Interpreter = inputList [ input "/bin/bash"; input "-c" ],
            Environment =
                inputMap
                    [
                        "CONTEXT_ID", input contextId
                        "POLICY_NAME", input p.name
                        "POLICY", io body
                    ],
            Triggers = inputList [ io (body.Apply box); input (box scripts.caContextPolicy) ]
        ),
        // After the provider's policies: creating them all at once gets Graph to throttle.
        CustomResourceOptions(DependsOn = inputList [ for r in after -> input r ])
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

let private grantArgs =
    function
    | RequireMfa ->
        ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", BuiltInControls = inputList [ input "mfa" ])
    | Block -> ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", BuiltInControls = inputList [ input "block" ])
    | RequireStrength id ->
        ConditionalAccessPolicyGrantControlsArgs(Operator = "OR", AuthenticationStrengthPolicyId = id)

/// Without `persistentBrowser`, the browser never offers "stay signed in". With it, the setting is left
/// out (Entra's default), because "always" would force persistent sessions on everyone.
let private sessionArgs (s: CaSession) =
    let authType = "primaryAndSecondaryAuthentication"

    match s.signIn, s.persistentBrowser with
    | Hours h, true ->
        ConditionalAccessPolicySessionControlsArgs(
            SignInFrequency = h,
            SignInFrequencyPeriod = "hours",
            SignInFrequencyInterval = "timeBased",
            SignInFrequencyAuthenticationType = authType
        )
    | Hours h, false ->
        ConditionalAccessPolicySessionControlsArgs(
            SignInFrequency = h,
            SignInFrequencyPeriod = "hours",
            SignInFrequencyInterval = "timeBased",
            SignInFrequencyAuthenticationType = authType,
            PersistentBrowserMode = "never"
        )
    | EveryTime, true ->
        ConditionalAccessPolicySessionControlsArgs(
            SignInFrequencyInterval = "everyTime",
            SignInFrequencyAuthenticationType = authType
        )
    | EveryTime, false ->
        ConditionalAccessPolicySessionControlsArgs(
            SignInFrequencyInterval = "everyTime",
            SignInFrequencyAuthenticationType = authType,
            PersistentBrowserMode = "never"
        )

/// A policy on cloud apps (`apps`), which the azuread provider manages.
let private caPolicy (groups: Map<GroupName, Group>) (apps: string list, p: CaPolicy) =
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
        | Guests ->
            ConditionalAccessPolicyConditionsUsersArgs(
                IncludedUsers = inputList [ input "GuestsOrExternalUsers" ],
                ExcludedUsers = inputList [ for id in p.excludedUsers -> input (EntraObjectId.string id) ]
            )

    let conditions =
        ConditionalAccessPolicyConditionsArgs(
            ClientAppTypes = inputList [ for t in p.clientAppTypes -> input t ],
            Applications =
                ConditionalAccessPolicyConditionsApplicationsArgs(
                    IncludedApplications = inputList [ for a in apps -> input a ]
                ),
            SignInRiskLevels = inputList [ for r in p.signInRisk -> input r ],
            Users = users
        )

    let state = caState p.state

    ConditionalAccessPolicy(
        p.name,
        match p.controls with
        | Grant g ->
            ConditionalAccessPolicyArgs(
                DisplayName = p.name,
                State = state,
                Conditions = conditions,
                GrantControls = grantArgs g
            )
        | Session session ->
            ConditionalAccessPolicyArgs(
                DisplayName = p.name,
                State = state,
                Conditions = conditions,
                SessionControls = sessionArgs session
            )
        | GrantAndSession(g, session) ->
            ConditionalAccessPolicyArgs(
                DisplayName = p.name,
                State = state,
                Conditions = conditions,
                GrantControls = grantArgs g,
                SessionControls = sessionArgs session
            )
    )

let deploy (scripts: Scripts) (plan: Plan) : Deployed =
    // The repos are public, so the addresses only ever exist as Pulumi secrets: hidden in diffs and state.
    let alertEmails = Output.CreateSecret(plan.alertEmails |> List.map Email.value)
    let groups = plan.groups |> List.map (fun name -> name, group name) |> Map.ofList

    let groupIds =
        Output
            .All(
                [|
                    for KeyValue(name, g) in groups -> io (g.ObjectId.Apply(fun id -> name, id))
                |]
            )
            .Apply(Map.ofSeq)

    let appPolicies, contextPolicies =
        plan.conditionalAccess
        |> List.partition (fun p ->
            match p.target with
            | Apps _ -> true
            | Context _ -> false)

    let appPolicyResources =
        appPolicies
        |> List.choose (fun p ->
            match p.target with
            | Apps apps -> Some(caPolicy groups (apps, p))
            | Context _ -> None)

    let contextCommands =
        contextPolicies
        |> List.choose (fun p ->
            match p.target with
            | Context id ->
                Some(contextPolicy scripts groupIds [ for r in appPolicyResources -> r :> Resource ] (id, p))
            | Apps _ -> None)

    let after = contextCommands |> List.map (fun c -> c :> Resource)

    let policies =
        plan.pim |> List.map (fun rule -> rule, pimPolicy groups alertEmails after rule)

    let rolePolicies =
        plan.directoryRoles
        |> List.map (fun pim -> pim, rolePolicy scripts.pimDirectoryRolePolicy alertEmails after pim)

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
        conditionalAccess = appPolicyResources
        contextPolicies = contextCommands
    }
