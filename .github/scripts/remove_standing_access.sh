#!/usr/bin/env bash
# One-time removal of the standing admin access left over from the bootstrap (PLAN.md Phase 0, D35/D36).
# Run by .github/workflows/remove-standing-access.yml as the apply identity, after a platform lead has
# approved the dev environment. Never run it from a laptop.
#
#   step 1: only the admin account's standing Global Admin, so its PIM activation can be tested while the
#           owner account and the emergency accounts still hold Global Admin.
#   step 2: everything else: the admin's subscription Owner and bootstrap roles on the Pulumi state, and
#           the owner account's Global Admin and subscription Owner (it keeps only its billing role).
#
# Each step checks its safety conditions first and skips assignments that are already gone, so it can be
# re-run. Object IDs are not secrets.
set -euo pipefail

step=${1:?usage: remove_standing_access.sh 1|2}
graph=https://graph.microsoft.com/v1.0
global_admin=62e90394-69f5-4237-9190-012177145e10
sub=/subscriptions/${AZURE_SUBSCRIPTION_ID:?}
state=$sub/resourceGroups/rg-platform-state/providers

admin=19d54064-9c11-4c17-8b66-dee39703a3e2         # endre@…onmicrosoft.com (people.yaml: endre)
owner_account=0db47489-ab57-4af2-8fb3-bf44aaf52029 # the owner Microsoft account (guest)
emergency=(9c485111-65fd-44cc-b019-d4bca6cf2779 4340cda3-8d90-4023-be5b-6feeba472bf8) # people.yaml

fail() { echo "Stopped: $*" >&2; exit 1; }

# Global Admin assignment schedules of one principal, of one type: Assigned (standing) or Activated (PIM).
global_admin_schedules() { # principal-id assignment-type
  az rest --method GET \
    --url "$graph/roleManagement/directory/roleAssignmentSchedules?\$filter=principalId%20eq%20'$1'%20and%20roleDefinitionId%20eq%20'$global_admin'" \
    --query "length(value[?assignmentType=='$2'])" -o tsv
}

remove_global_admin() { # principal-id label
  if [ "$(global_admin_schedules "$1" Assigned)" = 0 ]; then
    echo "  = $2: no standing Global Admin"
    return
  fi
  az rest --method POST --url "$graph/roleManagement/directory/roleAssignmentScheduleRequests" -o none --body \
    "{\"action\":\"adminRemove\",\"principalId\":\"$1\",\"roleDefinitionId\":\"$global_admin\",\"directoryScopeId\":\"/\",\"justification\":\"D35: no standing Global Admin (PLAN.md Phase 0)\"}"
  echo "  - $2: standing Global Admin removed"
}

remove_role() { # principal-id label role scope
  local ids
  ids=$(az role assignment list --assignee "$1" --role "$3" --scope "$4" --query "[].id" -o tsv)
  if [ -z "$ids" ]; then
    echo "  = $2: no $3 on ${4##*/}"
    return
  fi
  az role assignment delete --ids $ids -o none
  echo "  - $2: $3 on ${4##*/} removed"
}

has_owner() { # principal-id
  [ "$(az role assignment list --assignee "$1" --role Owner --scope "$sub" --query "length(@)" -o tsv)" != 0 ]
}

echo "Safety checks"
for id in "${emergency[@]}"; do
  [ "$(global_admin_schedules "$id" Assigned)" != 0 ] || fail "emergency account $id has no standing Global Admin"
done
echo "  ok: both emergency accounts hold standing Global Admin"
eligible=$(az rest --method GET \
  --url "$graph/roleManagement/directory/roleEligibilitySchedules?\$filter=principalId%20eq%20'$admin'%20and%20roleDefinitionId%20eq%20'$global_admin'" \
  --query "length(value)" -o tsv)
[ "$eligible" != 0 ] || fail "the admin account is not PIM-eligible for Global Admin"
echo "  ok: the admin account is PIM-eligible for Global Admin"

case "$step" in
1)
  echo "Step 1: the admin account's standing Global Admin"
  remove_global_admin "$admin" "admin account"
  ;;
2)
  # Removing the last other Owners is only safe once the emergency accounts hold Owner (the access stack
  # assigns it), and once the admin has shown it can reach Global Admin through PIM (step 1).
  for id in "${emergency[@]}"; do has_owner "$id" || fail "emergency account $id is not subscription Owner"; done
  echo "  ok: both emergency accounts are subscription Owner"
  [ "$(global_admin_schedules "$admin" Assigned)" = 0 ] || fail "run step 1 first"
  echo "  ok: step 1 is done"

  echo "Step 2: the admin account's Azure roles"
  remove_role "$admin" "admin account" Owner "$sub"
  remove_role "$admin" "admin account" "Storage Blob Data Contributor" "$state/Microsoft.Storage/storageAccounts/stplatform21784576"
  remove_role "$admin" "admin account" "Key Vault Crypto Officer" "$state/Microsoft.KeyVault/vaults/kv-platform-21784576"
  echo "Step 2: the owner account (keeps only its billing role)"
  remove_global_admin "$owner_account" "owner account"
  remove_role "$owner_account" "owner account" Owner "$sub"
  ;;
*) fail "unknown step '$step', expected 1 or 2" ;;
esac
echo "Done"
