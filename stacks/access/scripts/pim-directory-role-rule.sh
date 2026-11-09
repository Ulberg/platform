#!/usr/bin/env bash
# Sets one rule of an Entra directory role's PIM policy. A PATCH, so running it again is harmless.
# The azuread provider has no resource for directory-role PIM policies, so Entra.fs runs this as a
# Pulumi Command: on apply only (never on preview), as the apply identity.
# Env: ROLE_ID (role template ID), RULE_ID (e.g. Expiration_EndUser_Assignment), BODY (the rule as JSON).
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
filter="scopeId%20eq%20'/'%20and%20scopeType%20eq%20'DirectoryRole'%20and%20roleDefinitionId%20eq%20'$ROLE_ID'"

policy=$(az rest --method GET --url "$graph/policies/roleManagementPolicyAssignments?\$filter=$filter" \
  --query "value[0].policyId" -o tsv)
[ -n "$policy" ] || { echo "No PIM policy found for directory role $ROLE_ID" >&2; exit 1; }

az rest --method PATCH --url "$graph/policies/roleManagementPolicies/$policy/rules/$RULE_ID" --body "$BODY" -o none
echo "PIM policy for role $ROLE_ID: $RULE_ID set"
