/// Every repo's committed CODEOWNERS must give the whole repo to its leads. It is committed by hand,
/// not pushed by a bot, because a bot pushing to main would need a ruleset bypass.
module Platform.Access.Codeowners

open Platform.Access.People

/// The file every repo must contain.
let expected (leads: Person list) =
    let owners =
        leads
        |> List.map (fun p -> "@" + GitHubHandle.value p.github)
        |> String.concat " "

    $"# Generated from Ulberg/platform access/people.yaml (repo-leads). Change it there, not here.\n* {owners}\n"

/// Compares what each repo has committed with what it should contain. Pure: the reading is done by the caller.
let check (committed: (RepoName * Person list * string option) list) : Result<unit, string list> =
    committed
    |> List.choose (fun (repo, leads, actual) ->
        let want = expected leads

        if actual = Some want then
            None
        else
            Some $"{RepoName.value repo}: .github/CODEOWNERS must be exactly:\n{want}")
    |> function
        | [] -> Ok()
        | errors -> Error errors
