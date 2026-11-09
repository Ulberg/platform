/// Turns the GitHub part of a Desired.Plan into rulesets, collaborators and environments, and reads
/// each repo's CODEOWNERS for the check in Codeowners.fs. No decisions are made here.
module Platform.Access.GitHub

open System.IO
open System.Threading.Tasks
open Pulumi
open Pulumi.FSharp
open Pulumi.Github
open Pulumi.Github.Inputs
open Platform.Access.People
open Platform.Access.Desired

type Deployed =
    {
        rulesets: RepositoryRuleset list
        collaborators: RepositoryCollaborator list
        environments: (RepositoryEnvironment * RepositoryEnvironmentDeploymentPolicy) list
    }

/// GitHub Actions' app ID, so a required check can only be satisfied by our own workflows.
let private githubActions = 15368

/// D34, the solo-developer exception: repo admins (only the owner, Ulberg) may bypass the rules,
/// so they can push straight to main. No app gets a bypass. See the TODO in PLAN.md §13.
let private ownerBypass =
    RepositoryRulesetBypassActorArgs(ActorId = 5, ActorType = "RepositoryRole", BypassMode = "always")

let private ruleset (r: Ruleset) =
    let repo = RepoName.value r.repo

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
                            Includes = inputList [ input "~DEFAULT_BRANCH" ],
                            Excludes = inputList []
                        )
                ),
            BypassActors = inputList [ input ownerBypass ],
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
                                inputList
                                    [
                                        for c in r.requiredChecks ->
                                            input (
                                                RepositoryRulesetRulesRequiredStatusChecksRequiredCheckArgs(
                                                    Context = c,
                                                    IntegrationId = githubActions
                                                )
                                            )
                                    ]
                        )
                )
        )
    )

let private collaborator (c: Collaborator) =
    let repo = RepoName.value c.repo

    RepositoryCollaborator(
        $"{repo}-{Person.key c.person}",
        RepositoryCollaboratorArgs(
            Repository = repo,
            Username = GitHubHandle.value c.person.github,
            Permission = "push"
        )
    )

/// An existing environment, managed in place (PUT is an upsert). Protected, because deleting
/// an environment also deletes its secrets.
let private environment (e: Environment) =
    let repo = RepoName.value e.repo

    let reviewers =
        match e.reviewers with
        | [] -> inputList []
        | people ->
            let ids =
                people
                |> List.map (fun p ->
                    GetUser.Invoke(GetUserInvokeArgs(Username = GitHubHandle.value p.github)).Apply(fun u -> int u.Id))
                |> Output.All

            inputList [ input (RepositoryEnvironmentReviewerArgs(Users = ids)) ]

    let env =
        RepositoryEnvironment(
            $"{repo}-{e.name}",
            RepositoryEnvironmentArgs(
                Repository = repo,
                Environment = e.name,
                PreventSelfReview = false,
                CanAdminsBypass = false,
                Reviewers = reviewers,
                DeploymentBranchPolicy =
                    RepositoryEnvironmentDeploymentBranchPolicyArgs(
                        ProtectedBranches = false,
                        CustomBranchPolicies = true
                    )
            ),
            CustomResourceOptions(Protect = true, RetainOnDelete = true)
        )

    let branchPolicy =
        RepositoryEnvironmentDeploymentPolicy(
            $"{repo}-{e.name}-main",
            RepositoryEnvironmentDeploymentPolicyArgs(Repository = repo, Environment = e.name, BranchPattern = "main"),
            CustomResourceOptions(
                ImportId = $"{repo}:{e.name}:{e.branchPolicyId}",
                DependsOn = InputList.op_Implicit (env :> Resource),
                Protect = true,
                RetainOnDelete = true
            )
        )

    env, branchPolicy

let deploy (plan: Plan) : Deployed =
    {
        rulesets = plan.rulesets |> List.map ruleset
        collaborators = plan.collaborators |> List.map collaborator
        environments = plan.environments |> List.map environment
    }

/// Reads a repo's committed CODEOWNERS. The platform repo is read from the checkout, so a PR that
/// changes leads must change CODEOWNERS in the same PR; other repos are read from their `main`.
let readCodeowners (platformRoot: string) (repo: RepoName) : Task<string option> =
    if repo = RepoName.platform then
        let path = Path.Combine(platformRoot, ".github", "CODEOWNERS")

        Task.FromResult(
            if File.Exists path then
                Some(File.ReadAllText path)
            else
                None
        )
    else
        task {
            try
                let! file =
                    GetRepositoryFile.InvokeAsync(
                        GetRepositoryFileArgs(
                            Repository = RepoName.value repo,
                            File = ".github/CODEOWNERS",
                            Branch = "main"
                        )
                    )

                return Some file.Content
            with _ ->
                return None
        }
