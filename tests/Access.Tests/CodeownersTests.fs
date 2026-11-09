module Platform.Access.Tests.CodeownersTests

open System.IO
open Expecto
open Platform.Access
open Platform.Access.Tests.Fixtures

[<Tests>]
let tests =
    let access = parsed valid
    let todoLeads = access.repoLeads[repo "todo"]

    testList
        "CODEOWNERS"
        [
            test "gives the whole repo to its leads" {
                Expect.stringEnds (Codeowners.expected todoLeads) "\n* @ann-gh @bob-gh\n" "owners line"
            }

            test "a matching file passes" {
                Expect.equal
                    (Codeowners.check [ repo "todo", todoLeads, Some(Codeowners.expected todoLeads) ])
                    (Ok())
                    "ok"
            }

            test "a missing or different file names the repo and the expected content" {
                let result =
                    Codeowners.check
                        [
                            repo "todo", todoLeads, None
                            repo "platform", access.platformLeads, Some "* @someone"
                        ]

                match result with
                | Error [ todo; platform ] ->
                    Expect.stringStarts todo "todo: .github/CODEOWNERS must be exactly" "todo"
                    Expect.stringStarts platform "platform:" "platform"
                | other -> failtestf "expected two errors, got %A" other
            }

            test "the committed access/people.yaml and platform CODEOWNERS agree" {
                let access =
                    File.ReadAllText(Path.Combine(platformRoot, "access", "people.yaml")) |> parsed

                let committed =
                    File.ReadAllText(Path.Combine(platformRoot, ".github", "CODEOWNERS"))

                Expect.equal committed (Codeowners.expected access.platformLeads) "platform CODEOWNERS"
            }
        ]
