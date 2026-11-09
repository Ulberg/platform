/// Turns the Azure RBAC part of a Desired.Plan into role assignments on the subscription.
module Platform.Access.AzureRbac

open Pulumi.AzureNative.Authorization
open Platform.Access.Desired

let private assignment (subscriptionId: string) (groups: Map<GroupName, Pulumi.AzureAD.Group>) (a: RoleGrant) =
    let scope = $"/subscriptions/{subscriptionId}"
    let group = GroupName.value a.group
    let role = AzureRole.definitionId a.role

    RoleAssignment(
        $"{group}-{(string a.role).ToLowerInvariant()}",
        RoleAssignmentArgs(
            Scope = scope,
            RoleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{role}",
            PrincipalId = groups[a.group].ObjectId,
            PrincipalType = PrincipalType.Group,
            Description = $"{group}: {a.role} on the subscription (access/people.yaml)"
        )
    )

let deploy (subscriptionId: string) (groups: Map<GroupName, Pulumi.AzureAD.Group>) (plan: Plan) : RoleAssignment list =
    plan.roleAssignments |> List.map (assignment subscriptionId groups)
