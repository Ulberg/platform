#!/usr/bin/env bash
# Sets the tenant's authorization policy: what every member may do without a role (Desired.TenantDefaults).
# The azuread provider has no resource for it, so Entra.fs runs this as a Pulumi Command: on apply only, as
# the apply identity (needs Policy.ReadWrite.Authorization). A PATCH, so running it again is harmless.
# Env: POLICY (the policy as Graph JSON).
set -euo pipefail

url=https://graph.microsoft.com/v1.0/policies/authorizationPolicy
az rest --method PATCH --url "$url" --body "$POLICY" -o none

# Read it back: every value we sent (at any depth) must be what Entra now has. Retries for a minute.
for attempt in 1 2 3 4 5 6 7; do
  actual=$(az rest --method GET --url "$url")
  jq -e --argjson want "$POLICY" \
    '. as $got | [$want | paths(type != "object")] | all(. as $p | ($want | getpath($p)) == ($got | getpath($p)))' \
    <<<"$actual" >/dev/null && break
  [ "$attempt" -lt 7 ] || { echo "Tenant authorization policy did not take effect: $actual" >&2; exit 1; }
  sleep 10
done
echo "Tenant authorization policy set and verified"
