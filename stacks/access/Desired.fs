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

module GroupName =
    let all =
        [ InfraDevelopers; AppDevelopers; PlatformRead; PlatformWrite; PlatformAdmin ]

    let value =
        function
        | InfraDevelopers -> "infra-developers"
        | AppDevelopers -> "app-developers"
        | PlatformRead -> "platform-read"
        | PlatformWrite -> "platform-write"
        | PlatformAdmin -> "platform-admin"

    let description =
        function
        | InfraDevelopers -> "Infra developers (access/people.yaml). Standing read only."
        | AppDevelopers -> "App developers (access/people.yaml). App logs, metrics and traces."
        | PlatformRead -> "Read on Azure, AKS and the DB. Contains platform-write."
        | PlatformWrite -> "Break-glass write. PIM only (max 2 h). Contains platform-admin."
        | PlatformAdmin -> "Platform admin. PIM only (max 1 h), platform leads only."

[<RequireQualifiedAccess>]
type Member =
    | Person of Person
    | Group of GroupName

type Membership = { group: GroupName; includes: Member }

module Membership =
    /// Stable resource name, e.g. "infra-developers-endre" or "platform-write-in-platform-read".
    let resourceName m =
        match m.includes with
        | Member.Person p -> $"{GroupName.value m.group}-{Person.key p}"
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

/// Built-in Entra roles that nobody holds standing (D35). Only the emergency accounts keep a
/// standing Global Admin, and those are set up by hand, not here.
type DirectoryRole = | GlobalAdministrator

module DirectoryRole =
    let value =
        function
        | GlobalAdministrator -> "global-administrator"

    /// Built-in role template ID, the same in every tenant.
    let templateId =
        function
        | GlobalAdministrator -> "62e90394-69f5-4237-9190-012177145e10"

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
    | Owner

module AzureRole =
    let definitionId =
        function
        | Reader -> "acdd72a7-3385-48ef-bd42-f606fba81ae7"
        | Owner -> "8e3af657-a8ff-443c-a75c-2fe8c4bcb635"

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
        /// false: the browser never offers "stay signed in".
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
                AllUsers
                []
                (Grant(RequireStrength phishingResistant))
            policy
                "CA04 Risky sign-ins by infra developers need MFA"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers ])
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
                (Groups [ InfraDevelopers ])
                []
                (Session
                    {
                        signIn = Hours 4
                        persistentBrowser = false
                    })
            policy
                "CA07 Developers sign in every 12 h"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; AppDevelopers ])
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = true
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
    }

let private memberships (access: Access) =
    let people group (persons: Person list) =
        persons
        |> List.map (fun p ->
            {
                group = group
                includes = Member.Person p
            })

    people InfraDevelopers access.infraDevelopers
    @ people AppDevelopers access.appDevelopers
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
            }
    }
