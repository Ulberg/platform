#!/usr/bin/env bash
# Removes the licence that group-licence.sh assigned, when it leaves the plan. Env: GROUP_ID, SKU_ID.
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
body=$(jq -n --arg sku "$SKU_ID" '{addLicenses: [], removeLicenses: [$sku]}')
az rest --method POST --url "$graph/groups/$GROUP_ID/assignLicense" --body "$body" -o none
echo "Licence $SKU_ID removed from group $GROUP_ID"
