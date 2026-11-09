module Platform.Access.Tests.DesiredTests

open Expecto
open Platform.Access
open Platform.Access.People
open Platform.Access.Desired
open Platform.Access.Tests.Fixtures

let private planWith caState =
    Desired.plan (github "owner-gh") caState (parsed valid) |> ok

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
                Expect.hasLength policies 4 "four policies"

                for p in policies do
                    Expect.equal p.state ReportOnly p.name
                    Expect.hasLength p.excludedUsers 2 p.name
            }

            test "the owner is never added as a collaborator" {
                let plan = Desired.plan (github "Ann-GH") None (parsed valid) |> ok

                let collaborators =
                    plan.collaborators
                    |> List.map (fun c -> RepoName.value c.repo, Person.key c.person)

                Expect.equal collaborators [ "todo", "bob" ] "only bob, only on todo"
            }

            test "a repo without required checks is an error, not a ruleset without checks" {
                let yaml = valid.Replace("todo: [ann, bob]", "todo: [ann, bob]\n  other: [ann]")
                let plan = Desired.plan (github "owner-gh") None (parsed yaml)
                Expect.equal plan (Error [ "no required checks defined for repo 'other'" ]) "missing checks"
            }

            test "conditionalAccess config only accepts off, report-only or on" {
                Expect.equal (CaState.parse "on") (Ok(Some Enforced)) "on"
                Expect.isError (CaState.parse "yes") "yes"
            }
        ]
