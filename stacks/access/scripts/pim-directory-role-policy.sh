#!/usr/bin/env bash
# Sets the rules of one Entra directory role's PIM policy. PATCHes, so running it again is harmless.
# The azuread provider has no resource for directory-role PIM policies, so Entra.fs runs this as a
# Pulumi Command: on apply only (never on preview), as the apply identity.
# Env: ROLE_ID (role template ID), RULES (JSON array of rules, each with its "id").
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
filter="scopeId%20eq%20'/'%20and%20scopeType%20eq%20'DirectoryRole'%20and%20roleDefinitionId%20eq%20'$ROLE_ID'"

policy=$(az rest --method GET --url "$graph/policies/roleManagementPolicyAssignments?\$filter=$filter" \
  --query "value[0].policyId" -o tsv)
[ -n "$policy" ] || { echo "No PIM policy found for directory role $ROLE_ID" >&2; exit 1; }

# One rule at a time: parallel PATCHes on the same policy overwrite each other. Each rule is read back
# afterwards, so a PATCH that didn't take effect fails the apply instead of passing silently.
jq -c '.[]' <<<"$RULES" | while read -r rule; do
  id=$(jq -r .id <<<"$rule")
  url="$graph/policies/roleManagementPolicies/$policy/rules/$id"
  az rest --method PATCH --url "$url" --body "$rule" -o none
  actual=$(az rest --method GET --url "$url")
  jq -e --argjson want "$rule" \
    '. as $got | $want | del(.target, ."@odata.type") | to_entries | all(.value == $got[.key])' \
    <<<"$actual" >/dev/null || { echo "PIM policy for role $ROLE_ID: $id did not take effect: $actual" >&2; exit 1; }
  echo "PIM policy for role $ROLE_ID: $id set and verified"
done
