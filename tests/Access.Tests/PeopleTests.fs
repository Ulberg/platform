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

let private errors yaml =
    match parse yaml with
    | Ok _ -> failtest "expected the file to be rejected"
    | Error e -> e

let private hasError (text: string) (errors: string list) =
    Expect.exists errors (fun e -> e.Contains text) $"expected an error containing '{text}', got %A{errors}"

[<Tests>]
let tests =
    testList
        "people.yaml"
        [
            test "a valid file parses into people, groups and leads" {
                let access = parse valid |> Result.defaultWith (fun e -> failtestf "%A" e)
                Expect.equal (access.AppDevelopers |> List.map _.Key) [ "ann"; "bob" ] "app developers"
                Expect.equal (access.PlatformLeads |> List.map _.GitHub) [ "ann-gh" ] "platform leads"
                Expect.equal access.EmergencyAccess.Length 2 "emergency accounts"
            }

            test "an unknown person in a group is rejected" {
                valid.Replace("app-developers: [ann, bob]", "app-developers: [ann, carl]")
                |> errors
                |> hasError "groups.app-developers: unknown person 'carl'"
            }

            test "every error is reported, not just the first" {
                let e =
                    valid
                        .Replace("app-developers: [ann, bob]", "app-developers: [carl]")
                        .Replace("todo: [ann, bob]", "todo: [dave]")
                    |> errors

                hasError "carl" e
                hasError "dave" e
            }

            test "an entra value that isn't an object ID is rejected" {
                valid.Replace("22222222-2222-2222-2222-222222222222", "bob@example.com")
                |> errors
                |> hasError "bob: 'bob@example.com' is not an object ID"
            }

            test "a repo without leads is rejected" {
                valid.Replace("todo: [ann, bob]", "todo: []")
                |> errors
                |> hasError "repo-leads.todo: needs at least one person"
            }

            test "the platform repo must have leads" {
                valid.Replace("platform: [ann]", "other: [ann]")
                |> errors
                |> hasError "repo-leads: missing 'platform'"
            }

            test "required groups must exist" {
                valid.Replace("infra-developers: [ann]", "")
                |> errors
                |> hasError "groups: missing 'infra-developers'"
            }

            test "two emergency-access accounts are required" {
                valid.Replace("  - 44444444-4444-4444-4444-444444444444\n", "")
                |> errors
                |> hasError "emergency-access: needs two accounts"
            }

            test "a github handle can only belong to one person" {
                valid.Replace("github: bob-gh", "github: Ann-GH")
                |> errors
                |> hasError "'ann-gh' is used more than once"
            }

            test "CODEOWNERS gives the whole repo to its leads" {
                let access = parse valid |> Result.defaultWith (fun e -> failtestf "%A" e)

                Expect.stringEnds (codeowners access.RepoLeads["todo"]) "\n* @ann-gh @bob-gh\n" "owners line"
            }

            test "the committed access/people.yaml and CODEOWNERS are valid" {
                let root = Path.Combine(__SOURCE_DIRECTORY__, "..", "..")
                let access = load (Path.Combine(root, "access", "people.yaml"))
                let committed = File.ReadAllText(Path.Combine(root, ".github", "CODEOWNERS"))
                Expect.equal committed (codeowners access.PlatformLeads) "platform CODEOWNERS matches people.yaml"
            }
        ]
