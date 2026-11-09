#!/usr/bin/env bash
# Deletes the Conditional Access policy that ca-context-policy.sh created, when it leaves the plan (for
# example when access:conditionalAccess goes back to "off"). The authentication context stays; on its own
# it does nothing. Env: POLICY_NAME.
set -euo pipefail

ca=https://graph.microsoft.com/v1.0/identity/conditionalAccess
id=$(az rest --method GET --url "$ca/policies" --query "value[?displayName=='$POLICY_NAME'].id | [0]" -o tsv)
[ -z "$id" ] || az rest --method DELETE --url "$ca/policies/$id" -o none
echo "Conditional Access policy '$POLICY_NAME' removed"
