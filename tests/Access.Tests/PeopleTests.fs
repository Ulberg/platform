module Platform.Access.Tests.PeopleTests

open System.IO
open Expecto
open Platform.Access.People

let private valid =
    """
people:
  ann: { github: ann-gh, entra: 11111111-1111-1111-1111-111111111111 }
  bob: { github: bob-gh, entra: 22222222-2222-2222-2222-222222222222 }
groups:
  infra-developers: [ann]
  app-developers: [ann, bob]
repo-leads:
  platform: [ann]
  todo: [ann, bob]
emergency-access:
  - 33333333-3333-3333-3333-333333333333
  - 44444444-4444-4444-4444-444444444444
"""

let private parsed yaml =
    parse yaml
    |> Result.defaultWith (failtestf "expected the file to parse, got %A")

let private errors yaml =
    match parse yaml with
    | Ok _ -> failtest "expected the file to be rejected"
    | Error e -> e

let private hasError (text: string) (errors: string list) =
    Expect.exists errors (fun e -> e.Contains text) $"expected an error containing '{text}', got %A{errors}"

let private rejects (replace: string) (by: string) (expected: string) =
    valid.Replace(replace, by) |> errors |> hasError expected

[<Tests>]
let tests =
    testList
        "people.yaml"
        [
            test "a valid file parses into people, groups and leads" {
                let access = parsed valid
                Expect.equal (access.appDevelopers |> List.map Person.key) [ "ann"; "bob" ] "app developers"
                Expect.equal (access.platformLeads |> List.map (_.github >> GitHubHandle.value)) [ "ann-gh" ] "leads"
                Expect.equal access.emergencyAccess.Length 2 "emergency accounts"
            }

            test "every error is reported, not just the first" {
                let e =
                    valid
                        .Replace("app-developers: [ann, bob]", "app-developers: [carl]")
                        .Replace("todo: [ann, bob]", "todo: [dave]")
                    |> errors

                hasError "groups.app-developers: unknown person 'carl'" e
                hasError "repo-leads.todo: unknown person 'dave'" e
            }

            test "an entra value that isn't an object ID is rejected" {
                rejects
                    "22222222-2222-2222-2222-222222222222"
                    "bob@example.com"
                    "people.bob: 'entra': 'bob@example.com' is not an object ID"
            }

            test "a github handle must follow GitHub's rules" {
                rejects "github: bob-gh" "github: -bob" "people.bob: 'github': '-bob' is not valid"
            }

            test "a github handle can only belong to one person" {
                rejects "github: bob-gh" "github: Ann-GH" "'ann-gh' is used more than once"
            }

            test "a person without fields is rejected" {
                rejects
                    "  bob: { github: bob-gh, entra: 22222222-2222-2222-2222-222222222222 }"
                    "  bob:"
                    "people.bob: needs 'github' and 'entra'"
            }

            test "a repo without leads is rejected" {
                rejects "todo: [ann, bob]" "todo: []" "repo-leads.todo: needs at least one person"
            }

            test "the platform repo must have leads" {
                rejects "platform: [ann]" "other: [ann]" "repo-leads: missing 'platform'"
            }

            test "required groups must exist" {
                rejects "infra-developers: [ann]" "" "groups: missing 'infra-developers'"
            }

            test "a misspelled group is rejected, not ignored" {
                rejects
                    "app-developers: [ann, bob]"
                    "app-developers: [ann]\n  app-develpers: [bob]"
                    "groups: unknown group 'app-develpers'"
            }

            test "a misspelled section is rejected, not ignored" { rejects "repo-leads:" "repo-lead:" "not valid YAML" }

            test "two emergency-access accounts are required" {
                rejects "  - 44444444-4444-4444-4444-444444444444\n" "" "emergency-access: needs two accounts"
            }

            test "CODEOWNERS gives the whole repo to its leads" {
                let access = parsed valid
                let todo = access.repoLeads |> Map.toList |> List.map snd |> List.last
                Expect.stringEnds (codeowners todo) "\n* @ann-gh @bob-gh\n" "owners line"
            }

            test "the committed access/people.yaml and CODEOWNERS are valid" {
                let root = Path.Combine(__SOURCE_DIRECTORY__, "..", "..")
                let access = load (Path.Combine(root, "access", "people.yaml"))
                let committed = File.ReadAllText(Path.Combine(root, ".github", "CODEOWNERS"))
                Expect.equal committed (codeowners access.platformLeads) "platform CODEOWNERS matches people.yaml"
            }
        ]
