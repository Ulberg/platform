#!/usr/bin/env bash
# Sets rules of one PIM policy: a directory role's (the azuread provider has no resource for those) or a
# group's activation check (the provider can't switch between MFA and an authentication context). Entra.fs
# runs this as a Pulumi Command: on apply only (never on preview), as the apply identity. PATCHes, so
# running it again is harmless.
# Env: SCOPE_ID ("/" or a group ID), SCOPE_TYPE (DirectoryRole or Group), ROLE_ID (role template ID or
# "member"), RULES (JSON array of rules, each with its "id").
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
filter="scopeId%20eq%20'$SCOPE_ID'%20and%20scopeType%20eq%20'$SCOPE_TYPE'%20and%20roleDefinitionId%20eq%20'$ROLE_ID'"

policy=$(az rest --method GET --url "$graph/policies/roleManagementPolicyAssignments?\$filter=$filter" \
  --query "value[0].policyId" -o tsv)
[ -n "$policy" ] || { echo "No PIM policy found for $SCOPE_TYPE $SCOPE_ID, role $ROLE_ID" >&2; exit 1; }

# One rule at a time: parallel PATCHes on the same policy overwrite each other. Each rule is read back
# afterwards, so a PATCH that didn't take effect fails the apply instead of passing silently. A rule may
# not read back right away (seen with notification recipients), so the check retries for a minute.
matches() { # rule actual
  jq -e --argjson want "$1" \
    '. as $got | $want | del(.target, ."@odata.type") | to_entries | all(.value == $got[.key])' <<<"$2" >/dev/null
}

jq -c '.[]' <<<"$RULES" | while read -r rule; do
  id=$(jq -r .id <<<"$rule")
  url="$graph/policies/roleManagementPolicies/$policy/rules/$id"
  az rest --method PATCH --url "$url" --body "$rule" -o none
  for attempt in 1 2 3 4 5 6 7; do
    actual=$(az rest --method GET --url "$url")
    matches "$rule" "$actual" && break
    [ "$attempt" -lt 7 ] || { echo "PIM policy for $SCOPE_TYPE $SCOPE_ID, role $ROLE_ID: $id did not take effect: $actual" >&2; exit 1; }
    sleep 10
  done
  echo "PIM policy for $SCOPE_TYPE $SCOPE_ID, role $ROLE_ID: $id set and verified"
done
