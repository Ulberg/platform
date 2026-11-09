#!/usr/bin/env bash
# Manual test of Claude's agent identity (D41, AGENTS.md section 4). Claude runs it with the agent's profile
# after the one-time setup, and again whenever the agent's access changes. Reads must work; writes must be
# refused. A write that succeeds anyway is undone at once and fails the test.
set -uo pipefail

failed=0
ok() { echo "  ok    $*"; }
bad() {
  echo "  FAIL  $*"
  failed=1
}

user=$(az account show --query user.name -o tsv 2>/dev/null) || {
  echo "Not signed in. Sign the agent in first (AGENTS.md section 4, step 4)."
  exit 1
}
[[ "$user" == *-agent@* ]] || {
  echo "Signed in as $user, which is not an agent account. Stopping."
  exit 1
}
echo "Signed in as $user"
me=$(az ad signed-in-user show --query id -o tsv)
sub=/subscriptions/$(az account show --query id -o tsv)

echo "Reads (must work)"
az group list -o none 2>/dev/null && ok "list resource groups" || bad "list resource groups"
az role assignment list --all -o none 2>/dev/null && ok "list role assignments" || bad "list role assignments"
az monitor activity-log list --max-events 1 -o none 2>/dev/null && ok "read the activity log" ||
  bad "read the activity log"
az rest --method GET --url "https://graph.microsoft.com/v1.0/auditLogs/signIns?\$top=1" -o none 2>/dev/null &&
  ok "read Entra sign-in logs" || bad "read Entra sign-in logs"

echo "Writes (must be refused)"
probe=rg-agent-write-probe
if az group create --name "$probe" --location swedencentral -o none 2>/dev/null; then
  bad "created resource group $probe (deleting it)"
  az group delete --name "$probe" --yes --no-wait
else
  ok "create a resource group: refused"
fi
if az role assignment create --assignee-object-id "$me" --assignee-principal-type User --role Owner \
  --scope "$sub" -o none 2>/dev/null; then
  bad "made itself Owner (removing it)"
  az role assignment delete --assignee "$me" --role Owner --scope "$sub"
else
  ok "make itself Owner: refused"
fi
if az storage blob list --account-name stplatform21784576 --container-name pulumi --auth-mode login \
  --num-results 1 -o none 2>/dev/null; then
  bad "read the Pulumi state"
else
  ok "read the Pulumi state: refused"
fi
if group=$(az ad group create --display-name agent-write-probe --mail-nickname agent-write-probe \
  --query id -o tsv 2>/dev/null); then
  bad "created Entra group agent-write-probe (deleting it)"
  az ad group delete --group "$group"
else
  ok "create an Entra group: refused"
fi
m365='{"displayName":"agent-write-probe-m365","mailNickname":"agent-write-probe-m365","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false}'
if group=$(az rest --method POST --url https://graph.microsoft.com/v1.0/groups --body "$m365" --query id -o tsv \
  2>/dev/null); then
  bad "created Microsoft 365 group agent-write-probe-m365 (deleting it)"
  az rest --method DELETE --url "https://graph.microsoft.com/v1.0/groups/$group"
else
  ok "create a Microsoft 365 group: refused"
fi
if app=$(az ad app create --display-name agent-write-probe --query id -o tsv 2>/dev/null); then
  bad "registered app agent-write-probe (deleting it)"
  az ad app delete --id "$app"
else
  ok "register an app: refused"
fi

[ "$failed" = 0 ] && echo "Agent smoke test passed" || echo "Agent smoke test FAILED"
exit "$failed"
