module Platform.Access.Tests.DesiredTests

open Expecto
open Platform.Access
open Platform.Access.People
open Platform.Access.Desired
open Platform.Access.Tests.Fixtures

let private planWith caState =
    Desired.plan (github "owner-gh") caState alertEmails (parsed valid) |> ok

let private keys (people: Person list) = people |> List.map Person.key

[<Tests>]
let tests =
    testList
        "plan"
        [
            test "groups nest read ⊃ write ⊃ admin, and infra developers get standing read" {
                let nesting =
                    (planWith None).memberships
                    |> List.choose (fun m ->
                        match m.includes with
                        | Member.Group inner -> Some(m.group, inner)
                        | Member.Person _ -> None)

                Expect.equal
                    nesting
                    [
                        PlatformRead, InfraDevelopers
                        PlatformRead, PlatformWrite
                        PlatformWrite, PlatformAdmin
                    ]
                    "nesting"
            }

            test "people are members of exactly the groups people.yaml lists" {
                let people =
                    (planWith None).memberships
                    |> List.choose (fun m ->
                        match m.includes with
                        | Member.Person p -> Some(GroupName.value m.group, Person.key p)
                        | Member.Group _ -> None)

                Expect.equal
                    people
                    [ "infra-developers", "ann"; "app-developers", "ann"; "app-developers", "bob" ]
                    "memberships"
            }

            test "write is PIM-only for infra developers (2 h); admin for platform leads (1 h)" {
                let pim =
                    (planWith None).pim |> List.map (fun r -> r.group, r.maxHours, keys r.eligible)

                Expect.equal pim [ PlatformWrite, 2, [ "ann" ]; PlatformAdmin, 1, [ "ann" ] ] "PIM rules"
            }

            test "Global Admin is PIM-only for platform leads (1 h)" {
                let roles =
                    (planWith None).directoryRoles
                    |> List.map (fun r -> r.role, r.maxHours, keys r.eligible)

                Expect.equal roles [ GlobalAdministrator, 1, [ "ann" ] ] "directory roles"
            }

            test "standing Azure roles: Reader for platform-read, Owner only for the emergency accounts" {
                let roles =
                    (planWith None).roleAssignments
                    |> List.map (fun g ->
                        match g.grantee with
                        | Grantee.Group group -> GroupName.value group, g.role
                        | Grantee.EmergencyAccount id -> EntraObjectId.string id, g.role)

                Expect.equal
                    roles
                    [
                        "platform-read", Reader
                        "33333333-3333-3333-3333-333333333333", Owner
                        "44444444-4444-4444-4444-444444444444", Owner
                    ]
                    "roles"
            }

            test "Conditional Access off means no policies" {
                Expect.isEmpty (planWith None).conditionalAccess "no policies"
            }

            test "every Conditional Access policy excludes both emergency accounts" {
                let policies = (planWith (Some ReportOnly)).conditionalAccess
                Expect.hasLength policies 8 "eight policies"

                for p in policies do
                    Expect.equal p.state ReportOnly p.name
                    Expect.hasLength p.excludedUsers 2 p.name
            }

            test "PIM activation needs the authentication context only once Conditional Access is on" {
                let activations caState =
                    let plan = planWith caState

                    (plan.pim |> List.map _.activation)
                    @ (plan.directoryRoles |> List.map _.activation)
                    |> List.distinct

                Expect.equal (activations None) [ Mfa ] "off"
                Expect.equal (activations (Some ReportOnly)) [ Mfa ] "report-only checks nothing"
                Expect.equal (activations (Some Enforced)) [ AuthenticationContext "c1" ] "on"
            }

            test "sessions: a fresh sign-in for every activation, 4 h for Azure management, 12 h otherwise (D39)" {
                let policies = (planWith (Some Enforced)).conditionalAccess

                let find (prefix: string) =
                    policies |> List.find (fun p -> p.name.StartsWith prefix)

                Expect.equal (find "CA05").target (Context "c1") "CA05 targets the PIM context"

                match (find "CA05").controls with
                | GrantAndSession(RequireStrength _, { signIn = EveryTime }) -> ()
                | other -> failtest $"CA05: %A{other}"

                Expect.equal
                    (find "CA06").controls
                    (Session
                        {
                            signIn = Hours 4
                            persistentBrowser = true
                        })
                    "CA06"

                Expect.equal (find "CA06").users (Groups [ InfraDevelopers ]) "CA06 users"

                Expect.equal
                    (find "CA08").controls
                    (Session
                        {
                            signIn = Hours 12
                            persistentBrowser = false
                        })
                    "CA08: infra developers never stay signed in"

                Expect.equal (find "CA08").users (Groups [ InfraDevelopers ]) "CA08 users"

                Expect.equal
                    (find "CA07").controls
                    (Session
                        {
                            signIn = Hours 12
                            persistentBrowser = true
                        })
                    "CA07"

                Expect.equal (find "CA07").users (Groups [ InfraDevelopers; AppDevelopers ]) "CA07 users"
            }

            test "\"never stay signed in\" is only used on policies for all apps (Entra rejects it otherwise)" {
                for p in (planWith (Some Enforced)).conditionalAccess do
                    match p.controls with
                    | Session { persistentBrowser = false }
                    | GrantAndSession(_, { persistentBrowser = false }) -> Expect.equal p.target (Apps [ "All" ]) p.name
                    | _ -> ()
            }

            test "the CA05 Graph body targets the context and asks for phishing-resistant MFA every time" {
                let ca05 =
                    (planWith (Some ReportOnly)).conditionalAccess
                    |> List.find (fun p -> p.name.StartsWith "CA05")

                let body =
                    System.Text.Json.JsonDocument.Parse(Entra.contextPolicyBody Map.empty "c1" ca05).RootElement

                let at (path: string list) =
                    path
                    |> List.fold (fun (e: System.Text.Json.JsonElement) name -> e.GetProperty name) body

                Expect.equal ((at [ "state" ]).GetString()) "enabledForReportingButNotEnforced" "report-only"

                Expect.equal
                    ((at [ "conditions"; "applications"; "includeAuthenticationContextClassReferences" ]).ToString())
                    """["c1"]"""
                    "context"

                Expect.equal
                    ((at [ "grantControls"; "authenticationStrength"; "id" ]).GetString())
                    "00000000-0000-0000-0000-000000000004"
                    "phishing-resistant"

                Expect.equal
                    ((at [ "sessionControls"; "signInFrequency"; "frequencyInterval" ]).GetString())
                    "everyTime"
                    "every time"

                Expect.equal ((at [ "conditions"; "users"; "excludeUsers" ]).GetArrayLength()) 2 "emergency accounts"

                Expect.isFalse
                    (body.GetProperty("sessionControls").TryGetProperty "persistentBrowser" |> fst)
                    "no browser mode"
            }

            test "the owner is never added as a collaborator" {
                let plan = Desired.plan (github "Ann-GH") None alertEmails (parsed valid) |> ok

                let collaborators =
                    plan.collaborators
                    |> List.map (fun c -> RepoName.value c.repo, Person.key c.person)

                Expect.equal collaborators [ "todo", "bob" ] "only bob, only on todo"
            }

            test "a repo without required checks is an error, not a ruleset without checks" {
                let yaml = valid.Replace("todo: [ann, bob]", "todo: [ann, bob]\n  other: [ann]")
                let plan = Desired.plan (github "owner-gh") None alertEmails (parsed yaml)
                Expect.equal plan (Error [ "no required checks defined for repo 'other'" ]) "missing checks"
            }

            test "conditionalAccess config only accepts off, report-only or on" {
                Expect.equal (CaState.parse "on") (Ok(Some Enforced)) "on"
                Expect.isError (CaState.parse "yes") "yes"
            }
        ]
