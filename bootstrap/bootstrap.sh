#!/usr/bin/env bash
# One-time bootstrap of what Pulumi needs before it can run (see bootstrap/README.md).
#
# Run as the tenant admin, after `az login --tenant <TENANT_ID>` and `gh auth login`.
# Safe to re-run: every step checks before it creates.
#
# Creates, in the workforce tenant / subscription:
#   - resource providers we use
#   - Pulumi state: storage account (Entra auth only, no shared keys) + Key Vault key for secrets
#   - two CI identities (user-assigned managed identities, no secrets) trusted via GitHub OIDC:
#       id-platform-apply    only from repo:<repo>:environment:dev   (approved apply runs)
#       id-platform-preview  only from repo:<repo>:pull_request and :ref:refs/heads/main (read-only preview)
#   - their Azure roles and Microsoft Graph permissions
#   - GitHub variables in the platform repo pointing at all of the above
set -euo pipefail
cd "$(dirname "$0")"
source ./config.env

log() { printf '\n==> %s\n' "$*"; }

# --- preflight ----------------------------------------------------------------
log "Checking you are signed in to the right tenant and subscription"
az account set --subscription "$SUBSCRIPTION_ID"
[ "$(az account show --query tenantId -o tsv)" = "$TENANT_ID" ] || { echo "Signed in to the wrong tenant"; exit 1; }
ME=$(az ad signed-in-user show --query id -o tsv)
gh auth status >/dev/null
SUB_SCOPE="/subscriptions/$SUBSCRIPTION_ID"
# GitHub's OIDC subject carries immutable IDs (repo:<owner>@<owner-id>/<repo>@<repo-id>), so a renamed
# or deleted-and-recreated repo with the same name can never get these identities' tokens.
OIDC_REPO="repo:${GITHUB_REPO%%/*}@$(gh api "users/${GITHUB_REPO%%/*}" --jq .id)/${GITHUB_REPO##*/}@$(gh api "repos/$GITHUB_REPO" --jq .id)"

assign_role() { # principal-id principal-type role scope
  local existing
  existing=$(az role assignment list --assignee-object-id "$1" --role "$3" --scope "$4" --query "length(@)" -o tsv)
  if [ "$existing" = "0" ]; then
    az role assignment create --assignee-object-id "$1" --assignee-principal-type "$2" --role "$3" --scope "$4" -o none
    echo "  + $3 on ${4##*/}"
  else
    echo "  = $3 on ${4##*/}"
  fi
}

# --- providers ----------------------------------------------------------------
log "Registering resource providers"
for ns in Microsoft.ContainerService Microsoft.DBforPostgreSQL Microsoft.KeyVault Microsoft.ContainerRegistry \
          Microsoft.Network Microsoft.OperationalInsights Microsoft.Insights Microsoft.ManagedIdentity \
          Microsoft.Storage Microsoft.AzureActiveDirectory; do
  az provider register --namespace "$ns" -o none 2>/dev/null
  echo "  $ns"
done
echo "  waiting for the providers the bootstrap needs right now"
for ns in Microsoft.Storage Microsoft.KeyVault Microsoft.ManagedIdentity; do
  until [ "$(az provider show -n "$ns" --query registrationState -o tsv)" = "Registered" ]; do sleep 10; done
done

# --- state storage --------------------------------------------------------------
log "Pulumi state storage"
az group create -n "$STATE_RESOURCE_GROUP" -l "$LOCATION" -o none
if ! az storage account show -n "$STATE_STORAGE_ACCOUNT" -g "$STATE_RESOURCE_GROUP" -o none 2>/dev/null; then
  az storage account create -n "$STATE_STORAGE_ACCOUNT" -g "$STATE_RESOURCE_GROUP" -l "$LOCATION" \
    --sku Standard_ZRS --kind StorageV2 --min-tls-version TLS1_2 --https-only true \
    --allow-blob-public-access false --allow-shared-key-access false -o none
fi
STORAGE_ID=$(az storage account show -n "$STATE_STORAGE_ACCOUNT" -g "$STATE_RESOURCE_GROUP" --query id -o tsv)
az storage account blob-service-properties update --account-name "$STATE_STORAGE_ACCOUNT" -g "$STATE_RESOURCE_GROUP" \
  --enable-versioning true --enable-delete-retention true --delete-retention-days 30 \
  --enable-container-delete-retention true --container-delete-retention-days 30 -o none
assign_role "$ME" User "Storage Blob Data Contributor" "$STORAGE_ID"
echo "  waiting for the role assignment to take effect"
for _ in $(seq 1 30); do
  az storage container exists --account-name "$STATE_STORAGE_ACCOUNT" -n "$STATE_CONTAINER" --auth-mode login -o none 2>/dev/null && break
  sleep 10
done
az storage container create --account-name "$STATE_STORAGE_ACCOUNT" -n "$STATE_CONTAINER" --auth-mode login -o none
CONTAINER_SCOPE="$STORAGE_ID/blobServices/default/containers/$STATE_CONTAINER"

log "Key Vault for Pulumi secrets"
if ! az keyvault show -n "$STATE_KEY_VAULT" -o none 2>/dev/null; then
  az keyvault create -n "$STATE_KEY_VAULT" -g "$STATE_RESOURCE_GROUP" -l "$LOCATION" \
    --enable-rbac-authorization true --enable-purge-protection true --retention-days 90 -o none
fi
VAULT_ID=$(az keyvault show -n "$STATE_KEY_VAULT" --query id -o tsv)
assign_role "$ME" User "Key Vault Crypto Officer" "$VAULT_ID"
for _ in $(seq 1 30); do
  az keyvault key list --vault-name "$STATE_KEY_VAULT" -o none 2>/dev/null && break
  sleep 10
done
if ! az keyvault key show --vault-name "$STATE_KEY_VAULT" -n "$SECRETS_KEY" -o none 2>/dev/null; then
  az keyvault key create --vault-name "$STATE_KEY_VAULT" -n "$SECRETS_KEY" --kty RSA --size 3072 --ops encrypt decrypt -o none
fi

# --- CI identities ----------------------------------------------------------------
create_identity() { # name federated-subject
  az identity show -n "$1" -g "$STATE_RESOURCE_GROUP" -o none 2>/dev/null \
    || az identity create -n "$1" -g "$STATE_RESOURCE_GROUP" -l "$LOCATION" -o none
  federate "$1" github "$2"
}

federate() { # identity credential-name federated-subject
  local action=create
  az identity federated-credential show -n "$2" --identity-name "$1" -g "$STATE_RESOURCE_GROUP" -o none 2>/dev/null && action=update
  az identity federated-credential "$action" -n "$2" --identity-name "$1" -g "$STATE_RESOURCE_GROUP" \
    --issuer https://token.actions.githubusercontent.com --subject "$3" --audiences api://AzureADTokenExchange -o none
}

GRAPH_SP=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
grant_graph() { # principal-id permission...
  local principal=$1; shift
  local existing
  existing=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principal/appRoleAssignments" \
    --query "value[].appRoleId" -o tsv)
  for permission in "$@"; do
    local role
    role=$(az ad sp show --id "$GRAPH_SP" --query "appRoles[?value=='$permission' && contains(allowedMemberTypes, 'Application')].id | [0]" -o tsv)
    [ -n "$role" ] || { echo "Unknown Graph permission $permission"; exit 1; }
    if grep -qx "$role" <<<"$existing"; then
      echo "  = Graph $permission"
    else
      az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principal/appRoleAssignments" \
        --body "{\"principalId\":\"$principal\",\"resourceId\":\"$GRAPH_SP\",\"appRoleId\":\"$role\"}" -o none
      echo "  + Graph $permission"
    fi
  done
}

log "CI identity for approved apply runs ($APPLY_IDENTITY)"
create_identity "$APPLY_IDENTITY" "$OIDC_REPO:environment:dev"
APPLY_PRINCIPAL=$(az identity show -n "$APPLY_IDENTITY" -g "$STATE_RESOURCE_GROUP" --query principalId -o tsv)
APPLY_CLIENT=$(az identity show -n "$APPLY_IDENTITY" -g "$STATE_RESOURCE_GROUP" --query clientId -o tsv)
assign_role "$APPLY_PRINCIPAL" ServicePrincipal Contributor "$SUB_SCOPE"
assign_role "$APPLY_PRINCIPAL" ServicePrincipal "Role Based Access Control Administrator" "$SUB_SCOPE"
assign_role "$APPLY_PRINCIPAL" ServicePrincipal "Storage Blob Data Contributor" "$CONTAINER_SCOPE"
assign_role "$APPLY_PRINCIPAL" ServicePrincipal "Key Vault Crypto User" "$VAULT_ID"
grant_graph "$APPLY_PRINCIPAL" \
  Group.ReadWrite.All User.ReadWrite.All Application.ReadWrite.All \
  RoleManagement.ReadWrite.Directory PrivilegedAccess.ReadWrite.AzureADGroup RoleManagementPolicy.ReadWrite.AzureADGroup \
  Policy.Read.All Policy.ReadWrite.ConditionalAccess Policy.ReadWrite.Authorization

log "CI identity for pull-request previews ($PREVIEW_IDENTITY)"
create_identity "$PREVIEW_IDENTITY" "$OIDC_REPO:pull_request"
# Also from pushes to main, so every apply is shown as a preview before a lead approves it.
federate "$PREVIEW_IDENTITY" github-main "$OIDC_REPO:ref:refs/heads/main"
PREVIEW_PRINCIPAL=$(az identity show -n "$PREVIEW_IDENTITY" -g "$STATE_RESOURCE_GROUP" --query principalId -o tsv)
PREVIEW_CLIENT=$(az identity show -n "$PREVIEW_IDENTITY" -g "$STATE_RESOURCE_GROUP" --query clientId -o tsv)
assign_role "$PREVIEW_PRINCIPAL" ServicePrincipal Reader "$SUB_SCOPE"
# Preview takes a lock file in the state container, so it needs write on that container only.
assign_role "$PREVIEW_PRINCIPAL" ServicePrincipal "Storage Blob Data Contributor" "$CONTAINER_SCOPE"
assign_role "$PREVIEW_PRINCIPAL" ServicePrincipal "Key Vault Crypto User" "$VAULT_ID"
grant_graph "$PREVIEW_PRINCIPAL" \
  Group.Read.All User.Read.All Application.Read.All \
  RoleManagement.Read.Directory PrivilegedAccess.Read.AzureADGroup RoleManagementPolicy.Read.AzureADGroup \
  Policy.Read.All

# --- GitHub variables ---------------------------------------------------------------
log "GitHub variables in $GITHUB_REPO"
setvar() { gh variable set "$1" --repo "$GITHUB_REPO" --body "$2" "${@:3}"; echo "  $1"; }
setvar AZURE_TENANT_ID "$TENANT_ID"
setvar AZURE_SUBSCRIPTION_ID "$SUBSCRIPTION_ID"
setvar AZURE_PREVIEW_CLIENT_ID "$PREVIEW_CLIENT"
setvar PULUMI_BACKEND_URL "azblob://$STATE_CONTAINER?storage_account=$STATE_STORAGE_ACCOUNT"
setvar PULUMI_SECRETS_PROVIDER "azurekeyvault://$STATE_KEY_VAULT.vault.azure.net/keys/$SECRETS_KEY"
setvar AZURE_CLIENT_ID "$APPLY_CLIENT" --env dev

log "Done"
cat <<EOF
Pulumi state:   azblob://$STATE_CONTAINER?storage_account=$STATE_STORAGE_ACCOUNT
Secrets key:    azurekeyvault://$STATE_KEY_VAULT.vault.azure.net/keys/$SECRETS_KEY
Apply identity:   $APPLY_CLIENT   (trusted only from $OIDC_REPO:environment:dev)
Preview identity: $PREVIEW_CLIENT   (trusted only from $OIDC_REPO:pull_request and main)

Note: the apply identity can manage groups, apps, PIM and Conditional Access in this tenant.
Only an approved run in the 'dev' environment can sign in as it.
EOF
