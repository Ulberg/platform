/// Turns the Azure RBAC part of a Desired.Plan into role assignments on the subscription.
module Platform.Access.AzureRbac

open Pulumi.FSharp
open Pulumi.AzureNative.Authorization
open Platform.Access.People
open Platform.Access.Desired

let private assignment (subscriptionId: string) (groups: Map<GroupName, Pulumi.AzureAD.Group>) (a: RoleGrant) =
    let scope = $"/subscriptions/{subscriptionId}"
    let role = AzureRole.definitionId a.role
    let roleName = (string a.role).ToLowerInvariant()

    let name, principalId, principalType =
        match a.grantee with
        | Grantee.Group group -> GroupName.value group, io groups[group].ObjectId, PrincipalType.Group
        | Grantee.EmergencyAccount id ->
            $"emergency-{EntraObjectId.string id}", input (EntraObjectId.string id), PrincipalType.User

    RoleAssignment(
        $"{name}-{roleName}",
        RoleAssignmentArgs(
            Scope = scope,
            RoleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{role}",
            PrincipalId = principalId,
            PrincipalType = principalType,
            Description = $"{name}: {a.role} on the subscription (access/people.yaml)"
        )
    )

let deploy (subscriptionId: string) (groups: Map<GroupName, Pulumi.AzureAD.Group>) (plan: Plan) : RoleAssignment list =
    plan.roleAssignments |> List.map (assignment subscriptionId groups)
