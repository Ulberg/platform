#!/usr/bin/env bash
# Fails if anyone holds privileged access *standing* that should only be reachable through PIM (D35, D36):
#   - Global Admin: only the emergency accounts in access/people.yaml;
#   - Privileged Role Administrator (can grant Global Admin): nobody;
#   - Owner or User Access Administrator on the subscription, including inherited from above: only the
#     emergency accounts.
# PIM activations are not standing access and are ignored. Runs in the pulumi workflow as the read-only
# preview identity, on every PR, on main and every night, so a role added outside PIM fails a check.
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
sub=/subscriptions/${AZURE_SUBSCRIPTION_ID:?}
emergency=$(yq '.["emergency-access"][]' access/people.yaml)
[ -n "$emergency" ] || { echo "No emergency-access accounts in access/people.yaml" >&2; exit 1; }

# Prints every principal in $1 (one per line) that isn't allowed by $2 (one per line).
not_allowed() { grep -vxF -f <(printf '%s\n' "$2") <<<"$1" || true; }

standing_directory_role() { # role-template-id
  az rest --method GET \
    --url "$graph/roleManagement/directory/roleAssignmentSchedules?\$filter=roleDefinitionId%20eq%20'$1'" \
    --query "value[?assignmentType=='Assigned'].principalId" -o tsv
}

problems=()
report() { # label principals
  local p
  for p in $2; do problems+=("$1: $p"); done
}

report "standing Global Admin" "$(not_allowed "$(standing_directory_role 62e90394-69f5-4237-9190-012177145e10)" "$emergency")"
report "standing Privileged Role Administrator" "$(standing_directory_role e8611ab8-c189-46e8-94e1-60213ab1f814)"
for role in Owner "User Access Administrator"; do
  holders=$(az role assignment list --scope "$sub" --include-inherited --role "$role" --query "[].principalId" -o tsv)
  report "$role on the subscription" "$(not_allowed "$holders" "$emergency")"
done

if [ ${#problems[@]} -gt 0 ]; then
  echo "Standing privileged access outside PIM (object IDs):" >&2
  printf '  %s\n' "${problems[@]}" >&2
  echo "Remove it, or make it PIM-eligible through access/people.yaml." >&2
  exit 1
fi
echo "Standing access ok: Global Admin, Owner and User Access Administrator are held only by the emergency accounts"
