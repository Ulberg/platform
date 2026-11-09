#!/usr/bin/env bash
# Grants one Microsoft Graph application permission to a CI identity, for when a later change needs one
# that bootstrap.sh didn't grant at the time (bootstrap.sh lists them all, for a fresh tenant).
#
# Run it yourself, in your own terminal, as the admin account with Global Admin activated in PIM (then
# `az login` again, so the token carries the role). Never through Claude: its agent account can't grant.
#
#   bootstrap/grant-graph-permission.sh id-platform-apply Policy.ReadWrite.Authorization
set -euo pipefail
cd "$(dirname "$0")"
source ./config.env

identity=${1:?usage: grant-graph-permission.sh <identity name> <Graph permission>}
permission=${2:?usage: grant-graph-permission.sh <identity name> <Graph permission>}
graph_app=00000003-0000-0000-c000-000000000000

principal=$(az identity show -n "$identity" -g "$STATE_RESOURCE_GROUP" --subscription "$SUBSCRIPTION_ID" \
  --query principalId -o tsv)
graph_sp=$(az ad sp show --id "$graph_app" --query id -o tsv)
role=$(az ad sp show --id "$graph_app" \
  --query "appRoles[?value=='$permission' && contains(allowedMemberTypes, 'Application')].id | [0]" -o tsv)
[ -n "$role" ] || { echo "Unknown Graph permission $permission" >&2; exit 1; }

existing=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principal/appRoleAssignments" \
  --query "value[].appRoleId" -o tsv)
if grep -qx "$role" <<<"$existing"; then
  echo "= $identity already has $permission"
else
  az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principal/appRoleAssignments" \
    --body "{\"principalId\":\"$principal\",\"resourceId\":\"$graph_sp\",\"appRoleId\":\"$role\"}" -o none
  echo "+ $identity: $permission"
fi
