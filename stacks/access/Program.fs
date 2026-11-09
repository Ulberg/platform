module Platform.Access.Program

open System.Collections.Generic
open System.IO
open Pulumi
open Platform.Access

let private run () =
    task {
        let config = Config "access"

        let platformRoot =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."))

        let access = People.load (Path.Combine(platformRoot, "access", "people.yaml"))
        let subscriptionId = Config("azure-native").Require "subscriptionId"

        let owner =
            Config("github").Require "owner"
            |> People.GitHubHandle.parse
            |> Result.defaultWith (failwithf "github:owner: %s")

        do! GitHub.checkCodeowners platformRoot access

        let groups = Entra.groups access
        Entra.pim access groups
        Entra.conditionalAccess (Entra.parseMode (config.Require "conditionalAccess")) access groups
        AzureRbac.assign subscriptionId groups
        GitHub.repos owner access

        return
            dict
                [
                    "platformRead", groups.platformRead.ObjectId :> obj
                    "platformWrite", groups.platformWrite.ObjectId :> obj
                    "platformAdmin", groups.platformAdmin.ObjectId :> obj
                    "infraDevelopers", groups.infraDevelopers.ObjectId :> obj
                    "appDevelopers", groups.appDevelopers.ObjectId :> obj
                ]
    }

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(run).GetAwaiter().GetResult()
