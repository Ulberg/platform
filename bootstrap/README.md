# Bootstrap

What has to exist before Pulumi can run. Everything here is done once, by hand or with `bootstrap.sh`. After that, all changes go through Pulumi in CI.

Non-secret IDs live in [`config.env`](config.env). People live in [`../access/people.yaml`](../access/people.yaml).

## Part A: accounts and tenants (manual, done 2026-11-08)

1. **Local tools:** `brew install azure-cli pulumi/tap/pulumi gh kind kubectl helm Azure/kubelogin/kubelogin`, plus the .NET 10 SDK and a Docker runtime.
2. **Azure account** with a personal Microsoft account tied to an email, *not* GitHub sign-in. This "owner" account owns billing and recovery. Upgrade to pay-as-you-go right away.
3. **Find the tenant:** if entra.microsoft.com doesn't show it, use portal.azure.com → avatar → *Switch directory* → "Default Directory", or `az account show`. Rename the tenant under Entra ID → Properties.
4. **Admin account** `…@<tenant>.onmicrosoft.com`, a cloud-only member account. Give it Global Administrator.
   - **Set its usage location (Norway)** under Properties → Settings, otherwise license assignment fails.
   - **Global Admin gives no access to subscriptions.** As the owner, grant the admin **Owner** on the subscription (subscription → Access control (IAM)).
   - When adding that Owner assignment, choose **"Allow user to assign all roles"** or remove the condition. The default condition blocks granting privileged roles, and the bootstrap has to grant *Role Based Access Control Administrator*. After changing it, Azure can take ~3–5 minutes before it applies.
5. **Passkeys:** enable *Passkey (FIDO2)* and *Temporary Access Pass* under Entra → Authentication methods → Policies.
   - Owner account: account.microsoft.com → Security → add a passkey/security key, turn on two-step verification, and store the recovery code offline.
   - Admin account: mysignins.microsoft.com/security-info → add a passkey, with Authenticator as backup.
6. **Two emergency-access accounts** (`breakglass1@`, `breakglass2@`), permanent Global Admin, long random passwords, one FIDO2 key each, stored offline. To register the keys, issue a Temporary Access Pass per account and sign in once. There's no admin UI for registering a FIDO2 key on someone else's account.
7. **Entra ID P2:** 1 license (or the 30-day trial), assigned to the admin.
8. **Budget:** Cost Management → Budgets, USD 200/month, with alerts at 50/80/100 %.
9. **External ID tenant** for app users: Entra → Manage tenants → Create → *External*. Linked to the subscription, resource group `rg-identity` (Sweden Central), data location Europe.
10. **Security defaults stay on** until the Conditional Access policies that replace them are deployed.

## Part B: GitHub (done 2026-11-08)

| App | ID | Installed on | Key stored as |
|---|---|---|---|
| `ulberg-platform-admin` | 5672546 | todo, platform | `PLATFORM_ADMIN_APP_KEY`, environment `dev` (platform) |
| `ulberg-platform-reader` | 5672559 | todo, platform | `PLATFORM_READER_APP_KEY`, repo (platform) |
| `ulberg-platform-pr-bot` | 5672567 | platform | `PR_BOT_APP_KEY`, environment `release` (todo) |

All apps have webhooks off and can only be installed on this account. The keys were piped from 1Password straight into `gh secret set` and never printed. Rotate them every 6 months.

## Part C: run the bootstrap script (done 2026-11-08)

```bash
az login --tenant e17ac17e-d8cd-4280-a4e5-94ad2bb85549   # as the admin account
gh auth status                                          # must be Ulberg
./bootstrap/bootstrap.sh
```

The script is safe to re-run. It creates:

| What | Details |
|---|---|
| `rg-platform-state` | Resource group for everything below |
| `stplatform21784576` / `pulumi` | Pulumi state. ZRS, Entra-only auth (shared keys disabled), versioning + 30-day soft delete |
| `kv-platform-21784576` / `pulumi` | RSA key for Pulumi secrets. RBAC mode, purge protection |
| `id-platform-apply` | Managed identity, federated **only** with `repo:Ulberg/platform:environment:dev`. Contributor + RBAC Administrator on the subscription; Graph: groups, users, apps, PIM, Conditional Access |
| `id-platform-preview` | Managed identity, federated **only** with `repo:Ulberg/platform:pull_request`. Reader + read-only Graph; write only on the state container (for preview lock files) |
| GitHub variables | `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_PREVIEW_CLIENT_ID`, `PULUMI_BACKEND_URL`, `PULUMI_SECRETS_PROVIDER`, and `AZURE_CLIENT_ID` in environment `dev` |

There are no client secrets anywhere: both identities sign in with GitHub's OIDC token.

**Be aware:** `id-platform-apply` can manage groups, apps, PIM and Conditional Access, which makes it effectively a tenant admin. The protection is that only a run in the `dev` environment, which needs a platform lead's approval and only deploys from `main`, can get a token for it.

The admin account also gets standing *Storage Blob Data Contributor* on the state account and *Key Vault Crypto Officer* on the vault, so it can create the container and key. Pulumi moves these to PIM-eligible later.

Verified after the run: `pulumi login azblob://pulumi?storage_account=stplatform21784576` works with Entra sign-in only (no storage keys).

The External ID tenant's CI identity is created in Phase 3, when we first manage resources there.
