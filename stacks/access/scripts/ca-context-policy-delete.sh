#!/usr/bin/env bash
# Deletes the Conditional Access policy that ca-context-policy.sh created, when it leaves the plan (for
# example when access:conditionalAccess goes back to "off"). The authentication context stays; on its own
# it does nothing. Env: POLICY_NAME.
set -euo pipefail

ca=https://graph.microsoft.com/v1.0/identity/conditionalAccess

# Graph throttles Conditional Access writes ("Too many requests"). Retry those with a growing pause; any
# other error fails at once.
graph() {
  local attempt out err
  err=$(mktemp)
  for attempt in 1 2 3 4 5 6; do
    if out=$(az rest "$@" 2>"$err"); then
      printf '%s' "$out"
      return 0
    fi
    grep -q TooManyRequests "$err" || { cat "$err" >&2; return 1; }
    echo "Graph is throttling, retrying in $((attempt * 15)) s" >&2
    sleep $((attempt * 15))
  done
  cat "$err" >&2
  return 1
}
id=$(graph --method GET --url "$ca/policies" --query "value[?displayName=='$POLICY_NAME'].id | [0]" -o tsv)
[ -z "$id" ] || graph --method DELETE --url "$ca/policies/$id" -o none
echo "Conditional Access policy '$POLICY_NAME' removed"
