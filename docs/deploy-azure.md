# Deploying to Azure

LogPulse runs on Azure inside the free tiers:

| Piece | Azure service | Why it stays free |
|---|---|---|
| API and dashboard | Azure Container Apps, Consumption profile | Scale to zero, one replica at most, monthly free grant |
| Database | Azure SQL Database free offer (serverless) | Auto-pauses until next month when the free allowance runs out, instead of billing |
| Network | Virtual network + `Microsoft.Sql` service endpoint | Free; the SQL firewall only accepts the apps' subnet |
| Logs | Console log stream only (no Log Analytics) | Log Analytics bills per GB |

Everything is described in [`infra/main.bicep`](../infra/main.bicep) and deployed once by hand. After that, the
[CD workflow](../.github/workflows/cd.yml) only rolls out new images. Agents keep running on your own servers and send to
the API's public URL.

## Who can deploy and what it can cost

- **Spending:** use an [Azure for Students](https://azure.microsoft.com/free/students) subscription. It has a hard
  spending limit (no credit card): when the credit runs out, services stop and nothing is charged. On a pay-as-you-go
  subscription the worst case is someone keeping both apps awake all month with requests (roughly 30 € a month). Add a
  budget alert there, but keep in mind that an alert does not stop anything.
- **Triggering a deploy:** only a push to `main` (through a PR with the required CI checks) or a manual run by
  someone with write access. Pull requests and forks never run the CD workflow and never see its secrets.
- **Approving it:** the deploy job runs in the `production` environment, which waits for a required reviewer.
- **Getting into Azure:** there is no Azure password in GitHub. The workflow signs in with OIDC as a user-assigned
  managed identity. Azure only issues that token to jobs of this repository running in the `production` environment.
  The identity has a custom role on the two container apps (read and update, **not** `listSecrets`) and `Reader` on the
  resource group. It can roll out a new image, but it cannot read the SQL password or the JWT key, and it cannot create
  resources.
- **What gets deployed:** images go out by digest (`image@sha256:…`), the exact build of that commit. Tags in GHCR
  can be overwritten, so they are never used. Only commits already on `main` are published, including tags and manual runs.
- **What stays private:** the workflow never prints application logs, because Actions logs of a public repository
  are public. Read the logs from your own machine with `az containerapp logs show`.

## 1. Before merging the CD workflow: protect the environment

In GitHub, open **Settings → Environments → New environment** and name it `production`:

- **Required reviewers:** yourself. "Prevent self-review" must stay off if you are the only maintainer.
- **Deployment branches and tags:** "Selected branches and tags", rule `main`.

Create it before the first run. Otherwise GitHub creates it on the fly **without protection**. The deploy job is
skipped anyway until step 4, but the protection must already be in place by then.

## 2. Make the images public

The first push to `main` with the CD workflow publishes `logpulse-api`, `logpulse-dashboard` and `logpulse-agent` to
GHCR. GitHub creates new packages as private. For each one, open **your profile → Packages → package → Package settings →
Change visibility → Public**, so Container Apps can pull them without a registry password. In the same page, under
**Manage Actions access**, leave only this repository (with the "Write" role it gets by default).

## 3. Provision Azure

With the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (it downloads Bicep on first use):

```bash
az login
az account set --subscription "<your Azure for Students subscription>"

# Resource providers used by the template (one time per subscription).
for p in Microsoft.App Microsoft.Sql Microsoft.Network Microsoft.ManagedIdentity; do az provider register -n $p --wait; done

# A region your subscription allows (Azure for Students restricts them: the portal lists the allowed ones).
az group create -n logpulse-rg -l <region>

# The four secure parameters are prompted for and never written to disk. Use long random values; the admin and
# viewer passwords are the dashboard/API logins.
az deployment group create -g logpulse-rg -f infra/main.bicep -o json \
  --query "properties.outputs.{azureClientId: azureClientId.value, azureTenantId: azureTenantId.value, azureSubscriptionId: azureSubscriptionId.value, apiUrl: apiUrl.value, dashboardUrl: dashboardUrl.value}"
```

If `githubRepository` is not `Samot2003/logpulse`, pass `-p githubRepository=<owner>/<repo>`. Some university tenants
enforce Microsoft Entra-only authentication on Azure SQL through a policy. In that case the SQL server cannot be
created with a SQL login, and the deployment says so.

Check before going on that the database shows "Free offer applied" in the portal. If the subscription cannot use the
free offer, the deployment fails instead of creating a paid database.

The API creates its schema on first start. It never creates the database (`Database__CreateIfMissing=false`), so a
wrong name fails instead of creating a second, billed database.

## 4. Connect GitHub to Azure

From the deployment outputs:

| Where | Name | Value |
|---|---|---|
| `production` environment → Secrets | `AZURE_CLIENT_ID` | `azureClientId` |
| `production` environment → Secrets | `AZURE_TENANT_ID` | `azureTenantId` |
| `production` environment → Secrets | `AZURE_SUBSCRIPTION_ID` | `azureSubscriptionId` |
| `production` environment → Variables | `DASHBOARD_URL` | `dashboardUrl` |
| Repository → Variables | `AZURE_RESOURCE_GROUP` | `logpulse-rg` |

`AZURE_RESOURCE_GROUP` is what turns the deploy job on. Then open **Actions → CD → Run workflow** on `main`, approve
the deployment, and wait for the smoke test. It checks that the new revision serves traffic and that `/health` answers
on both apps. Expect a few minutes the first time, because the apps start from zero and the database resumes.

## 5. Send data from an agent

```bash
API=https://<apiUrl>
TOKEN=$(curl -s -H 'Content-Type: application/json' -d '{"userName":"admin","password":"<admin password>"}' \
  $API/api/auth/token | jq -r .accessToken)
curl -s -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"serverName":"my-server"}' $API/api/agents     # returns the API key once
```

Configure the agent with `Agent:ApiBaseUrl` = the API URL, `Agent:ServerName` = `my-server` and the returned key (see
[Run the agent](../README.md#run-the-agent)), then open the dashboard URL.

## Known limits

- **Cold starts:** after a quiet period, the first request wakes the app (seconds) and possibly the database (up to
  about a minute).
- **Dashboard sessions are in memory,** and its Data Protection keys too: when the dashboard scales to zero or is
  redeployed, users sign in again.
- **Rate limits by user IP:** the API trusts `X-Forwarded-For` from the apps' subnet, two hops deep (user → ingress →
  dashboard → ingress → API). If calls from the dashboard to the API's public URL leave through a public outbound
  address instead of staying in the environment, login attempts made through the dashboard share one rate-limit
  bucket. That is safe (nothing can be spoofed) but less precise. Check it after the first deploy: fail 11 logins in a
  row from one network, then sign in from another one (a phone on mobile data). If that login works, the buckets are
  per user.
- **The API connects as the SQL server administrator,** because it applies the schema scripts itself. Anyone who took
  over the API could create more databases on that server. With Azure for Students the spending limit caps that
  anyway. A contained database user with rights on `LogPulse` only would close the gap: `CREATE USER logpulse_app
  WITH PASSWORD = '…'; ALTER ROLE db_owner ADD MEMBER logpulse_app;` run in the database, then the connection string
  secret changed to that user.
- **Re-running the template** resets the images to `latest`. Run the CD workflow afterwards to pin the current commit again.

## Tearing down

```bash
az group delete -n logpulse-rg
```
