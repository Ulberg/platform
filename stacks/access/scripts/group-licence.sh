#!/usr/bin/env bash
# Assigns a licence to a group (group-based licensing): every member gets it while they are a member.
# The azuread provider has no resource for this, so Entra.fs runs this as a Pulumi Command: on apply only,
# as the apply identity. Assigning an assigned licence again is harmless.
# Env: GROUP_ID, SKU_ID.
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
body=$(jq -n --arg sku "$SKU_ID" '{addLicenses: [{skuId: $sku, disabledPlans: []}], removeLicenses: []}')
az rest --method POST --url "$graph/groups/$GROUP_ID/assignLicense" --body "$body" -o none

# Entra applies a group licence to the members in the background. Wait up to two minutes for it, and fail if
# any member couldn't get it (most often: no free licences left).
for attempt in $(seq 1 12); do
  assigned=$(az rest --method GET --url "$graph/groups/$GROUP_ID?\$select=assignedLicenses" \
    --query "length(assignedLicenses[?skuId=='$SKU_ID'])" -o tsv)
  errors=$(az rest --method GET --url "$graph/groups/$GROUP_ID/membersWithLicenseErrors?\$select=displayName" \
    --query "value[].displayName" -o tsv)
  [ "$assigned" = 1 ] && [ -z "$errors" ] && break
  [ "$attempt" -lt 12 ] || {
    echo "Licence $SKU_ID on group $GROUP_ID: assigned=$assigned, members with licence errors: ${errors:-none}." >&2
    echo "Most often there is no free licence left: buy one more (admin.microsoft.com, Your products)." >&2
    exit 1
  }
  sleep 10
done
echo "Licence $SKU_ID on group $GROUP_ID set and verified, no member errors"
