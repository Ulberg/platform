#!/usr/bin/env bash
# Creates or updates a Conditional Access policy that targets an authentication context, and the context
# itself. The azuread provider can do neither, so Entra.fs runs this as a Pulumi Command: on apply only
# (never on preview), as the apply identity. Running it again is harmless.
# Env: CONTEXT_ID (e.g. c1), POLICY_NAME, POLICY (the policy as Graph JSON).
set -euo pipefail

ca=https://graph.microsoft.com/v1.0/identity/conditionalAccess

context=$(jq -n --arg id "$CONTEXT_ID" '{
  id: $id,
  displayName: "Privileged activation",
  description: "Required by PIM activations of privileged roles (platform access stack, CA05)",
  isAvailable: true
}')
az rest --method PATCH --url "$ca/authenticationContextClassReferences/$CONTEXT_ID" --body "$context" -o none
echo "Authentication context $CONTEXT_ID set"

id=$(az rest --method GET --url "$ca/policies" --query "value[?displayName=='$POLICY_NAME'].id | [0]" -o tsv)
if [ -n "$id" ]; then
  az rest --method PATCH --url "$ca/policies/$id" --body "$POLICY" -o none
else
  id=$(az rest --method POST --url "$ca/policies" --body "$POLICY" --query id -o tsv)
fi

# Read it back, so a change that didn't take effect fails the apply. Retries for a minute, like the PIM rules.
want=$(jq -r .state <<<"$POLICY")
for attempt in 1 2 3 4 5 6 7; do
  actual=$(az rest --method GET --url "$ca/policies/$id")
  jq -e --arg state "$want" --arg ctx "$CONTEXT_ID" \
    '.state == $state and (.conditions.applications.includeAuthenticationContextClassReferences | index($ctx) != null)' \
    <<<"$actual" >/dev/null && break
  [ "$attempt" -lt 7 ] || { echo "Conditional Access policy '$POLICY_NAME' did not take effect: $actual" >&2; exit 1; }
  sleep 10
done
echo "Conditional Access policy '$POLICY_NAME' ($id) set and verified: $want"
