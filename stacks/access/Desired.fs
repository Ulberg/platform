/// What access should exist, computed from people.yaml. Pure data and pure functions: no Pulumi, no I/O,
/// so every rule here is unit-tested. Entra.fs, AzureRbac.fs and GitHub.fs only turn a Plan into
/// resources (functional core, imperative shell).
module Platform.Access.Desired

open FsToolkit.ErrorHandling
open Platform.Access.People

// ---------------------------------------------------------------------------------------------
// Entra groups

type GroupName =
    | InfraDevelopers
    | AppDevelopers
    /// Standing read access.
    | PlatformRead
    /// Break-glass write. Only reachable by activating PIM.
    | PlatformWrite
    /// Admin. Only reachable by activating PIM, platform leads only.
    | PlatformAdmin
    /// Claude's agent accounts (D41): read roles only, never PIM-eligible.
    | PlatformAgents

module GroupName =
    let all =
        [
            InfraDevelopers
            AppDevelopers
            PlatformRead
            PlatformWrite
            PlatformAdmin
            PlatformAgents
        ]

    let value =
        function
        | InfraDevelopers -> "infra-developers"
        | AppDevelopers -> "app-developers"
        | PlatformRead -> "platform-read"
        | PlatformWrite -> "platform-write"
        | PlatformAdmin -> "platform-admin"
        | PlatformAgents -> "platform-agents"

    let description =
        function
        | InfraDevelopers -> "Infra developers (access/people.yaml). Standing read only."
        | AppDevelopers -> "App developers (access/people.yaml). App logs, metrics and traces."
        | PlatformRead -> "Read on Azure, AKS and the DB. Contains platform-write."
        | PlatformWrite -> "Break-glass write. PIM only (max 2 h). Contains platform-admin."
        | PlatformAdmin -> "Platform admin. PIM only (max 1 h), platform leads only."
        | PlatformAgents -> "Claude's agent accounts (access/people.yaml agents). Read only, never PIM-eligible."

/// Claude's own Entra account for one developer (D41). It reads what that developer can read day to day,
/// never holds or activates anything privileged, and its sign-ins show up separately in the audit logs.
type Agent = { owner: Person }

module Agent =
    /// e.g. "endre-agent"; also the account's user name.
    let key (a: Agent) = $"{Person.key a.owner}-agent"

    /// e.g. "Claude agent of endre (Ulberg)", so every log line names the person it acts for.
    let displayName (a: Agent) =
        $"Claude agent of {Person.key a.owner} ({GitHubHandle.value a.owner.github})"

[<RequireQualifiedAccess>]
type Member =
    | Person of Person
    | Agent of Agent
    | Group of GroupName

type Membership = { group: GroupName; includes: Member }

module Membership =
    /// Stable resource name, e.g. "infra-developers-endre" or "platform-write-in-platform-read".
    let resourceName m =
        match m.includes with
        | Member.Person p -> $"{GroupName.value m.group}-{Person.key p}"
        | Member.Agent a -> $"{GroupName.value m.group}-{Agent.key a}"
        | Member.Group inner -> $"{GroupName.value inner}-in-{GroupName.value m.group}"

/// What PIM asks for at activation, besides a justification.
type ActivationCheck =
    /// PIM's own MFA check. It accepts the MFA from an earlier sign-in, however old.
    | Mfa
    /// A Conditional Access authentication context: a fresh phishing-resistant sign-in every time (CA05, D39).
    | AuthenticationContext of id: string

/// The authentication context that privileged PIM activations require once Conditional Access is on.
let privilegedActivationContext = "c1"

/// PIM for Groups: membership of `group` is eligible only, activated with `activation` + justification.
type PimRule =
    {
        group: GroupName
        maxHours: int
        activation: ActivationCheck
        eligible: Person list
    }

// ---------------------------------------------------------------------------------------------
// Entra directory roles

/// Built-in Entra roles we assign. Nobody holds Global Admin standing (D35); only the emergency accounts
/// keep it, and those are set up by hand, not here.
type DirectoryRole =
    | GlobalAdministrator
    /// Read-only: sign-in and audit logs, Conditional Access and security settings.
    | SecurityReader

module DirectoryRole =
    let value =
        function
        | GlobalAdministrator -> "global-administrator"
        | SecurityReader -> "security-reader"

    /// Built-in role template ID, the same in every tenant.
    let templateId =
        function
        | GlobalAdministrator -> "62e90394-69f5-4237-9190-012177145e10"
        | SecurityReader -> "5d6b6bb7-de71-4623-b4af-96380a352509"

/// A standing, read-only directory role for an agent account.
type AgentDirectoryRole = { agent: Agent; role: DirectoryRole }

/// PIM for a directory role: eligible only, activated with `activation` + justification for at most `maxHours`.
type DirectoryRolePim =
    {
        role: DirectoryRole
        maxHours: int
        activation: ActivationCheck
        eligible: Person list
    }

// ---------------------------------------------------------------------------------------------
// Azure RBAC

type AzureRole =
    | Reader
    | MonitoringReader
    | LogAnalyticsReader
    | CostManagementReader
    | Owner

module AzureRole =
    let definitionId =
        function
        | Reader -> "acdd72a7-3385-48ef-bd42-f606fba81ae7"
        | MonitoringReader -> "43d0d8ad-25c7-4714-9337-8ba259a9fe05"
        | LogAnalyticsReader -> "73c42c96-874c-492b-b04d-ab87d138a893"
        | CostManagementReader -> "72fafb9e-0641-4937-9268-a91bfd8191a3"
        | Owner -> "8e3af657-a8ff-443c-a75c-2fe8c4bcb635"

    /// What Claude's agent accounts get on the subscription (D41): everything a platform developer reads
    /// day to day, and nothing that writes or reads data (no state, no secrets).
    let agentRoles =
        [ Reader; MonitoringReader; LogAnalyticsReader; CostManagementReader ]

[<RequireQualifiedAccess>]
type Grantee =
    | Group of GroupName
    /// An emergency-access account from people.yaml (D36).
    | EmergencyAccount of EntraObjectId

/// A role on the whole subscription. Write roles on app resource groups come in Phase 3.
type RoleGrant = { grantee: Grantee; role: AzureRole }

// ---------------------------------------------------------------------------------------------
// Conditional Access

type CaState =
    | ReportOnly
    | Enforced

module CaState =
    /// "off" | "report-only" | "on". Off means no policies at all (security defaults still on).
    let parse =
        function
        | "off" -> Ok None
        | "report-only" -> Ok(Some ReportOnly)
        | "on" -> Ok(Some Enforced)
        | other -> Error $"access:conditionalAccess must be off, report-only or on, not '{other}'"

type CaUsers =
    | AllUsers
    | Groups of GroupName list
    /// Guests and other external users, e.g. the owner Microsoft account.
    | Guests
    /// Everyone in the tenant except guests and external users.
    | Members

/// What a policy applies to: cloud apps, or an authentication context that something (PIM) asks for.
type CaTarget =
    | Apps of string list
    | Context of id: string

type CaGrant =
    | RequireMfa
    | Block
    /// An Entra authentication strength policy, e.g. phishing-resistant MFA.
    | RequireStrength of policyId: string

type SignInFrequency =
    | Hours of int
    /// A fresh sign-in for every request that hits the policy.
    | EveryTime

type CaSession =
    {
        signIn: SignInFrequency
        /// false: the browser never offers "stay signed in". Entra only allows that on policies for all apps.
        persistentBrowser: bool
    }

/// A policy needs a grant, a session control or both; "neither" can't be expressed.
type CaControls =
    | Grant of CaGrant
    | Session of CaSession
    | GrantAndSession of CaGrant * CaSession

type CaPolicy =
    {
        name: string
        state: CaState
        clientAppTypes: string list
        target: CaTarget
        users: CaUsers
        /// Emergency-access accounts, excluded from every policy.
        excludedUsers: EntraObjectId list
        signInRisk: string list
        controls: CaControls
    }

/// Windows Azure Service Management API (portal, az CLI, ARM).
let private azureManagementApp = "797f4846-ba00-4fd7-ba43-dac1f8f63013"

/// Built-in authentication strength "Phishing-resistant MFA" (passkeys, FIDO2, Windows Hello).
let private phishingResistant =
    "/policies/authenticationStrengthPolicies/00000000-0000-0000-0000-000000000004"

let private conditionalAccess (state: CaState option) (access: Access) =
    let policy name clientAppTypes target users signInRisk controls state =
        {
            name = name
            state = state
            clientAppTypes = clientAppTypes
            target = target
            users = users
            excludedUsers = access.emergencyAccess
            signInRisk = signInRisk
            controls = controls
        }

    let policies =
        [
            policy "CA01 Require MFA for everyone" [ "all" ] (Apps [ "All" ]) AllUsers [] (Grant RequireMfa)
            policy
                "CA02 Block legacy authentication"
                [ "exchangeActiveSync"; "other" ]
                (Apps [ "All" ])
                AllUsers
                []
                (Grant Block)
            policy
                "CA03 Phishing-resistant MFA for Azure management"
                [ "all" ]
                (Apps [ azureManagementApp ])
                // Not guests: the owner Microsoft account can't register a passkey here, and would lose
                // the portal and billing. Guests have no Azure access beyond billing; CA01 still needs MFA.
                Members
                []
                (Grant(RequireStrength phishingResistant))
            policy
                "CA04 Risky sign-ins by infra developers need MFA"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; PlatformAgents ])
                [ "medium"; "high" ]
                (Grant RequireMfa)
            // D39: PIM asks for this context, so every activation needs a fresh passkey sign-in.
            policy
                "CA05 Fresh phishing-resistant sign-in for every PIM activation"
                [ "all" ]
                (Context privilegedActivationContext)
                AllUsers
                []
                (GrantAndSession(
                    RequireStrength phishingResistant,
                    {
                        signIn = EveryTime
                        persistentBrowser = true
                    }
                ))
            policy
                "CA06 Infra developers sign in to Azure management every 4 h"
                [ "all" ]
                (Apps [ azureManagementApp ])
                (Groups [ InfraDevelopers; PlatformAgents ])
                []
                (Session
                    {
                        signIn = Hours 4
                        persistentBrowser = true
                    })
            policy
                "CA07 Developers sign in every 12 h"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; AppDevelopers; PlatformAgents ])
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = true
                    })
            // D39: guests (the owner account keeps billing) sign in again every 12 h too.
            policy
                "CA09 Guests sign in every 12 h"
                [ "all" ]
                (Apps [ "All" ])
                Guests
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = true
                    })
            // Part of D39 for the Azure tools: Entra only allows "never stay signed in" on all apps.
            policy
                "CA08 Infra developers never stay signed in"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; PlatformAgents ])
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = false
                    })
        ]

    match state with
    | None -> []
    | Some state -> policies |> List.map (fun p -> p state)

// ---------------------------------------------------------------------------------------------
// GitHub

/// Status checks (job names) a PR must pass, per repo.
let private requiredChecks =
    Map
        [
            "platform",
            [
                "secret scan (gitleaks)"
                "prompt log"
                "dotnet (format, build, test)"
                "pulumi preview"
            ]
            "todo", [ "secret scan (gitleaks)"; "prompt log"; "fsharp style" ]
        ]

type Ruleset =
    {
        repo: RepoName
        requiredChecks: string list
    }

type Collaborator = { repo: RepoName; person: Person }

/// An environment that already exists in GitHub. `branchPolicyId` is its existing `main` branch
/// policy, which is imported rather than created.
type Environment =
    {
        repo: RepoName
        name: string
        branchPolicyId: int
        reviewers: Person list
    }

// ---------------------------------------------------------------------------------------------
// The plan

type Plan =
    {
        groups: GroupName list
        memberships: Membership list
        pim: PimRule list
        directoryRoles: DirectoryRolePim list
        roleAssignments: RoleGrant list
        conditionalAccess: CaPolicy list
        rulesets: Ruleset list
        collaborators: Collaborator list
        environments: Environment list
        /// Who gets PIM emails: every activation and every new assignment of a privileged role (D40).
        alertEmails: Email list
        /// Claude's agent accounts (D41) and the tenant domain their user names live in.
        agents: Agent list
        agentDirectoryRoles: AgentDirectoryRole list
        tenantDomain: string
    }

let private memberships (access: Access) =
    let people group (persons: Person list) =
        persons
        |> List.map (fun p ->
            {
                group = group
                includes = Member.Person p
            })

    let agents =
        access.agents
        |> List.map (fun p ->
            {
                group = PlatformAgents
                includes = Member.Agent { owner = p }
            })

    people InfraDevelopers access.infraDevelopers
    @ people AppDevelopers access.appDevelopers
    @ agents
    // read ⊃ write ⊃ admin, so each consumer only maps three levels.
    @ [
        {
            group = PlatformRead
            includes = Member.Group InfraDevelopers
        }
        {
            group = PlatformRead
            includes = Member.Group PlatformWrite
        }
        {
            group = PlatformWrite
            includes = Member.Group PlatformAdmin
        }
    ]

let private rulesets (access: Access) =
    access.repoLeads
    |> Map.keys
    |> List.ofSeq
    |> List.traverseResultA (fun repo ->
        requiredChecks
        |> Map.tryFind (RepoName.value repo)
        |> Result.requireSome $"no required checks defined for repo '{RepoName.value repo}'"
        |> Result.map (fun checks -> { repo = repo; requiredChecks = checks }))

/// Everyone who works in a repo, except the owner (who already has full access to their own repos).
let private collaborators (owner: GitHubHandle) (access: Access) =
    [
        for KeyValue(repo, leads) in access.repoLeads do
            let developers =
                if repo = RepoName.platform then
                    access.infraDevelopers
                else
                    access.infraDevelopers @ access.appDevelopers

            for person in leads @ developers |> List.distinct do
                if not (GitHubHandle.same person.github owner) then
                    { repo = repo; person = person }
    ]

let plan
    (owner: GitHubHandle)
    (caState: CaState option)
    (alertEmails: Email list)
    (tenantDomain: string)
    (access: Access)
    : Result<Plan, string list> =

    // PIM requires the authentication context only once CA05 is enforced. In report-only, CA05 checks
    // nothing, so PIM keeps its own MFA check.
    let activation =
        match caState with
        | Some Enforced -> AuthenticationContext privilegedActivationContext
        | Some ReportOnly
        | None -> Mfa

    result {
        let! rulesets = rulesets access
        let! todo = RepoName.parse "todo" |> Result.mapError List.singleton

        return
            {
                groups = GroupName.all
                memberships = memberships access
                pim =
                    [
                        {
                            group = PlatformWrite
                            maxHours = 2
                            activation = activation
                            eligible = access.infraDevelopers
                        }
                        {
                            group = PlatformAdmin
                            maxHours = 1
                            activation = activation
                            eligible = access.platformLeads
                        }
                    ]
                directoryRoles =
                    [
                        {
                            role = GlobalAdministrator
                            maxHours = 1
                            activation = activation
                            eligible = access.platformLeads
                        }
                    ]
                // Subscription Owner only through the emergency accounts (D36); nobody else holds it standing.
                roleAssignments =
                    {
                        grantee = Grantee.Group PlatformRead
                        role = Reader
                    }
                    :: [
                        for role in AzureRole.agentRoles ->
                            {
                                grantee = Grantee.Group PlatformAgents
                                role = role
                            }
                    ]
                    @ [
                        for id in access.emergencyAccess ->
                            {
                                grantee = Grantee.EmergencyAccount id
                                role = Owner
                            }
                    ]
                conditionalAccess = conditionalAccess caState access
                rulesets = rulesets
                collaborators = collaborators owner access
                // Applies need a platform lead's approval; app releases (the pr-bot) run from main only.
                environments =
                    [
                        {
                            repo = RepoName.platform
                            name = "dev"
                            branchPolicyId = 65981291
                            reviewers = access.platformLeads
                        }
                        {
                            repo = todo
                            name = "release"
                            branchPolicyId = 65981293
                            reviewers = []
                        }
                    ]
                alertEmails = alertEmails
                agents = access.agents |> List.map (fun p -> { owner = p })
                agentDirectoryRoles =
                    [
                        for p in access.agents ->
                            {
                                agent = { owner = p }
                                role = SecurityReader
                            }
                    ]
                tenantDomain = tenantDomain
            }
    }
