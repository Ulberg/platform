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
        let owner = Config("github").Require "owner"

        do! GitHub.checkCodeowners platformRoot access

        let groups = Entra.groups access
        Entra.pim access groups
        Entra.conditionalAccess (Entra.parseMode (config.Require "conditionalAccess")) access groups
        AzureRbac.assign subscriptionId groups
        GitHub.repos owner access

        return
            dict
                [
                    "platformRead", groups.PlatformRead.ObjectId :> obj
                    "platformWrite", groups.PlatformWrite.ObjectId :> obj
                    "platformAdmin", groups.PlatformAdmin.ObjectId :> obj
                    "infraDevelopers", groups.InfraDevelopers.ObjectId :> obj
                    "appDevelopers", groups.AppDevelopers.ObjectId :> obj
                ]
    }

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(run).GetAwaiter().GetResult()
