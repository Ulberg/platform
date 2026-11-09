/// Azure role assignments for the access groups. Write roles on app resource groups come in Phase 3.
module Platform.Access.AzureRbac

open Pulumi
open Pulumi.AzureNative.Authorization
open Platform.Access.Entra

let private reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7"

let assign (subscriptionId: string) (g: Groups) =
    let scope = $"/subscriptions/{subscriptionId}"

    RoleAssignment(
        "platform-read-reader",
        RoleAssignmentArgs(
            Scope = scope,
            RoleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{reader}",
            PrincipalId = g.platformRead.ObjectId,
            PrincipalType = PrincipalType.Group,
            Description = "platform-read: standing read on the subscription (access/people.yaml)"
        )
    )
    |> ignore
