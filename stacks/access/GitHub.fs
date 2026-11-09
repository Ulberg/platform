/// GitHub repo rules, environments and collaborators for every repo in repo-leads (PLAN.md §2).
module Platform.Access.GitHub

open System.IO
open System.Threading.Tasks
open Pulumi
open Pulumi.Github
open Pulumi.Github.Inputs
open Platform.Access.People

/// GitHub Actions' app ID, so a required check can only be satisfied by our own workflows.
let private githubActions = 15368

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
            "todo", [ "secret scan (gitleaks)"; "prompt log" ]
        ]

/// D34, the solo-developer exception: repo admins (only the owner, Ulberg) may bypass the rules,
/// so they can push straight to main. No app gets a bypass. See the TODO in PLAN.md §13.
let private ownerBypass =
    RepositoryRulesetBypassActorArgs(ActorId = 5, ActorType = "RepositoryRole", BypassMode = "always")

let private ruleset (repo: string) =
    let checks =
        requiredChecks
        |> Map.tryFind repo
        |> Option.defaultWith (fun () -> failwith $"No required checks defined for repo '{repo}'")

    RepositoryRuleset(
        $"{repo}-main",
        RepositoryRulesetArgs(
            Repository = repo,
            Name = "main",
            Target = "branch",
            Enforcement = "active",
            Conditions =
                RepositoryRulesetConditionsArgs(
                    RefName =
                        RepositoryRulesetConditionsRefNameArgs(
                            Includes = InputList.op_Implicit [| "~DEFAULT_BRANCH" |],
                            Excludes = InputList<string>()
                        )
                ),
            BypassActors = InputList.op_Implicit [| ownerBypass |],
            Rules =
                RepositoryRulesetRulesArgs(
                    Deletion = true,
                    NonFastForward = true,
                    RequiredSignatures = true,
                    PullRequest =
                        RepositoryRulesetRulesPullRequestArgs(
                            RequiredApprovingReviewCount = 0,
                            RequireCodeOwnerReview = true,
                            DismissStaleReviewsOnPush = true,
                            RequiredReviewThreadResolution = true
                        ),
                    RequiredStatusChecks =
                        RepositoryRulesetRulesRequiredStatusChecksArgs(
                            StrictRequiredStatusChecksPolicy = false,
                            RequiredChecks =
                                InputList.op_Implicit (
                                    [|
                                        for c in checks ->
                                            RepositoryRulesetRulesRequiredStatusChecksRequiredCheckArgs(
                                                Context = c,
                                                IntegrationId = githubActions
                                            )
                                    |]
                                )
                        )
                )
        )
    )
    |> ignore

let private collaborators (owner: string) (repo: string) (people: Person list) =
    for p in people |> List.distinct |> List.filter (fun p -> p.GitHub <> owner) do
        RepositoryCollaborator(
            $"{repo}-{p.Key}",
            RepositoryCollaboratorArgs(Repository = repo, Username = p.GitHub, Permission = "push")
        )
        |> ignore

/// An existing environment, managed in place (PUT is an upsert). Protected, because deleting
/// an environment also deletes its secrets.
let private environment (repo: string) (name: string) (branchPolicyId: int) (reviewers: Person list) =
    let reviewerIds =
        reviewers
        |> List.map (fun p -> GetUser.Invoke(GetUserInvokeArgs(Username = p.GitHub)).Apply(fun u -> int u.Id))
        |> Output.All

    let env =
        RepositoryEnvironment(
            $"{repo}-{name}",
            RepositoryEnvironmentArgs(
                Repository = repo,
                Environment = name,
                PreventSelfReview = false,
                CanAdminsBypass = false,
                Reviewers =
                    (if List.isEmpty reviewers then
                         InputList<RepositoryEnvironmentReviewerArgs>()
                     else
                         InputList.op_Implicit [| RepositoryEnvironmentReviewerArgs(Users = reviewerIds) |]),
                DeploymentBranchPolicy =
                    RepositoryEnvironmentDeploymentBranchPolicyArgs(
                        ProtectedBranches = false,
                        CustomBranchPolicies = true
                    )
            ),
            CustomResourceOptions(Protect = true, RetainOnDelete = true)
        )

    RepositoryEnvironmentDeploymentPolicy(
        $"{repo}-{name}-main",
        RepositoryEnvironmentDeploymentPolicyArgs(Repository = repo, Environment = name, BranchPattern = "main"),
        CustomResourceOptions(
            ImportId = $"{repo}:{name}:{branchPolicyId}",
            DependsOn = InputList.op_Implicit (env :> Resource),
            Protect = true,
            RetainOnDelete = true
        )
    )
    |> ignore

let repos (owner: string) (access: Access) =
    for KeyValue(repo, leads) in access.RepoLeads do
        ruleset repo

        let developers =
            if repo = "platform" then
                access.InfraDevelopers
            else
                access.InfraDevelopers @ access.AppDevelopers

        collaborators owner repo (leads @ developers)

    // Applies need a platform lead's approval; app releases (the pr-bot) run from main only.
    environment "platform" "dev" 65981291 access.PlatformLeads
    environment "todo" "release" 65981293 []

/// Fails the run if a repo's CODEOWNERS doesn't match its leads. Committed files rather than a bot push,
/// because a bot pushing to main would need a ruleset bypass. The platform repo is read from the
/// checkout, so a PR that changes leads must change CODEOWNERS in the same PR.
let checkCodeowners (platformRoot: string) (access: Access) : Task<unit> =
    task {
        let mutable errors = []

        for KeyValue(repo, leads) in access.RepoLeads do
            let expected = codeowners leads

            let! actual =
                if repo = "platform" then
                    Task.FromResult(Some(File.ReadAllText(Path.Combine(platformRoot, ".github", "CODEOWNERS"))))
                else
                    task {
                        try
                            let! file =
                                GetRepositoryFile.InvokeAsync(
                                    GetRepositoryFileArgs(
                                        Repository = repo,
                                        File = ".github/CODEOWNERS",
                                        Branch = "main"
                                    )
                                )

                            return Some file.Content
                        with _ ->
                            return None
                    }

            if actual <> Some expected then
                errors <- $"{repo}: .github/CODEOWNERS must be exactly:\n{expected}" :: errors

        if not (List.isEmpty errors) then
            failwith (String.concat "\n" errors)
    }
