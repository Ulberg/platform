module Platform.Access.Tests.Fixtures

open System.IO
open Expecto
open Platform.Access.People

let valid =
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

let parsed yaml =
    parse yaml
    |> Result.defaultWith (failtestf "expected the file to parse, got %A")

let ok result =
    result |> Result.defaultWith (failtestf "expected Ok, got %A")

let repo name = RepoName.parse name |> ok
let github handle = GitHubHandle.parse handle |> ok

let platformRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..")

let alertEmails =
    Email.parseList "alerts" (Some "lead@example.com, second@example.org") |> ok
