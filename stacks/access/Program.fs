/// The stack as a pipeline: input data → domain model → plan → action → report.
/// Only this file and the deploy functions have effects; everything they act on is computed by pure code.
module Platform.Access.Program

open System.IO
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Pulumi
open Platform.Access.People
open Platform.Access.Desired

/// Logs every problem and fails the run without a stack trace.
let private fail (errors: string list) : 'a =
    errors |> List.iter (fun e -> Log.Error e)
    raise (RunException $"access: {List.length errors} problem(s), see the errors above")

let private run () =
    task {
        // 1. Input data
        let config = Config "access"

        let platformRoot =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."))

        let peopleYaml =
            File.ReadAllText(Path.Combine(platformRoot, "access", "people.yaml"))

        let script name =
            File.ReadAllText(Path.Combine(platformRoot, "stacks", "access", "scripts", name))

        let scripts: Entra.Scripts =
            {
                pimPolicyRules = script "pim-policy-rules.sh"
                caContextPolicy = script "ca-context-policy.sh"
                caContextPolicyDelete = script "ca-context-policy-delete.sh"
                groupLicence = script "group-licence.sh"
                groupLicenceDelete = script "group-licence-delete.sh"
            }

        let subscriptionId = Config("azure-native").Require "subscriptionId"

        // A GitHub secret, because the repo is public. Option.ofObj: .NET returns null for an unset variable.
        let alertEmails =
            System.Environment.GetEnvironmentVariable "PLATFORM_ALERT_EMAILS"
            |> Option.ofObj

        // 2. Domain model and plan (pure)
        let planned =
            result {
                let! access = People.parse peopleYaml

                let! owner =
                    Config("github").Require "owner"
                    |> GitHubHandle.parse
                    |> Result.mapError (fun e -> [ $"github:owner: {e}" ])

                let! caState =
                    config.Require "conditionalAccess"
                    |> CaState.parse
                    |> Result.mapError List.singleton

                let! emails = Email.parseList "PLATFORM_ALERT_EMAILS" alertEmails

                let! tenantDomain =
                    config.Require "tenantDomain"
                    |> Parse.notEmpty "access:tenantDomain"
                    |> Result.mapError List.singleton

                let! plan = Desired.plan owner caState emails tenantDomain access
                return access, plan
            }

        match planned with
        | Error errors -> return fail errors
        | Ok(access, plan) ->
            let! committed =
                access.repoLeads
                |> Map.toList
                |> List.map (fun (repo, leads) ->
                    task {
                        let! actual = GitHub.readCodeowners platformRoot repo
                        return repo, leads, actual
                    })
                |> Task.WhenAll

            match Codeowners.check (List.ofArray committed) with
            | Error errors -> return fail errors
            | Ok() ->
                // 3. Action
                let entra = Entra.deploy scripts plan
                let roles = AzureRbac.deploy subscriptionId entra.groups plan
                let github = GitHub.deploy plan

                // 4. Report
                Log.Info(
                    $"access: {entra.groups.Count} groups, {entra.members.Length} memberships, "
                    + $"{entra.eligibilities.Length} PIM group eligibilities, "
                    + $"{entra.directoryRoleEligibilities.Length} PIM directory-role eligibilities "
                    + $"({entra.directoryRolePolicies.Length} PIM policies), {roles.Length} Azure role assignments, "
                    + $"{entra.conditionalAccess.Length + entra.contextPolicies.Length} Conditional Access policies, {github.rulesets.Length} rulesets, "
                    + $"{github.collaborators.Length} collaborators, {github.environments.Length} environments, "
                    + $"{entra.agents.Length} agent accounts"
                )

                return
                    entra.groups
                    |> Map.toList
                    |> List.map (fun (name, group) -> GroupName.value name, group.ObjectId :> obj)
                    |> dict
    }

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(run).GetAwaiter().GetResult()
