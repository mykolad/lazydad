# Deployment setup (Azure + GitHub), from an empty subscription

Everything behind Deploy Master, created in order with the `az` and `gh` CLIs. The apps and the pipelines sign in
with managed identities (Entra ID): no database password, no API key, no connection-string secret, no GitHub secret.
The only credentials you keep are the Grafana Cloud write tokens, in Key Vault. Two more keys pass through this
runbook, and only Azure keeps them: the Log Analytics workspace key, which the Container Apps environment stores to
send its logs (section 1), and the Cloudflare Origin CA private key, which you upload to the environment and then
delete locally (section 10). Keep this in sync with reality until it's replaced by Bicep (IaC is on the
plan).

| Section | Creates |
|---|---|
| 1 | Resource group, Log Analytics workspace, Container Apps environment |
| 2 | Container registry and its weekly purge |
| 3 | Key Vault |
| 4 | Azure SQL server (Entra-only) and both databases |
| 5 | Azure OpenAI (AI Services, key authentication off) and the model deployments |
| 6 | Identities: registry pull, and one deploy identity per GitHub environment |
| 7 | The two Container Apps and their roles |
| 8 | Database users |
| 9 | GitHub environments and variables; the first deploy |
| 10 | `lazydad.fyi` behind Cloudflare |
| 11 | Monitoring and logs: Grafana Cloud |
| 12 | Operations: checks, token rotation, Cloudflare's ranges, registry purge, manual rollback, restoring the database |

What it all looks like at the end:

| Resource | Name | Region | Settings |
|---|---|---|---|
| Resource group | `lazydad-rg` | West Europe | |
| Log Analytics workspace | `workspace-lazydadrgseCk` | West Europe | 30 days, 0.1 GB/day cap |
| Container Apps environment | `lazydad-cae` | West Europe | Consumption |
| Container registry | `lazydadacr` | West Europe | Basic, admin user off, repository permissions; `lazydad-preview` (builds, staging) and `lazydad` (production) |
| Key Vault | `lazydad-kv` | West Europe | RBAC, soft delete 90 days, no purge protection |
| SQL server | `lazydad-sql-swedencentral` | Sweden Central | Entra-only authentication, TLS 1.2 |
| Database | `lazydad-db` | | Basic (5 DTU, 2 GB); geo-redundant backups, long-term 7 weeks / 12 months; delete lock |
| Database | `lazydad-db-staging` | | serverless, free offer |
| AI Services | `lazydad-openai-resource` | East US 2 | S0, key authentication off |
| Container App | `lazydad-app` (prod) | West Europe | 1 replica, `lazydad.fyi` |
| Container App | `lazydad-app-staging` | West Europe | 0–1 replicas, your IP only |

| Identity | Kind | Signs in as it | Roles |
|---|---|---|---|
| `lazydad-app` | system-assigned | the prod app | `Foundry User` on the AI resource; `Key Vault Secrets User` on `OtlpHeaders`; read/write in `lazydad-db` |
| `lazydad-app-staging` | system-assigned | the staging app | `Foundry User` on the AI resource; `Key Vault Secrets User` on `OtlpHeadersStaging`; read/write in `lazydad-db-staging` |
| `lazydad-acr-pull` | user-assigned | both apps, to pull images | `Container Registry Repository Reader` (all repositories) |
| `lazydad-github-cd` | user-assigned | GitHub's `production` environment | `Container Registry Repository Writer` on `lazydad`, `Reader` on `lazydad-preview`, `Reader` on the registry; `Contributor` on `lazydad-app`; `LazyDad Deployer`; migrations in `lazydad-db` |
| `lazydad-github-staging` | user-assigned | GitHub's `staging` environment (branch previews too) | `Container Registry Repository Writer` on `lazydad-preview` only, `Reader` on the registry; `Contributor` on `lazydad-app-staging`; `LazyDad Deployer`; migrations in `lazydad-db-staging` |
| you | your account | local runs, this runbook | `Owner`; the SQL server's Entra admin; `Foundry User`; `Key Vault Secrets Officer`; `Container Registry Repository Contributor` + `Catalog Lister` |

## Before you start

You need the Azure CLI (`az login` as an Owner of the subscription), the GitHub CLI (`gh auth login` as the repo's
owner) and a clone of the repository: run everything from its root.

Each command block is bash (Git Bash on Windows). Under it, a collapsed **PowerShell 7** version does the same, or a
note says the bash commands run unchanged. Both use the same variable names, except `$PID` and `$ENV`, which
PowerShell reserves (process id, and too close to the `$env:` drive): those are `$APP_PID` and `$ENV_NAME` there.
Every block reads the IDs it needs itself, so a new terminal only needs the block below first. (One exception,
said where it applies: a new Grafana token needs section 11's helper.)

> In Git Bash, `export MSYS_NO_PATHCONV=1` first. Otherwise the `/subscriptions/...` scopes get
> rewritten as file paths and role assignments fail with `MissingSubscription`.

> In PowerShell on Windows, `az` is `az.cmd`, so every argument also passes through `cmd`. Arguments in single quotes
> (the PowerShell blocks use them for anything with spaces, `;`, `,`, `^` or `$`) arrive intact, but `cmd` drops the
> double quotes inside JSON (so the blocks pass JSON to `az` in files), and treats `<...>` in an argument without
> spaces as a redirection: replace every `<placeholder>` before running a command.

```bash
export MSYS_NO_PATHCONV=1   # Git Bash only
RG=lazydad-rg
REPO=mykolad/lazydad        # -R on every gh command: it then works outside the clone too
```

<details><summary>PowerShell 7</summary>

```powershell
$RG = 'lazydad-rg'
$REPO = 'mykolad/lazydad'   # -R on every gh command: it then works outside the clone too
```

</details>

An empty subscription also has to register the resource providers once (each takes a minute or two), and the
Container Apps commands come from an `az` extension:

```bash
for ns in Microsoft.App Microsoft.OperationalInsights Microsoft.ContainerRegistry Microsoft.KeyVault \
          Microsoft.Sql Microsoft.CognitiveServices Microsoft.ManagedIdentity; do
  az provider register -n $ns --wait
done
az extension add -n containerapp --upgrade
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($ns in 'Microsoft.App', 'Microsoft.OperationalInsights', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault',
                'Microsoft.Sql', 'Microsoft.CognitiveServices', 'Microsoft.ManagedIdentity') {
  az provider register -n $ns --wait
}
az extension add -n containerapp --upgrade
```

</details>

## 1. Resource group, logs and the Container Apps environment

The environment sends the apps' console output to a Log Analytics workspace: the fallback log, next to Grafana
(section 11), and the place to look when a revision doesn't start at all. 30 days, and a 0.1 GB daily cap: the apps
log about 8 MB a month (at most 2.2 MB a day, 2026-09-26), so the cap never bites in normal use but stops a logging
bug from running up a bill. The workspace's name is the one the portal generated; any name works.

```bash
az group create -n $RG -l westeurope -o none
az monitor log-analytics workspace create -g $RG -n workspace-lazydadrgseCk -l westeurope \
  --retention-time 30 --quota 0.1 -o none
# The environment writes to the workspace with its shared key: handed from one resource to the other here, never shown.
az containerapp env create -g $RG -n lazydad-cae -l westeurope --logs-destination log-analytics \
  --logs-workspace-id "$(az monitor log-analytics workspace show -g $RG -n workspace-lazydadrgseCk --query customerId -o tsv)" \
  --logs-workspace-key "$(az monitor log-analytics workspace get-shared-keys -g $RG -n workspace-lazydadrgseCk --query primarySharedKey -o tsv)" \
  -o none
```

<details><summary>PowerShell 7</summary>

```powershell
az group create -n $RG -l westeurope -o none
az monitor log-analytics workspace create -g $RG -n workspace-lazydadrgseCk -l westeurope `
  --retention-time 30 --quota 0.1 -o none
# The environment writes to the workspace with its shared key: handed from one resource to the other here, never shown.
az containerapp env create -g $RG -n lazydad-cae -l westeurope --logs-destination log-analytics `
  --logs-workspace-id (az monitor log-analytics workspace show -g $RG -n workspace-lazydadrgseCk --query customerId -o tsv) `
  --logs-workspace-key (az monitor log-analytics workspace get-shared-keys -g $RG -n workspace-lazydadrgseCk --query primarySharedKey -o tsv) `
  -o none
```

</details>

The environment gets the Consumption workload profile (pay per use, scale to zero) and a static IP, which
`lazydad.fyi` points at (section 10).

## 2. Container registry and its weekly purge

Basic tier, with the admin user (a username and password) off: everything pulls and pushes with Entra ID. Two
repositories keep branch previews away from production:

| Repository | Holds | Written by |
|---|---|---|
| `lazydad-preview` | every build (Deploy Master's and branch previews'); staging runs from it | `lazydad-github-staging` |
| `lazydad` | only what production deploys, copied from `lazydad-preview` with the same digest; production runs from it | `lazydad-github-cd` |

The registry uses **repository permissions** (the "RBAC Registry + ABAC Repository Permissions" mode): its roles can
be limited to one repository (section 6 assigns them). In this mode the older registry-wide roles (`AcrPull`,
`AcrPush`) don't work, and `Owner` or `Contributor` manage the registry but can't read or push images. So you get a
data role too, to browse the images in the portal and to build the first one (section 7).

```bash
az acr create -g $RG -n lazydadacr -l westeurope --sku Basic --admin-enabled false --role-assignment-mode rbac-abac -o none
ACR_ID=$(az acr show -n lazydadacr --query id -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)
for role in "Container Registry Repository Contributor" "Container Registry Repository Catalog Lister"; do
  az role assignment create --assignee "$ME" --role "$role" --scope "$ACR_ID" -o none   # all repositories
done
```

<details><summary>PowerShell 7</summary>

```powershell
az acr create -g $RG -n lazydadacr -l westeurope --sku Basic --admin-enabled false --role-assignment-mode rbac-abac -o none
$ACR_ID = az acr show -n lazydadacr --query id -o tsv
$ME = az ad signed-in-user show --query id -o tsv
foreach ($role in 'Container Registry Repository Contributor', 'Container Registry Repository Catalog Lister') {
  az role assignment create --assignee $ME --role $role --scope $ACR_ID -o none   # all repositories
}
```

</details>

Every build pushes a new `lazydad-preview:<short-sha>` image, and every production deploy copies one into
`lazydad`. An **ACR Task** (it runs inside the registry, on a cron schedule in UTC, and costs fractions of a cent per
run) deletes old ones from both every Sunday at 03:00 UTC. In this registry mode a task can't touch images unless it
has an identity with a role, so it gets a system-assigned one that may delete in both repositories:

```bash
az acr task create --registry lazydadacr --name purge-old-images --schedule "0 3 * * 0" \
  --cmd "acr purge --filter 'lazydad:^[0-9a-f]{7}.*$' --filter 'lazydad-preview:^[0-9a-f]{7}.*$' --ago 30d --keep 10 --untagged" \
  --context /dev/null --source-acr-auth-id "[system]" -o none
TASK_PID=$(az acr task show --registry lazydadacr --name purge-old-images --query identity.principalId -o tsv)
for role in "Container Registry Repository Contributor" "Container Registry Repository Catalog Lister"; do
  az role assignment create --assignee-object-id "$TASK_PID" --assignee-principal-type ServicePrincipal \
    --role "$role" --scope "$(az acr show -n lazydadacr --query id -o tsv)" -o none
done
```

<details><summary>PowerShell 7</summary>

```powershell
az acr task create --registry lazydadacr --name purge-old-images --schedule '0 3 * * 0' `
  --cmd 'acr purge --filter ''lazydad:^[0-9a-f]{7}.*$'' --filter ''lazydad-preview:^[0-9a-f]{7}.*$'' --ago 30d --keep 10 --untagged' `
  --context /dev/null --source-acr-auth-id '[system]' -o none
$TASK_PID = az acr task show --registry lazydadacr --name purge-old-images --query identity.principalId -o tsv
foreach ($role in 'Container Registry Repository Contributor', 'Container Registry Repository Catalog Lister') {
  az role assignment create --assignee-object-id $TASK_PID --assignee-principal-type ServicePrincipal `
    --role $role --scope (az acr show -n lazydadacr --query id -o tsv) -o none
}
```

</details>

What it keeps, in each repository:
- **every image from the last 30 days** (`--ago 30d`)
- **plus the 10 newest older images.** `--keep` counts only the tags that would otherwise be
  deleted, not all tags.
- `--untagged` removes manifests that nothing references anymore. `--keep` applies to those
  separately too: the 10 newest eligible untagged manifests are also kept, so a dry run can show fewer
  manifest deletions than you'd expect.
- **whatever an environment runs**, even if failed deploys pushed many newer images and no deploy
  succeeded for over 30 days. That takes two things together:
  1. **Revisions are pinned to the image digest** (`lazydad@sha256:…` in production, `lazydad-preview@sha256:…` in
     staging), not the commit tag. Container Apps resolves the configured image again on every replica start, so a
     revision pointing at a tag would fail to restart or scale once purge deleted that tag.
  2. **The running manifest always keeps a non-commit tag.** The filter only matches commit-style
     tags (`^[0-9a-f]{7}`), and Deploy Environment tags in two phases, so the job can stop at any point:
     - **before the rollout**, it re-tags the image of the revision **serving traffic** as `deployed-<env>` in its own repository
       (not the app's desired image, which after a failed rollout names the failed one; repairing any
       earlier interrupted run), and also as `previous-<env>` (so a manual rollback can return to it; the automatic
       rollback gets its digest from the deploy job itself).
       This is fatal on failure, and only then does it tag the new digest `deploying-<env>`;
     - **after the rollout**, it moves `deployed-<env>` to the new digest.

     Those tags survive the purge, so the manifest is never "untagged" and the pinned digest stays pullable.

Storage for context: 336 MB of Basic's 10 GB on 2026-09-25. Layers are shared, so each deploy adds only a few MB of
unique data. Section 12 shows how to preview a purge, and how to roll back by hand without losing the image.

## 3. Key Vault

It holds only what really is a secret: the Grafana Cloud write tokens (section 11). The AI endpoint and the database
connection strings contain no credentials, so they're plain app settings. Access goes through Azure roles (RBAC), per
secret where it matters: each app can read only its own token.

```bash
az keyvault create -g $RG -n lazydad-kv -l westeurope --enable-rbac-authorization true --retention-days 90 -o none
# Owner manages the vault but can't read or write secrets: that's a data role.
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Key Vault Secrets Officer" --scope "$(az keyvault show -n lazydad-kv --query id -o tsv)" -o none
```

<details><summary>PowerShell 7</summary>

```powershell
az keyvault create -g $RG -n lazydad-kv -l westeurope --enable-rbac-authorization true --retention-days 90 -o none
# Owner manages the vault but can't read or write secrets: that's a data role.
az role assignment create --assignee (az ad signed-in-user show --query id -o tsv) `
  --role 'Key Vault Secrets Officer' --scope (az keyvault show -n lazydad-kv --query id -o tsv) -o none
```

</details>

A deleted secret stays recoverable for 90 days (soft delete). Purge protection is off on purpose, so a leaked token
can also be purged at once (`az keyvault secret purge`); with it on, it would stay recoverable until the 90 days end.
The vault's name is global, and a deleted vault keeps it for the same 90 days.

## 4. Azure SQL: an Entra-only server and both databases

**Entra-only authentication** from the start: the server has no SQL logins at all, so there is no admin password to
keep, rotate or leak. You are its Entra admin; the apps and the pipelines get database users of their own in
section 8.

```bash
az sql server create -g $RG -n lazydad-sql-swedencentral -l swedencentral --minimal-tls-version 1.2 \
  --enable-ad-only-auth --external-admin-principal-type User \
  --external-admin-name "$(az ad signed-in-user show --query userPrincipalName -o tsv)" \
  --external-admin-sid "$(az ad signed-in-user show --query id -o tsv)" -o none

# Container Apps (no virtual network) reach it as "Azure services"; the login still needs an Entra identity with a
# database user. Your own IP, for local runs and the queries in section 8 (update it when it changes).
az sql server firewall-rule create -g $RG -s lazydad-sql-swedencentral -n AllowAzureServices \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none
MY_IP=$(curl -s https://api.ipify.org)
az sql server firewall-rule create -g $RG -s lazydad-sql-swedencentral -n AllowLocalDev \
  --start-ip-address $MY_IP --end-ip-address $MY_IP -o none

az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db \
  --service-objective Basic --max-size 2GB --backup-storage-redundancy Geo -o none
az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db-staging \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 --min-capacity 0.5 \
  --auto-pause-delay 60 --use-free-limit --free-limit-exhaustion-behavior AutoPause --backup-storage-redundancy Local -o none
```

<details><summary>PowerShell 7</summary>

```powershell
az sql server create -g $RG -n lazydad-sql-swedencentral -l swedencentral --minimal-tls-version 1.2 `
  --enable-ad-only-auth --external-admin-principal-type User `
  --external-admin-name (az ad signed-in-user show --query userPrincipalName -o tsv) `
  --external-admin-sid (az ad signed-in-user show --query id -o tsv) -o none

# Container Apps (no virtual network) reach it as "Azure services"; the login still needs an Entra identity with a
# database user. Your own IP, for local runs and the queries in section 8 (update it when it changes).
az sql server firewall-rule create -g $RG -s lazydad-sql-swedencentral -n AllowAzureServices `
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none
$MY_IP = Invoke-RestMethod https://api.ipify.org
az sql server firewall-rule create -g $RG -s lazydad-sql-swedencentral -n AllowLocalDev `
  --start-ip-address $MY_IP --end-ip-address $MY_IP -o none

az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db `
  --service-objective Basic --max-size 2GB --backup-storage-redundancy Geo -o none
az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db-staging `
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 --min-capacity 0.5 `
  --auto-pause-delay 60 --use-free-limit --free-limit-exhaustion-behavior AutoPause --backup-storage-redundancy Local -o none
```

</details>

- **Prod is Basic** (about $5 a month), not serverless: the scheduler wakes the database every 4 hours, so a
  serverless one would hardly ever pause, and cost about $74 a month.
- **Staging is serverless on the free offer** (100k vCore-seconds a month, one database per subscription). It pauses
  when idle; if the free amount runs out it stays paused until next month, and staging deploys fail until then.
- Deploys add a firewall rule for their runner while migrating, and remove it afterwards.

**Prod's backups.** Azure SQL backs up every database by itself; these settings decide how long the backups are kept,
where, and what protects them. Staging keeps the defaults (7 days, one datacenter): its data is test data.

| Backup | Kept | Restores |
|---|---|---|
| Point-in-time (full weekly, differential every 12 hours (the setting: 12 or 24), transaction log about every 10 minutes) | 7 days (Basic's maximum) | Any second in those 7 days |
| Long-term: each week's full backup | 7 weeks | That backup, even after the server is deleted |
| Long-term: each month's first weekly full backup | 12 months | That backup, even after the server is deleted |

- **Geo-redundant storage** (`--backup-storage-redundancy Geo` above): the backups are also copied to the paired
  region, so a geo-restore into another region works while Sweden Central is down. Point-in-time backups up to the
  database's size (2 GB) are free, and its 33 MB of data (2026-09-29) stays far below that; long-term backups cost a
  few cents a month.
- **A delete lock** on the database: deleting it, or the server or resource group with it, fails until the lock is
  removed. It's on the database, not the server: there it would also block deleting the server's child resources,
  which the deploys' temporary SQL firewall rules are.

```bash
az sql db ltr-policy set -g $RG -s lazydad-sql-swedencentral -n lazydad-db \
  --weekly-retention P7W --monthly-retention P12M -o none
az lock create -n lazydad-db-no-delete -t CanNotDelete -g $RG --namespace Microsoft.Sql \
  --parent servers/lazydad-sql-swedencentral --resource-type databases --resource lazydad-db \
  --notes "The jokes and votes. Remove it only to restore (runbook section 12) or to retire the app." -o none
```

<details><summary>PowerShell 7</summary>

```powershell
az sql db ltr-policy set -g $RG -s lazydad-sql-swedencentral -n lazydad-db `
  --weekly-retention P7W --monthly-retention P12M -o none
az lock create -n lazydad-db-no-delete -t CanNotDelete -g $RG --namespace Microsoft.Sql `
  --parent servers/lazydad-sql-swedencentral --resource-type databases --resource lazydad-db `
  --notes 'The jokes and votes. Remove it only to restore (runbook section 12) or to retire the app.' -o none
```

</details>

The first long-term backup appears within a week: it's the next weekly full backup. Section 12 has how to restore.

## 5. Azure OpenAI: AI Services without keys

One AI Services resource holds every model the app calls (each `LlmModels[].Model` in `appsettings.json` is a
deployment name). Key authentication is off from the start: the apps call it as their managed identities, and you
as your `az login`.

```bash
az cognitiveservices account create -g $RG -n lazydad-openai-resource -l eastus2 --kind AIServices --sku S0 \
  --custom-domain lazydad-openai-resource --yes -o none
az resource update -g $RG -n lazydad-openai-resource --resource-type Microsoft.CognitiveServices/accounts \
  --set properties.disableLocalAuth=true -o none

# name, model format, version, capacity (thousands of tokens per minute), all "Global Standard"
deploy_model() {
  az cognitiveservices account deployment create -g $RG -n lazydad-openai-resource --deployment-name "$1" \
    --model-name "$1" --model-format "$2" --model-version "$3" --sku-name GlobalStandard --sku-capacity "$4" -o none
}
deploy_model Kimi-K2.5  MoonshotAI 1          1    # joke writer
deploy_model gpt-6-luna OpenAI     2026-09-22 10   # joke writer
deploy_model gpt-6-sol  OpenAI     2026-09-22 10   # the leaderboard's judge

# Your own inference rights, for local runs:
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" --role "Foundry User" \
  --scope "$(az cognitiveservices account show -g $RG -n lazydad-openai-resource --query id -o tsv)" -o none
```

<details><summary>PowerShell 7</summary>

```powershell
az cognitiveservices account create -g $RG -n lazydad-openai-resource -l eastus2 --kind AIServices --sku S0 `
  --custom-domain lazydad-openai-resource --yes -o none
az resource update -g $RG -n lazydad-openai-resource --resource-type Microsoft.CognitiveServices/accounts `
  --set properties.disableLocalAuth=true -o none

# name, model format, version, capacity (thousands of tokens per minute), all "Global Standard"
function Add-ModelDeployment($Name, $Format, $Version, $Capacity) {
  az cognitiveservices account deployment create -g $RG -n lazydad-openai-resource --deployment-name $Name `
    --model-name $Name --model-format $Format --model-version $Version --sku-name GlobalStandard --sku-capacity $Capacity -o none
}
Add-ModelDeployment Kimi-K2.5  MoonshotAI '1'          1    # joke writer
Add-ModelDeployment gpt-6-luna OpenAI     '2026-09-22' 10   # joke writer
Add-ModelDeployment gpt-6-sol  OpenAI     '2026-09-22' 10   # the leaderboard's judge

# Your own inference rights, for local runs:
az role assignment create --assignee (az ad signed-in-user show --query id -o tsv) --role 'Foundry User' `
  --scope (az cognitiveservices account show -g $RG -n lazydad-openai-resource --query id -o tsv) -o none
```

</details>

- The deployments update themselves when a new default version of the model comes out.
- **"Foundry User"** covers both the OpenAI models and the others (Kimi-K2.5, which the app also calls through the
  Azure OpenAI chat API); "Cognitive Services OpenAI User" is narrower but may not cover non-OpenAI models.
- The app's endpoint setting is `https://lazydad-openai-resource.cognitiveservices.azure.com/` (the custom domain).
- The app uses Entra ID whenever `LlmProviders:AzureOpenAI:ApiKey` is empty, which it is everywhere:
  `DefaultAzureCredential` picks the app's managed identity in Azure, and your `az login` locally.

## 6. Identities: pulling images, and a deploy identity per environment

**Pulling.** Both apps pull images as one user-assigned identity, `lazydad-acr-pull`, which can do nothing else.

**Deploying.** GitHub Actions signs in to Azure with OpenID Connect (OIDC): each job gets a short-lived token from
GitHub, and Azure swaps it for one of a managed identity that trusts it. There's no stored credential at all. There
are two deploy identities, one per GitHub environment, because Deploy Branch to Staging runs a branch's own code in
the `staging` environment: its workflow files, and its `dotnet build` (which can run any MSBuild target a package
brings along). So a previewed branch, say one with a compromised package in a dependency update, has everything
staging's identity has. That's why it has nothing in production:

| GitHub environment | Signs in as | Can change |
|---|---|---|
| `staging` (`*/*` branches and `master`) | `lazydad-github-staging` | `lazydad-app-staging`; images in `lazydad-preview`; SQL firewall rules; the staging DB schema |
| `production` (`master` only) | `lazydad-github-cd` | `lazydad-app`; images in `lazydad` (and it reads `lazydad-preview`, to copy from it); SQL firewall rules; the prod DB schema |

Staging can't write production's repository at all (section 2), so a branch can't move production's tags
(`deployed-production`, `previous-production`) off the image it runs, which would let the weekly purge delete it.
And nothing that decides what production runs trusts a tag anyway: Deploy Master deploys the digest its own build
produced (checked again after the copy into `lazydad`), its automatic rollback returns to the digest the production
job read from production's revisions (a job output), and the Roll Back workflow only accepts an image whose digest
production's own revisions ran.

**1. A custom role for what a deploy needs outside its app.** The built-in roles reach too far: `Contributor` on the
Container Apps environment could delete it (and the prod app with it), and `SQL Server Contributor` could delete
the prod database. A deploy only has to *join* the environment (`az containerapp update` checks that) and open and
close its SQL firewall rule:

```bash
RG_ID=$(az group show -n $RG --query id -o tsv)
cat > lazydad-deployer.json <<JSON
{
  "Name": "LazyDad Deployer",
  "Description": "Deploy Environment's needs outside the app it deploys: join the Container Apps environment, open and close SQL firewall rules.",
  "Actions": [
    "Microsoft.App/managedEnvironments/read",
    "Microsoft.App/managedEnvironments/join/action",
    "Microsoft.Sql/servers/read",
    "Microsoft.Sql/servers/firewallRules/read",
    "Microsoft.Sql/servers/firewallRules/write",
    "Microsoft.Sql/servers/firewallRules/delete"
  ],
  "AssignableScopes": ["$RG_ID"]
}
JSON
az role definition create --role-definition @lazydad-deployer.json -o none
rm lazydad-deployer.json
```

<details><summary>PowerShell 7</summary>

```powershell
$RG_ID = az group show -n $RG --query id -o tsv
# A file, not an argument: az.cmd would strip the JSON's quotes (see "Before you start").
[ordered]@{
  Name             = 'LazyDad Deployer'
  Description      = "Deploy Environment's needs outside the app it deploys: join the Container Apps environment, open and close SQL firewall rules."
  Actions          = @(
    'Microsoft.App/managedEnvironments/read',
    'Microsoft.App/managedEnvironments/join/action',
    'Microsoft.Sql/servers/read',
    'Microsoft.Sql/servers/firewallRules/read',
    'Microsoft.Sql/servers/firewallRules/write',
    'Microsoft.Sql/servers/firewallRules/delete'
  )
  AssignableScopes = @($RG_ID)
} | ConvertTo-Json | Set-Content lazydad-deployer.json
az role definition create --role-definition '@lazydad-deployer.json' -o none
Remove-Item lazydad-deployer.json
```

</details>

**2. The identities, their roles, and each deploy identity's trust in its environment only.** The federated
credentials use GitHub's **immutable subject** format, `repo:<owner>@<owner_id>/<repo>@<repo_id>:environment:<env>`.
This repo emits it (it was created after 2026-07-15; `gh api repos/mykolad/lazydad/actions/oidc/customization/sub`
shows `use_immutable_subject: true`). The numeric IDs never change or get reused, so a renamed, transferred or
re-created repository can't inherit the trust. Name-based subjects (`repo:mykolad/lazydad:...`) would never match
this repo's tokens. A role assignment right after an identity is created can fail with "principal not found": run
it again a minute later.

The registry roles are limited to one repository by a **condition** on the role assignment. It lets the role's
actions through only for requests to that repository; without one, the role covers every repository:

| Identity | Registry roles |
|---|---|
| `lazydad-acr-pull` | `Container Registry Repository Reader`, all repositories (it only pulls) |
| `lazydad-github-staging` | `Container Registry Repository Writer` on `lazydad-preview` |
| `lazydad-github-cd` | `Container Registry Repository Writer` on `lazydad`; `Container Registry Repository Reader` on `lazydad-preview` |

Both deploy identities also get `Reader` on the registry: `az acr login` looks the registry up in Azure Resource
Manager (in this registry mode, `Reader` grants nothing on the images).

```bash
RG_ID=$(az group show -n $RG --query id -o tsv)
ACR_ID=$(az acr show -n lazydadacr --query id -o tsv)
PREFIX="repo:mykolad@$(gh api users/mykolad --jq .id)/lazydad@$(gh api repos/$REPO --jq .id)"
READ="content/read metadata/read"; WRITE="$READ content/write metadata/write"
# A registry role for one repository: $1 the identity's principal id, $2 the role, $3 the repository, $4 its actions.
repo_role() {
  local not="" action
  for action in $4; do
    not+="${not:+ AND }!(ActionMatches{'Microsoft.ContainerRegistry/registries/repositories/$action'})"
  done
  az role assignment create --assignee-object-id "$1" --assignee-principal-type ServicePrincipal --role "$2" \
    --scope "$ACR_ID" --condition-version 2.0 -o none \
    --condition "(($not) OR (@Request[Microsoft.ContainerRegistry/registries/repositories:name] StringEqualsIgnoreCase '$3'))"
}

az identity create -g $RG -n lazydad-acr-pull -l westeurope -o none
az role assignment create --assignee-object-id "$(az identity show -g $RG -n lazydad-acr-pull --query principalId -o tsv)" \
  --assignee-principal-type ServicePrincipal --role "Container Registry Repository Reader" --scope "$ACR_ID" -o none

for pair in lazydad-github-cd:production lazydad-github-staging:staging; do
  id=${pair%%:*}; env=${pair#*:}
  az identity create -g $RG -n $id -l westeurope -o none
  principal=$(az identity show -g $RG -n $id --query principalId -o tsv)
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal \
    --role Reader --scope "$ACR_ID" -o none
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal \
    --role "LazyDad Deployer" --scope "$RG_ID" -o none
  az identity federated-credential create -g $RG --identity-name $id -n github-$env-immutable \
    --issuer https://token.actions.githubusercontent.com \
    --subject "$PREFIX:environment:$env" --audiences api://AzureADTokenExchange -o none
done

STAGING=$(az identity show -g $RG -n lazydad-github-staging --query principalId -o tsv)
CD=$(az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv)
repo_role "$STAGING" "Container Registry Repository Writer" lazydad-preview "$WRITE"
repo_role "$CD" "Container Registry Repository Writer" lazydad "$WRITE"
repo_role "$CD" "Container Registry Repository Reader" lazydad-preview "$READ"
```

<details><summary>PowerShell 7</summary>

```powershell
$RG_ID = az group show -n $RG --query id -o tsv
$ACR_ID = az acr show -n lazydadacr --query id -o tsv
$PREFIX = "repo:mykolad@$(gh api users/mykolad --jq .id)/lazydad@$(gh api repos/$REPO --jq .id)"
$READ = @('content/read', 'metadata/read'); $WRITE = $READ + @('content/write', 'metadata/write')
# A registry role for one repository.
function Add-RepositoryRole([string]$Principal, [string]$Role, [string]$Repository, [string[]]$Actions) {
  $not = ($Actions | ForEach-Object { "!(ActionMatches{'Microsoft.ContainerRegistry/registries/repositories/$_'})" }) -join ' AND '
  az role assignment create --assignee-object-id $Principal --assignee-principal-type ServicePrincipal --role $Role `
    --scope $ACR_ID --condition-version 2.0 -o none `
    --condition "(($not) OR (@Request[Microsoft.ContainerRegistry/registries/repositories:name] StringEqualsIgnoreCase '$Repository'))"
}

az identity create -g $RG -n lazydad-acr-pull -l westeurope -o none
az role assignment create --assignee-object-id (az identity show -g $RG -n lazydad-acr-pull --query principalId -o tsv) `
  --assignee-principal-type ServicePrincipal --role 'Container Registry Repository Reader' --scope $ACR_ID -o none

foreach ($pair in @{ Id = 'lazydad-github-cd'; Env = 'production' }, @{ Id = 'lazydad-github-staging'; Env = 'staging' }) {
  az identity create -g $RG -n $pair.Id -l westeurope -o none
  $principal = az identity show -g $RG -n $pair.Id --query principalId -o tsv
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal `
    --role Reader --scope $ACR_ID -o none
  az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal `
    --role 'LazyDad Deployer' --scope $RG_ID -o none
  # ${PREFIX} in braces: "$PREFIX:environment" would read as a scoped variable.
  az identity federated-credential create -g $RG --identity-name $pair.Id -n "github-$($pair.Env)-immutable" `
    --issuer https://token.actions.githubusercontent.com `
    --subject "${PREFIX}:environment:$($pair.Env)" --audiences api://AzureADTokenExchange -o none
}

$STAGING = az identity show -g $RG -n lazydad-github-staging --query principalId -o tsv
$CD = az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv
Add-RepositoryRole $STAGING 'Container Registry Repository Writer' lazydad-preview $WRITE
Add-RepositoryRole $CD 'Container Registry Repository Writer' lazydad $WRITE
Add-RepositoryRole $CD 'Container Registry Repository Reader' lazydad-preview $READ
```

</details>

Each deploy identity also gets `Contributor` on its own app, once the apps exist (section 7), and a database user
for the migrations (section 8).

## 7. The Container Apps

**1. A first image.** A Container App can't be created without one, and GitHub can't deploy yet (section 9). ACR
Tasks builds it inside the registry from your clone (no Docker needed; it uploads the source, minus
`.dockerignore`'s entries), as you: in this registry mode a build can only push where its caller may (section 2). It
goes into both repositories with the same digest, since each app runs from its own. It reports version `dev`, and
the first Deploy Master run replaces it.

```bash
az acr build -r lazydadacr --source-acr-auth-id "[caller]" -t lazydad-preview:bootstrap -t lazydad:bootstrap .
```

<details><summary>PowerShell 7</summary>

```powershell
az acr build -r lazydadacr --source-acr-auth-id '[caller]' -t lazydad-preview:bootstrap -t lazydad:bootstrap .
```

</details>

**2. The apps.** Both are pinned to the image's digest (section 2), pull as `lazydad-acr-pull`, and get a
system-assigned identity for everything else. The settings hold no secrets: the database connection string names
the identity to sign in with (`Authentication=Active Directory Managed Identity`, the app's own system-assigned
identity when there's no `User Id`), and the AI endpoint is an address. Deploys keep these settings: Deploy
Environment only swaps the image.

```bash
DIGEST=$(az acr repository show -n lazydadacr --image lazydad:bootstrap --query digest -o tsv)
PULL_ID=$(az identity show -g $RG -n lazydad-acr-pull --query id -o tsv)
SQL="Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Authentication=Active Directory Managed Identity;Encrypt=True"
OPENAI=https://lazydad-openai-resource.cognitiveservices.azure.com/
# The pull identity's role (section 6) must have applied; if the create fails to pull, wait a minute and retry.

az containerapp create -g $RG -n lazydad-app --environment lazydad-cae --image "lazydadacr.azurecr.io/lazydad@$DIGEST" \
  --system-assigned --user-assigned "$PULL_ID" --registry-server lazydadacr.azurecr.io --registry-identity "$PULL_ID" \
  --ingress external --target-port 8080 --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1Gi \
  --env-vars "ConnectionStrings__DefaultConnection=$SQL;Database=lazydad-db" LlmProviders__AzureOpenAI__Endpoint=$OPENAI -o none

az containerapp create -g $RG -n lazydad-app-staging --environment lazydad-cae --image "lazydadacr.azurecr.io/lazydad-preview@$DIGEST" \
  --system-assigned --user-assigned "$PULL_ID" --registry-server lazydadacr.azurecr.io --registry-identity "$PULL_ID" \
  --ingress external --target-port 8080 --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1Gi \
  --env-vars "ConnectionStrings__DefaultConnection=$SQL;Database=lazydad-db-staging" LlmProviders__AzureOpenAI__Endpoint=$OPENAI -o none
```

<details><summary>PowerShell 7</summary>

```powershell
$DIGEST = az acr repository show -n lazydadacr --image lazydad:bootstrap --query digest -o tsv
$PULL_ID = az identity show -g $RG -n lazydad-acr-pull --query id -o tsv
$SQL = 'Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Authentication=Active Directory Managed Identity;Encrypt=True'
$OPENAI = 'https://lazydad-openai-resource.cognitiveservices.azure.com/'
# The pull identity's role (section 6) must have applied; if the create fails to pull, wait a minute and retry.

az containerapp create -g $RG -n lazydad-app --environment lazydad-cae --image "lazydadacr.azurecr.io/lazydad@$DIGEST" `
  --system-assigned --user-assigned $PULL_ID --registry-server lazydadacr.azurecr.io --registry-identity $PULL_ID `
  --ingress external --target-port 8080 --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1Gi `
  --env-vars "ConnectionStrings__DefaultConnection=$SQL;Database=lazydad-db" "LlmProviders__AzureOpenAI__Endpoint=$OPENAI" -o none

az containerapp create -g $RG -n lazydad-app-staging --environment lazydad-cae --image "lazydadacr.azurecr.io/lazydad-preview@$DIGEST" `
  --system-assigned --user-assigned $PULL_ID --registry-server lazydadacr.azurecr.io --registry-identity $PULL_ID `
  --ingress external --target-port 8080 --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1Gi `
  --env-vars "ConnectionStrings__DefaultConnection=$SQL;Database=lazydad-db-staging" "LlmProviders__AzureOpenAI__Endpoint=$OPENAI" -o none
```

</details>

- **Prod runs exactly one replica** (min = max = 1). **Staging scales to zero** when idle, and every cold start runs
  a real joke tick (LLM tokens).
- The first revisions' startup ticks fail, and that's expected: the databases have no users or schema yet. Deploy
  Master's first run (section 9) is the real test.

**3. Their roles.** Each app's own identity can call the models; each deploy identity can change its own app only.
(Their Key Vault roles come with the Grafana tokens, section 11.)

```bash
OPENAI_ID=$(az cognitiveservices account show -g $RG -n lazydad-openai-resource --query id -o tsv)
for pair in lazydad-app:lazydad-github-cd lazydad-app-staging:lazydad-github-staging; do
  app=${pair%%:*}; deployer=${pair#*:}
  az role assignment create --assignee-object-id "$(az containerapp show -g $RG -n $app --query identity.principalId -o tsv)" \
    --assignee-principal-type ServicePrincipal --role "Foundry User" --scope "$OPENAI_ID" -o none
  az role assignment create --assignee-object-id "$(az identity show -g $RG -n $deployer --query principalId -o tsv)" \
    --assignee-principal-type ServicePrincipal --role Contributor \
    --scope "$(az containerapp show -g $RG -n $app --query id -o tsv)" -o none
done
```

<details><summary>PowerShell 7</summary>

```powershell
$OPENAI_ID = az cognitiveservices account show -g $RG -n lazydad-openai-resource --query id -o tsv
foreach ($pair in @{ App = 'lazydad-app'; Deployer = 'lazydad-github-cd' }, @{ App = 'lazydad-app-staging'; Deployer = 'lazydad-github-staging' }) {
  az role assignment create --assignee-object-id (az containerapp show -g $RG -n $pair.App --query identity.principalId -o tsv) `
    --assignee-principal-type ServicePrincipal --role 'Foundry User' --scope $OPENAI_ID -o none
  az role assignment create --assignee-object-id (az identity show -g $RG -n $pair.Deployer --query principalId -o tsv) `
    --assignee-principal-type ServicePrincipal --role Contributor `
    --scope (az containerapp show -g $RG -n $pair.App --query id -o tsv) -o none
}
```

</details>

**4. Staging answers your IP only.** Staging is public by default, and every cold start costs LLM tokens, so any
visitor or crawler would too. With at least one `Allow` rule, Container Apps denies everyone else
(`403 RBAC: access denied`, answered at the ingress without waking the app):

```bash
az containerapp ingress access-restriction set -g $RG -n lazydad-app-staging \
  --rule-name home --ip-address "$(curl -s https://api.ipify.org)/32" --action Allow --description "Owner's home IP" -o none
```

<details><summary>PowerShell 7</summary>

```powershell
az containerapp ingress access-restriction set -g $RG -n lazydad-app-staging `
  --rule-name home --ip-address "$(Invoke-RestMethod https://api.ipify.org)/32" --action Allow --description "Owner's home IP" -o none
```

</details>

Deploy Environment lets its runner through for the smoke tests, for any app with Allow rules (staging, and prod once
it's behind Cloudflare), and removes it afterwards. If your home IP changes, run the command again: it updates the
`home` rule (and do the same for the SQL server's `AllowLocalDev` rule).

## 8. Database users

Nobody has a password: every user is an Entra identity, created by you as the server's Entra admin. Each app can
read and write its own database only, and each environment's deploy identity can also change that database's
schema (the migrations).

| User (an Entra identity) | Database | Roles |
|---|---|---|
| `lazydad-app` (the prod app's system-assigned identity) | `lazydad-db` | `db_datareader`, `db_datawriter` |
| `lazydad-github-cd` (production's migrations) | `lazydad-db` | `db_ddladmin`, `db_datareader`, `db_datawriter` |
| `lazydad-app-staging` (the staging app's system-assigned identity) | `lazydad-db-staging` | `db_datareader`, `db_datawriter` |
| `lazydad-github-staging` (staging's migrations) | `lazydad-db-staging` | `db_ddladmin`, `db_datareader`, `db_datawriter` |

Their object IDs:

```bash
for app in lazydad-app lazydad-app-staging; do
  echo "$app: $(az containerapp show -g $RG -n $app --query identity.principalId -o tsv)"
done
for id in lazydad-github-cd lazydad-github-staging; do
  echo "$id: $(az identity show -g $RG -n $id --query principalId -o tsv)"
done
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($app in 'lazydad-app', 'lazydad-app-staging') {
  "${app}: $(az containerapp show -g $RG -n $app --query identity.principalId -o tsv)"
}
foreach ($id in 'lazydad-github-cd', 'lazydad-github-staging') {
  "${id}: $(az identity show -g $RG -n $id --query principalId -o tsv)"
}
```

</details>

Then run these in each database: the portal's *Query editor* (signed in with Entra ID), Azure Data Studio, or
`sqlcmd -G`. `WITH OBJECT_ID` skips the directory lookup by name, but the user name must still **start with the
identity's display name**, and a system-assigned identity is named after its app: so staging's app user is
`[lazydad-app-staging]`.

```sql
-- In lazydad-db:
CREATE USER [lazydad-app] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<lazydad-app id>';
ALTER ROLE db_datareader ADD MEMBER [lazydad-app];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-app];
CREATE USER [lazydad-github-cd] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<lazydad-github-cd id>';
ALTER ROLE db_ddladmin ADD MEMBER [lazydad-github-cd];
ALTER ROLE db_datareader ADD MEMBER [lazydad-github-cd];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-github-cd];

-- In lazydad-db-staging:
CREATE USER [lazydad-app-staging] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<lazydad-app-staging id>';
ALTER ROLE db_datareader ADD MEMBER [lazydad-app-staging];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-app-staging];
CREATE USER [lazydad-github-staging] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<lazydad-github-staging id>';
ALTER ROLE db_ddladmin ADD MEMBER [lazydad-github-staging];
ALTER ROLE db_datareader ADD MEMBER [lazydad-github-staging];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-github-staging];
```

Check, in each database:

```sql
SELECT p.name, r.name AS role
FROM sys.database_principals p
JOIN sys.database_role_members m ON m.member_principal_id = p.principal_id
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
WHERE p.type = 'E' ORDER BY p.name, r.name;
```

The migrations sign in the same way: Deploy Environment runs them after `azure/login` as the environment's deploy
identity, with `Authentication=Active Directory Default`, which finds that sign-in through the Azure CLI.

## 9. GitHub: environments, variables, and the first deploy

Jobs sign in to Azure only through an environment (the federated subjects are `environment:staging` and
`environment:production`), so a workflow that doesn't use one can't get an Azure token. Both environments accept
`master`; `staging` also accepts `*/*` branches, for Deploy Branch to Staging (in GitHub's patterns `*` doesn't
cross `/`, so `*/*` matches `feature/x`). `production` stays master-only. The federated credential matches the
environment, not the branch.

Which identity a job signs in as is `vars.AZURE_CLIENT_ID`, set per environment. Nothing here is a secret: the
client, tenant and subscription IDs only identify things, and only a token from this repository's environment can
use them. The repository has **no secrets** at all.

```bash
for env in staging production; do
  echo '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}' |
    gh api -X PUT repos/$REPO/environments/$env --input - > /dev/null
  gh api -X POST repos/$REPO/environments/$env/deployment-branch-policies -f name=master -f type=branch > /dev/null
done
gh api -X POST repos/$REPO/environments/staging/deployment-branch-policies -f name='*/*' -f type=branch > /dev/null

gh variable set AZURE_CLIENT_ID -R $REPO --env production --body "$(az identity show -g $RG -n lazydad-github-cd --query clientId -o tsv)"
gh variable set AZURE_CLIENT_ID -R $REPO --env staging    --body "$(az identity show -g $RG -n lazydad-github-staging --query clientId -o tsv)"
gh variable set AZURE_TENANT_ID       -R $REPO --body "$(az account show --query tenantId -o tsv)"
gh variable set AZURE_SUBSCRIPTION_ID -R $REPO --body "$(az account show --query id -o tsv)"
# Roll Back Production never picks an image older than this commit (#30's merge: the first image without API keys).
gh variable set ROLLBACK_MIN_COMMIT -R $REPO --body 27d5dc5838996ce2101f4b19287a962fc710ad74
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($envName in 'staging', 'production') {
  '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}' |
    gh api -X PUT "repos/$REPO/environments/$envName" --input - | Out-Null
  gh api -X POST "repos/$REPO/environments/$envName/deployment-branch-policies" -f name=master -f type=branch | Out-Null
}
gh api -X POST "repos/$REPO/environments/staging/deployment-branch-policies" -f 'name=*/*' -f type=branch | Out-Null

gh variable set AZURE_CLIENT_ID -R $REPO --env production --body (az identity show -g $RG -n lazydad-github-cd --query clientId -o tsv)
gh variable set AZURE_CLIENT_ID -R $REPO --env staging    --body (az identity show -g $RG -n lazydad-github-staging --query clientId -o tsv)
gh variable set AZURE_TENANT_ID       -R $REPO --body (az account show --query tenantId -o tsv)
gh variable set AZURE_SUBSCRIPTION_ID -R $REPO --body (az account show --query id -o tsv)
# Roll Back Production never picks an image older than this commit (#30's merge: the first image without API keys).
gh variable set ROLLBACK_MIN_COMMIT -R $REPO --body 27d5dc5838996ce2101f4b19287a962fc710ad74
```

</details>

In the portal: *Settings → Environments → staging / production* shows each environment's branches and variables;
*Settings → Secrets and variables → Actions → Variables* the repository's.

**The first deploy.** Deploy Master builds the image (as staging's identity), migrates and rolls out staging, runs
its smoke tests, then does the same for production. A green run means every sign-in in this runbook works:
registry push and pull, the migrations as each deploy identity, and each app's database and model calls (the smoke
tests wait for the new revision's startup tick to save a joke; a model that fails or is slow shows up in Grafana, not
in the deploy).

```bash
gh workflow run deploy-master.yml -R $REPO --ref master
sleep 5   # until the run is listed
gh run watch -R $REPO "$(gh run list -R $REPO --workflow deploy-master.yml --limit 1 --json databaseId --jq '.[0].databaseId')"
curl -s "https://$(az containerapp show -g $RG -n lazydad-app --query properties.configuration.ingress.fqdn -o tsv)/status"
```

<details><summary>PowerShell 7</summary>

```powershell
gh workflow run deploy-master.yml -R $REPO --ref master
Start-Sleep 5   # until the run is listed
gh run watch -R $REPO (gh run list -R $REPO --workflow deploy-master.yml --limit 1 --json databaseId --jq '.[0].databaseId')
Invoke-RestMethod "https://$(az containerapp show -g $RG -n lazydad-app --query properties.configuration.ingress.fqdn -o tsv)/status"
```

</details>

`/status` shows the version, the revision, the process's id, each joke it saved as it went, and, once each language's
startup tick is done, the tick's result: a joke from every model, leaderboard not `failed`. The smoke tests only wait
for the first joke per language, so right after a deploy the tick can still be running (a thinking model can take
minutes); a model that fails or answers empty is for the Grafana alerts.
If the run fails at the Azure sign-in, compare the environment's `AZURE_CLIENT_ID` with the identity's client ID and
its federated credential's subject (section 6); at the migrations, check the deploy identity's database user
(section 8).

## 10. `lazydad.fyi` behind Cloudflare: DDoS and bot protection

Production gets a domain, `lazydad.fyi` (bought through Cloudflare Registrar, so its DNS is already on Cloudflare),
served through Cloudflare's proxy on the **Free** plan:

- **DDoS protection:** unmetered, included.
- **Bots:** Block AI bots, AI Labyrinth, Bot Fight Mode, and a rate-limiting rule on votes. None of this makes the
  site "humans only": an agent driving a real browser passes. Turnstile on votes is issue #35.
- **The origin only answers Cloudflare:** otherwise anyone could skip all of it through the app's
  `*.azurecontainerapps.io` address.

Staging stays on its Azure address, open to your IP only. The app takes the visitor's address from Cloudflare's
`CF-Connecting-IP` header, but only for requests from Cloudflare's ranges (`CloudflareClientAddressMiddleware`, ranges
in `appsettings.json`). Otherwise the vote rate limit would count every visitor behind the same Cloudflare edge
server as one.

**1. Check the zone** (nothing to change, normally). A *zone* is Cloudflare's name for a domain in your account: its
DNS records and all its settings. Open `lazydad.fyi` in the dashboard; its **Overview** should say plan **Free** and
status **Active**. Active means the domain's nameservers are Cloudflare's, so the records and settings you add there
are the ones the internet sees. Cloudflare Registrar sets up both when you buy a domain through it; a domain bought
elsewhere would need its nameservers changed at that registrar first, and then a wait until the zone is Active.

**2. A certificate between Cloudflare and Azure.** Container Apps' free managed certificate can't be issued or
renewed behind Cloudflare's proxy, so the origin uses a free **Cloudflare Origin CA** certificate, valid for 15
years. Browsers don't trust it, but Cloudflare does, and only Cloudflare connects to the origin. In the dashboard:
*SSL/TLS → Origin Server → Create Certificate*, key type RSA (2048), hostnames `lazydad.fyi` and `*.lazydad.fyi`,
validity 15 years. Save the certificate as `origin.pem` and the private key as `origin.key`. The key is shown only
once. Then turn them into a PFX (no password: it only exists for a minute) and upload it to the environment:

```bash
openssl pkcs12 -export -in origin.pem -inkey origin.key -out origin.pfx -passout pass:
az containerapp env certificate upload -g $RG -n lazydad-cae --certificate-name lazydad-fyi-origin --certificate-file origin.pfx
rm origin.key origin.pfx   # the key lives in the environment now; a new one is a new certificate (step 2 again)
```

<details><summary>PowerShell 7</summary>

```powershell
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile("$PWD/origin.pem", "$PWD/origin.key")
[IO.File]::WriteAllBytes("$PWD/origin.pfx", $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
az containerapp env certificate upload -g $RG -n lazydad-cae --certificate-name lazydad-fyi-origin --certificate-file origin.pfx
Remove-Item origin.key, origin.pfx   # the key lives in the environment now; a new one is a new certificate (step 2 again)
```

</details>

**3. Point the domain at the prod app, not proxied yet.** Azure checks that you own the domain (a TXT record) and
where it points (for an apex domain, an A record to the environment's IP) before it accepts the hostname. So first
add these in *DNS → Records*, both **DNS only** (grey cloud):

| Type | Name | Content |
|---|---|---|
| `A` | `lazydad.fyi` (`@`) | the environment's IP, first command below |
| `TXT` | `asuid` | the app's verification ID, second command below |

```bash
az containerapp env show -n lazydad-cae -g $RG --query properties.staticIp -o tsv
az containerapp show -n lazydad-app -g $RG --query properties.customDomainVerificationId -o tsv
# Once both records resolve (a minute or two):
az containerapp hostname add --hostname lazydad.fyi -n lazydad-app -g $RG
az containerapp hostname bind --hostname lazydad.fyi -n lazydad-app -g $RG --environment lazydad-cae --certificate lazydad-fyi-origin
curl -sk https://lazydad.fyi/healthz   # -k: straight to Azure, whose Origin CA certificate only Cloudflare trusts
```

*PowerShell 7: the same commands, with `curl.exe` instead of `curl`, which PowerShell aliases in some setups.*

**4. Turn on the proxy and the protections.**

- *SSL/TLS → Overview*: encryption mode **Full (strict)** (Cloudflare to Azure is encrypted and the certificate
  checked). *SSL/TLS → Edge Certificates*: **Always Use HTTPS** on, **Minimum TLS Version** 1.2.
- *DNS → Records*: switch the `A` record to **Proxied** (orange cloud). Leave the `TXT` record as it is.
  `curl -s https://lazydad.fyi/healthz` now works without `-k`: browsers get Cloudflare's certificate.
- *Security → Bots*: **Block AI bots** (on all pages), **AI Labyrinth** on, **Bot Fight Mode** on. On the Free plan
  no rule can make an exception to Bot Fight Mode, so check that Grafana's uptime check (section 11) passes; turn
  Bot Fight Mode off if it gets challenged.
- *Security → WAF → Rate limiting rules* (the Free plan has one, with a fixed 10-second period and block, and only
  the path and verified-bot fields): name "Votes", expression `(http.request.uri.path contains "/vote")` (only POST
  is served there), counted per IP, 20 requests per 10 seconds, action **Block**, duration 10 seconds. Cloudflare
  counts **per data center**, and one visitor's requests can reach more than one (tests from home went to both
  Tallinn and Riga), so it's a coarse flood guard. The app's own limit (30 a minute per visitor) is the exact
  one, and stays behind it. To test, send no-op votes (they change no counts) and note which data center answered
  (the end of the `CF-RAY` header). A Cloudflare block is a `429` with "error code: 1015"; the app's is a `429`
  with an empty body:

  ```bash
  id=$(curl -s "https://lazydad.fyi/jokes/feed?sort=new&limit=1" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
  for i in $(seq 1 50); do
    code=$(curl -s -D /tmp/h.txt -o /tmp/vote.txt -w '%{http_code}' -X POST "https://lazydad.fyi/jokes/$id/vote" \
      -H 'Content-Type: application/json' -d '{"value":0,"previous":0}')
    echo "$code-$(grep -q 'error code: 1015' /tmp/vote.txt && echo cloudflare || echo app) $(grep -i '^cf-ray' /tmp/h.txt | grep -o '[A-Z]\{3\}' | tail -1)"
  done | sort | uniq -c
  ```

  <details><summary>PowerShell 7</summary>

  ```powershell
  $id = (Invoke-RestMethod 'https://lazydad.fyi/jokes/feed?sort=new&limit=1').items[0].id
  $vote = @{ Uri = "https://lazydad.fyi/jokes/$id/vote"; Method = 'Post'; ContentType = 'application/json'
             Body = '{"value":0,"previous":0}'; SkipHttpErrorCheck = $true }
  1..50 | ForEach-Object {
    $r = Invoke-WebRequest @vote
    $by = if ($r.Content -match 'error code: 1015') { 'cloudflare' } else { 'app' }
    "$($r.StatusCode)-$by $(([string]$r.Headers['CF-RAY']).Split('-')[-1])"
  } | Group-Object | Select-Object Count, Name
  ```

  </details>

  A data center that got more than 20 within 10 seconds answers `429-cloudflare` from then on, for 10 seconds.
- Optional, `www`: a `CNAME` `www` → `lazydad.fyi` (proxied), and *Rules → Redirect Rules*, template
  "Redirect from WWW to root".

**5. Make Cloudflare the only way in.** Otherwise anyone could skip Cloudflare through the app's own
`*.azurecontainerapps.io` address. The switch is a variable on the `production` GitHub environment,
`CLOUDFLARE_ONLY_INGRESS`: while it's `true`, every deploy (Deploy Environment) makes the prod app's ingress admit
only Cloudflare's IPv4 ranges, one `cloudflare-*` rule per range (Container Apps' ingress is IPv4; the app's own list
also has the IPv6 ones, for visitors' addresses). Deploys then keep those rules current, and let their own runner
through for the smoke tests, which call the Azure address. The rules are written in a single update
(about 20 seconds), so the app switches from "everyone" to "only Cloudflare" at once, with no moment where only part
of Cloudflare gets through. Staging never has the variable, so its ingress (your IP only) is left alone.

```bash
gh variable set CLOUDFLARE_ONLY_INGRESS -R $REPO --env production --body true
gh workflow run deploy-master.yml -R $REPO --ref master   # or let the next merge deploy it
# Once Deploy Master is green (its production job logs "Locking lazydad-app to Cloudflare"):
curl -s -o /dev/null -w '%{http_code}\n' "https://$(az containerapp show -g $RG -n lazydad-app --query properties.configuration.ingress.fqdn -o tsv)/healthz"   # 403
curl -s -o /dev/null -w '%{http_code}\n' https://lazydad.fyi/healthz                                                                                         # 200
```

<details><summary>PowerShell 7</summary>

```powershell
gh variable set CLOUDFLARE_ONLY_INGRESS -R $REPO --env production --body true
gh workflow run deploy-master.yml -R $REPO --ref master   # or let the next merge deploy it
# Once Deploy Master is green (its production job logs "Locking lazydad-app to Cloudflare"):
(Invoke-WebRequest "https://$(az containerapp show -g $RG -n lazydad-app --query properties.configuration.ingress.fqdn -o tsv)/healthz" -SkipHttpErrorCheck).StatusCode   # 403
(Invoke-WebRequest https://lazydad.fyi/healthz -SkipHttpErrorCheck).StatusCode                                                                                          # 200
```

</details>

To undo: `gh variable delete CLOUDFLARE_ONLY_INGRESS -R mykolad/lazydad --env production`, and deploy again; the deploy
removes the `cloudflare-*` rules, and with no Allow rules left the app's own address is open again. Section 12 has
how the ranges stay current.

## 11. Monitoring and logs: Grafana Cloud

The app sends traces, metrics and logs over OpenTelemetry (OTLP) straight to a Grafana Cloud stack in the EU
(free tier; no collector or agent to run). Only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set: local runs and tests
send nothing. What goes out, and what doesn't:

- **Traces:** one per request (`/healthz` excluded, uptime checks would flood them) and one per scheduler tick
  (`joke tick`), with its lease, LLM calls (model, duration, tokens) and SQL commands under it.
- **Metrics:** requests (rate, errors, latency per route), rate-limited votes, LLM duration and tokens per model
  (`gen_ai_client_*`), SQL, .NET runtime, and the scheduler's own counters: `lazydad_scheduler_ticks_total`
  (outcome `succeeded`/`failed`/`skipped`), `lazydad_jokes_total` (per model, `saved`/`empty`/`failed`),
  `lazydad_leaderboard_updates_total`.
- **Logs:** everything the app logs through `ILogger`, linked to its trace. That includes model output the app logs
  on purpose: each saved joke's text, and the judge's raw answer when it's invalid (to debug it). It's only about
  the (public) jokes; the LLM spans themselves don't record prompts or responses.
- **Never:** visitor IPs, user agents or any other visitor data (`PersonalDataFilter` strips them before export).
  Nothing the app logs is about visitors; keep it that way.

Each app reports as its own service (`service.name` = the Container App's name, so `job="lazydad-app"` in PromQL).

**1. A stack and two tokens.** Sign up at grafana.com (free), with the stack in an **EU** region (this one is
`eu-north`). Then *Connections → OpenTelemetry (OTLP) → View connection details* shows the endpoint and the
instance ID. Create **two tokens** there (or as access policies under *Administration → Cloud access policies*),
each allowed to write metrics, logs and traces: one for prod, one for staging. Staging runs branch previews, so it
must never be able to read prod's token; a separate one can be revoked on its own. (Both still write to the same
stack, and a write token can't be limited to certain labels: staging's could send data labelled as production.)

Keep the tokens out of the repo and the chat. Each goes into Key Vault in `OTEL_EXPORTER_OTLP_HEADERS`'s format:
basic auth `<instance id>:<token>`, the space URL-encoded (per the OTLP spec). The helper checks a token with Grafana
first (the app doesn't log export failures), and stores it only if Grafana accepts it:

```bash
INSTANCE_ID=<instance id from the connection details>
store_token() {   # $1: the Key Vault secret. Reads the token without echoing it or keeping it in the history.
  local token auth code
  read -rsp "Grafana token for $1: " token; echo
  auth=$(printf '%s:%s' "$INSTANCE_ID" "$token" | base64 -w0)
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Basic $auth" -H 'Content-Type: application/json' \
    -d '{"resourceLogs":[]}' https://otlp-gateway-prod-eu-north-0.grafana.net/otlp/v1/logs)
  case $code in
    200|204) az keyvault secret set --vault-name lazydad-kv -n "$1" --value "Authorization=Basic%20$auth" -o none && echo "Stored $1." ;;
    *) echo "Grafana answered $code (401: wrong token or instance ID); $1 not stored."; return 1 ;;
  esac
}
store_token OtlpHeaders          # prod's token
store_token OtlpHeadersStaging   # staging's token
```

<details><summary>PowerShell 7</summary>

```powershell
$INSTANCE_ID = '<instance id from the connection details>'
function Set-GrafanaToken([string] $Secret) {   # Read-Host input isn't echoed or kept in the history
  $token = Read-Host "Grafana token for $Secret" -MaskInput
  # "${INSTANCE_ID}:" in braces: "$INSTANCE_ID:" would read as a scoped variable.
  $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${INSTANCE_ID}:$token"))
  $code = (Invoke-WebRequest -Method Post -Uri https://otlp-gateway-prod-eu-north-0.grafana.net/otlp/v1/logs `
    -Headers @{ Authorization = "Basic $auth" } -ContentType application/json `
    -Body '{"resourceLogs":[]}' -SkipHttpErrorCheck).StatusCode
  if ($code -notin 200, 204) { "Grafana answered $code (401: wrong token or instance ID); $Secret not stored."; return }
  az keyvault secret set --vault-name lazydad-kv -n $Secret --value "Authorization=Basic%20$auth" -o none
  "Stored $Secret."
}
Set-GrafanaToken OtlpHeaders          # prod's token
Set-GrafanaToken OtlpHeadersStaging   # staging's token
```

</details>

**2. Each app reads its own token.** A Container Apps secret can be a *Key Vault reference*, read with the app's
own identity when a replica starts, so the value never sits in the app's configuration. Each app gets
`Key Vault Secrets User` on its own secret only:

```bash
KV_ID=$(az keyvault show -n lazydad-kv --query id -o tsv)
for app_secret in lazydad-app:OtlpHeaders lazydad-app-staging:OtlpHeadersStaging; do
  app=${app_secret%%:*}; secret=${app_secret#*:}
  az role assignment create --assignee-object-id "$(az containerapp show -g $RG -n $app --query identity.principalId -o tsv)" \
    --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" --scope "$KV_ID/secrets/$secret" -o none
done
```

<details><summary>PowerShell 7</summary>

```powershell
$KV_ID = az keyvault show -n lazydad-kv --query id -o tsv
foreach ($pair in @{ App = 'lazydad-app'; Secret = 'OtlpHeaders' }, @{ App = 'lazydad-app-staging'; Secret = 'OtlpHeadersStaging' }) {
  az role assignment create --assignee-object-id (az containerapp show -g $RG -n $pair.App --query identity.principalId -o tsv) `
    --assignee-principal-type ServicePrincipal --role 'Key Vault Secrets User' --scope "$KV_ID/secrets/$($pair.Secret)" -o none
}
```

</details>

**3. Turn telemetry on**, staging first. Wait a few minutes after the role assignments: a reference the identity
can't read yet fails the new revision (the old one keeps serving). Each setting change makes a new revision.

```bash
APP=lazydad-app-staging; SECRET=OtlpHeadersStaging; ENV=staging     # then: lazydad-app / OtlpHeaders / production
az containerapp secret set -g $RG -n $APP \
  --secrets "otlp-headers=keyvaultref:https://lazydad-kv.vault.azure.net/secrets/$SECRET,identityref:system" -o none
az containerapp update -g $RG -n $APP -o none --set-env-vars \
  OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-eu-north-0.grafana.net/otlp \
  OTEL_EXPORTER_OTLP_HEADERS=secretref:otlp-headers \
  OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=$ENV
```

<details><summary>PowerShell 7</summary>

```powershell
$APP = 'lazydad-app-staging'; $SECRET = 'OtlpHeadersStaging'; $ENV_NAME = 'staging'     # then: lazydad-app / OtlpHeaders / production
az containerapp secret set -g $RG -n $APP `
  --secrets "otlp-headers=keyvaultref:https://lazydad-kv.vault.azure.net/secrets/$SECRET,identityref:system" -o none
az containerapp update -g $RG -n $APP -o none --set-env-vars `
  OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-eu-north-0.grafana.net/otlp `
  OTEL_EXPORTER_OTLP_HEADERS=secretref:otlp-headers `
  "OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=$ENV_NAME"
```

</details>

Check in Grafana's *Explore*: logs `{service_name="lazydad-app-staging"}`, a `joke tick` trace from the new
revision's startup tick, and the metric `lazydad_jokes_total`. If nothing arrives although the token check passed,
compare the app's settings with the commands above (`az containerapp show -n $APP -g $RG --query
properties.template.containers[0].env`). To switch telemetry off again:
`az containerapp update -n $APP -g $RG --remove-env-vars OTEL_EXPORTER_OTLP_ENDPOINT -o none`.

**4. Uptime check and alerts (prod only).** Staging's ingress admits your IP only, and it scales to zero, so it
would look down and idle all the time.

- *Testing & synthetics → Synthetics → Add check → HTTP*: `https://lazydad.fyi/healthz`, every 5 minutes from 2–3
  probes (well inside the free tier's executions), with its built-in alert when the check fails.
- *Alerting → Contact points*: your email. Then *Alert rules* (Prometheus data source), evaluated every 5 minutes:

  | Alert | Query | Condition |
  |---|---|---|
  | A scheduler tick failed | `sum(increase(lazydad_scheduler_ticks_total{job="lazydad-app", outcome="failed"}[15m]))` | > 0 |
  | No joke saved for 5 hours (ticks run every 4) | `sum(increase(lazydad_jokes_total{job="lazydad-app", outcome="saved"}[5h]))` | < 1; *no data* also alerts |
  | A model failed or returned nothing | `sum by (model) (increase(lazydad_jokes_total{job="lazydad-app", outcome=~"failed\|empty"}[4h]))` | > 0 |
  | Server errors | `sum(increase(http_server_request_duration_seconds_count{job="lazydad-app", http_response_status_code=~"5.."}[15m]))` | > 2 |

  Metric names are Grafana's translation of the OpenTelemetry names; if one doesn't match, pick it in the query
  builder's metric browser.

**5. The dashboard.** `infra/grafana/lazydad-dashboard.json` shows one app at a time (the *App* selector at the
top): requests, errors and latency; ticks, jokes and leaderboard updates by outcome; LLM call duration and tokens per
model; CPU and memory against the container's limits; SQL and outbound calls; and the logs. In Grafana:
*Dashboards → New → Import*, upload the file, *Import*. The *Metrics* and *Logs* selectors at the top offer only the
stack's `…-prom` and `…-logs` data sources (Grafana Cloud's names), so it picks those; other Loki data sources, such as
`…-alert-state-history`, hold Grafana's own records, not the app's logs. Editing it in Grafana is fine; to keep a change, export
it (*Share → Export*, with "Export for sharing externally" off) and replace the file in a PR.

CPU and memory come from the app process's own metrics. The container's metrics would need Grafana's Azure Monitor data
source, which signs in with an app registration secret: a stored credential this setup does without.

The console logs keep going to Log Analytics (section 1) as the fallback, with no visitor data either.

## 12. Operations

### What each identity can do

At any time, and after any change to roles (a role assignment with an empty `--scope`, from a variable a new
terminal didn't have, fails, so check):

```bash
for id in lazydad-acr-pull lazydad-github-staging lazydad-github-cd; do
  echo "== $id"
  az role assignment list --assignee "$(az identity show -g $RG -n $id --query principalId -o tsv)" --all \
    --query "[].{role:roleDefinitionName, scope:scope}" -o table
done
for app in lazydad-app lazydad-app-staging; do
  echo "== $app (system-assigned)"
  az role assignment list --assignee "$(az containerapp show -g $RG -n $app --query identity.principalId -o tsv)" --all \
    --query "[].{role:roleDefinitionName, scope:scope}" -o table
done
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($id in 'lazydad-acr-pull', 'lazydad-github-staging', 'lazydad-github-cd') {
  "== $id"
  az role assignment list --assignee (az identity show -g $RG -n $id --query principalId -o tsv) --all `
    --query '[].{role:roleDefinitionName, scope:scope}' -o table
}
foreach ($app in 'lazydad-app', 'lazydad-app-staging') {
  "== $app (system-assigned)"
  az role assignment list --assignee (az containerapp show -g $RG -n $app --query identity.principalId -o tsv) --all `
    --query '[].{role:roleDefinitionName, scope:scope}' -o table
}
```

</details>

What a previewed branch can do, running as `lazydad-github-staging`: anything to staging (its app, its database, LLM
calls, which cost tokens), push images to `lazydad-preview`, which production never runs, and open a SQL firewall rule
(useless without a database user). It can't change what production runs, make production run anything else, or
touch production's repository: not its images, and not the tags that keep them from the weekly purge.

### A new Grafana token

When one expires or leaks, per app: create a new token in Grafana, store it with section 11's helper, then restart
the app's active revision, since the value is read when a replica starts. Revoke the old token in Grafana. The helper
exists only in the terminal that defined it: in a new one, **run section 11's step 1 block first**, the lines up to
and including the helper (`INSTANCE_ID` and `store_token`, or `$INSTANCE_ID` and `Set-GrafanaToken`), without the
two calls at its end.

```bash
APP=lazydad-app; SECRET=OtlpHeaders     # or: lazydad-app-staging / OtlpHeadersStaging
store_token $SECRET
az containerapp revision restart -n $APP -g $RG \
  --revision "$(az containerapp show -n $APP -g $RG --query properties.latestReadyRevisionName -o tsv)"
```

<details><summary>PowerShell 7</summary>

```powershell
$APP = 'lazydad-app'; $SECRET = 'OtlpHeaders'     # or: lazydad-app-staging / OtlpHeadersStaging
Set-GrafanaToken $SECRET
az containerapp revision restart -n $APP -g $RG `
  --revision (az containerapp show -n $APP -g $RG --query properties.latestReadyRevisionName -o tsv)
```

</details>

### Keeping Cloudflare's ranges current

Cloudflare changes its ranges rarely, and announces it in advance. Two things keep up with it without you having to
notice:

- **Every deploy syncs the ingress rules.** While `CLOUDFLARE_ONLY_INGRESS` is on, Deploy Environment reads
  Cloudflare's list from its API (`tools/cloudflare-ranges.sh sync`), and adds new ranges and removes ones Cloudflare
  no longer lists, in one update of the whole list. If the API can't be read, it leaves the rules alone with a
  warning; it never removes more than 3 ranges at once (more means something is off: it adds, warns, and leaves
  removals to you).
- **A weekly check watches the app's own list.** *Check Cloudflare Ranges* (Mondays, or *Run workflow*) compares
  `Cloudflare:IpRanges` in `appsettings.json` with Cloudflare's list. If they differ, it fails and opens an issue,
  "Cloudflare's IP ranges changed", listing what to add and remove. The fix is a PR updating that list, and its
  deploy also syncs the ingress rules. The check closes the issue once the lists match again.

By hand (needs `jq`), from the repository root, signed in with `az login`:

```bash
bash tools/cloudflare-ranges.sh check src/LazyDad.Api/appsettings.json                     # compare the app's list
bash tools/cloudflare-ranges.sh sync lazydad-app $RG "manual-$(date +%Y%m%d%H%M)" on      # sync the ingress now
```

*PowerShell 7: run the same through Git Bash, for example `bash tools/cloudflare-ranges.sh check src/LazyDad.Api/appsettings.json`.*

Compare both lists with <https://www.cloudflare.com/ips/> now and then.

### Registry purge

Preview what it would delete, check runs, or run it now:

```bash
az acr run --registry lazydadacr --source-acr-auth-id "[caller]" \
  --cmd "acr purge --filter 'lazydad:^[0-9a-f]{7}.*$' --filter 'lazydad-preview:^[0-9a-f]{7}.*$' --ago 30d --keep 10 --untagged --dry-run" /dev/null
az acr task list-runs --registry lazydadacr --name purge-old-images -o table
az acr task run --registry lazydadacr --name purge-old-images
```

<details><summary>PowerShell 7</summary>

```powershell
az acr run --registry lazydadacr --source-acr-auth-id '[caller]' `
  --cmd 'acr purge --filter ''lazydad:^[0-9a-f]{7}.*$'' --filter ''lazydad-preview:^[0-9a-f]{7}.*$'' --ago 30d --keep 10 --untagged --dry-run' /dev/null
az acr task list-runs --registry lazydadacr --name purge-old-images -o table
az acr task run --registry lazydadacr --name purge-old-images
```

</details>

### Manual rollback

Deploy Master and Roll Back Production (both through Deploy Environment) protect the images they deploy from the
purge (section 2). For a rollback outside the pipelines (e.g. to an image Roll Back Production refuses: one that
production's revisions never ran, one built before images carried their version, or one older than
`ROLLBACK_MIN_COMMIT`), do the same yourself: deploy by digest (`az acr repository show -n lazydadacr --image
lazydad:<tag> --query digest -o tsv`, then `--image lazydadacr.azurecr.io/lazydad@<digest>`), and move the
`deployed-*` tag to it or lock the image (`az acr repository update -n lazydadacr --image lazydad:<tag>
--delete-enabled false`). Production pulls only from `lazydad`: an image that never ran there is only in
`lazydad-preview`, so copy it first, as Deploy Environment does (`docker pull`, `docker tag` into `lazydad`,
`docker push`; your registry role allows it, section 2) and check that the digest stayed the same. An image built before #17 reports version `dev` unless you also pass `--set-env-vars
App__Version=<tag>`; the next pipeline deploy removes that setting again. An image from before #30 (older than
`ROLLBACK_MIN_COMMIT`) only knows API keys, so its model calls fail now that key authentication is off.

### Restoring the database

Section 4 sets up the backups. Every restore creates a **new database** on a server; it never overwrites one. You check
the copy, then swap the names: the app's connection string and the deploys' migrations both use the name `lazydad-db`,
so after the swap nothing else changes. Database users and roles come with the copy.

| To get back | Restore from | Command |
|---|---|---|
| Any moment in the last 7 days | point-in-time backups | `az sql db restore --time` |
| A week or month further back | long-term backups (weekly for 7 weeks, monthly for 12 months) | `az sql db ltr-backup restore` |
| The database after Sweden Central is lost | geo-redundant backups (at most about an hour old) | `az sql db geo-backup restore`, to a server in another region |
| A deleted database | its point-in-time backups, while the server exists | `az sql db restore --deleted-time` (see `az sql db list-deleted`) |

**1. Restore a copy.** Point in time, e.g. just before a bad deploy (UTC):

```bash
S=lazydad-sql-swedencentral
az sql db restore -g $RG -s $S -n lazydad-db --dest-name lazydad-db-restored \
  --time 2026-09-29T08:30:00Z --service-objective Basic --backup-storage-redundancy Geo -o none
```

<details><summary>PowerShell 7</summary>

```powershell
$S = 'lazydad-sql-swedencentral'
az sql db restore -g $RG -s $S -n lazydad-db --dest-name lazydad-db-restored `
  --time 2026-09-29T08:30:00Z --service-objective Basic --backup-storage-redundancy Geo -o none
```

</details>

Or a long-term backup: list them, then restore the one you want by its id.

```bash
az sql db ltr-backup list -l swedencentral -s $S -d lazydad-db --query "[].{time:backupTime, id:id}" -o table
az sql db ltr-backup restore --backup-id "<id from the list>" --dest-database lazydad-db-restored \
  --dest-server $S --dest-resource-group $RG --service-objective Basic --backup-storage-redundancy Geo -o none
```

*PowerShell 7: the same commands, with the query in single quotes.*

A restore takes a few minutes for a database this size. It's billed as a second Basic database while it exists.

**2. Check the copy**, in the portal's Query editor (as the Entra admin), next to the same query on `lazydad-db`:

```sql
SELECT (SELECT COUNT(*) FROM Jokes) AS jokes, (SELECT MAX(GeneratedAt) FROM Jokes) AS newest,
       (SELECT SUM(Up + Down) FROM Jokes) AS votes, (SELECT COUNT(*) FROM TopJokes) AS top_jokes;
```

**3a. Just a test: delete the copy.**

```bash
az sql db delete -g $RG -s $S -n lazydad-db-restored --yes
```

*PowerShell 7: the same command.*

**3b. For real: swap it in.** Close the Query editor first. A database with open connections can't be renamed, so
the prod app stops for the swap: deactivating its only revision stops its replicas, and `lazydad.fyi` answers with
errors for those few minutes. The lock and the long-term retention belong to the database, not its name, so they move
to the new `lazydad-db` by hand. A backup can predate migrations, and the app never migrates by itself (the pipeline
does, staging first, minutes later), so apply them while the app is still stopped: from an up-to-date checkout of
`master`, with the pinned EF Core tool, as the server's Entra admin (your `az login`, from an IP the firewall allows).
The reactivated revision then starts on the current schema; no deploy is needed. Keep the old database until you're
sure, then delete it: its own point-in-time backups go with it, its long-term ones stay for their retention.

The block stops at the first command that fails, so the app is only reactivated after every step before it worked. If
it stops, the app stays stopped: read the error, fix the cause, and run the remaining commands by hand.

```bash
(
set -e   # in a subshell, so it ends with the block
REV=$(az containerapp show -n lazydad-app -g $RG --query properties.latestReadyRevisionName -o tsv)
az containerapp revision deactivate -n lazydad-app -g $RG --revision $REV -o none
az lock delete -n lazydad-db-no-delete -g $RG --namespace Microsoft.Sql \
  --parent servers/$S --resource-type databases --resource lazydad-db
az sql db rename -g $RG -s $S -n lazydad-db --new-name lazydad-db-before-restore -o none
az sql db rename -g $RG -s $S -n lazydad-db-restored --new-name lazydad-db -o none
# Section 4's backup commands again: the retention policy and the delete lock, now on the restored database.
az sql db ltr-policy set -g $RG -s $S -n lazydad-db --weekly-retention P7W --monthly-retention P12M -o none
az lock create -n lazydad-db-no-delete -t CanNotDelete -g $RG --namespace Microsoft.Sql \
  --parent servers/$S --resource-type databases --resource lazydad-db \
  --notes "The jokes and votes. Remove it only to restore (runbook section 12) or to retire the app." -o none
# The restored database gets master's migrations (none, if the backup is recent enough), while nothing uses it.
git switch master
git pull
dotnet tool restore
dotnet ef database update --project src/LazyDad.Data --startup-project src/LazyDad.Api \
  --connection "Server=tcp:$S.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
az containerapp revision activate -n lazydad-app -g $RG --revision $REV -o none
)
```

<details><summary>PowerShell 7</summary>

```powershell
& {
# In a script block, so the stop-on-error settings end with it. The second one makes a failing az, git or dotnet
# command stop the block too (PowerShell 7.3 and later).
$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true
$REV = az containerapp show -n lazydad-app -g $RG --query properties.latestReadyRevisionName -o tsv
az containerapp revision deactivate -n lazydad-app -g $RG --revision $REV -o none
az lock delete -n lazydad-db-no-delete -g $RG --namespace Microsoft.Sql `
  --parent "servers/$S" --resource-type databases --resource lazydad-db
az sql db rename -g $RG -s $S -n lazydad-db --new-name lazydad-db-before-restore -o none
az sql db rename -g $RG -s $S -n lazydad-db-restored --new-name lazydad-db -o none
# Section 4's backup commands again: the retention policy and the delete lock, now on the restored database.
az sql db ltr-policy set -g $RG -s $S -n lazydad-db --weekly-retention P7W --monthly-retention P12M -o none
az lock create -n lazydad-db-no-delete -t CanNotDelete -g $RG --namespace Microsoft.Sql `
  --parent "servers/$S" --resource-type databases --resource lazydad-db `
  --notes 'The jokes and votes. Remove it only to restore (runbook section 12) or to retire the app.' -o none
# The restored database gets master's migrations (none, if the backup is recent enough), while nothing uses it.
git switch master
git pull
dotnet tool restore
dotnet ef database update --project src/LazyDad.Data --startup-project src/LazyDad.Api `
  --connection "Server=tcp:$S.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
az containerapp revision activate -n lazydad-app -g $RG --revision $REV -o none
}
```

</details>

Then check `https://lazydad.fyi/status`: the reactivated revision's startup tick saves jokes into the restored
database.

**After a region outage** (Sweden Central down, the server unreachable), the swap above doesn't apply: there's no
`lazydad-db` to rename, only its geo-redundant backups (at most about an hour old). The copy goes straight to a new
server in another region, under the final name, and the app and the deploys point at that server. The block first
stops the prod app (deactivating its revision): if Sweden Central came back mid-way, the app would otherwise write
votes and jokes to the old database again, and those writes would be lost when the app moves to the new one. The
connection-string update at the end makes the new revision, which brings the app back:

1. **A server in another region**, as in section 4 (Entra-only, you as the admin, the two firewall rules), e.g.
   `lazydad-sql-northeurope` in North Europe. The deploy identities' `LazyDad Deployer` role covers it already (it's
   on the resource group).
2. **Geo-restore `lazydad-db` onto it**, then give it section 4's retention policy and lock (with `-s $NEW`). The
   database users come with it, so the apps and the migrations can sign in as before.
3. **Apply master's migrations** to it, as in 3b (the `dotnet ef database update` line, with `$NEW` as the server).
4. **Point the prod app at it.** A new connection string makes a new revision, which starts on the new server; check
   `https://lazydad.fyi/status` before relying on it.
5. **Point production's deploys at it**, in a PR: Deploy Environment's `SQL_SERVER` (in
   `.github/workflows/deploy-environment.yml`) is one value for both environments, and three steps use it: opening
   the runner's firewall rule, the migration's connection, and closing the rule. So choose the server per
   environment once, before the firewall step (production: the new server; staging: the old one), and use that for
   all three. Changing `SQL_SERVER` itself would send staging's migrations to the new server, where its database
   isn't, and Deploy Master would fail at staging. Until the PR is merged, production's migration step can't reach
   its database.

```bash
(
set -e   # stops at the first failure, like 3b
S=lazydad-sql-swedencentral   # the unreachable server, whose geo-backups are restored
NEW=lazydad-sql-northeurope
REV=$(az containerapp show -n lazydad-app -g $RG --query properties.latestReadyRevisionName -o tsv)
az containerapp revision deactivate -n lazydad-app -g $RG --revision $REV -o none   # no more writes to the old database
ID=$(az sql db geo-backup list -g $RG -s $S --query "[?name=='lazydad-db'].id | [0]" -o tsv)
az sql db geo-backup restore --geo-backup-id "$ID" --dest-database lazydad-db --dest-server $NEW -g $RG \
  --service-objective Basic --backup-storage-redundancy Geo -o none
az sql db ltr-policy set -g $RG -s $NEW -n lazydad-db --weekly-retention P7W --monthly-retention P12M -o none
az lock create -n lazydad-db-no-delete -t CanNotDelete -g $RG --namespace Microsoft.Sql \
  --parent servers/$NEW --resource-type databases --resource lazydad-db \
  --notes "The jokes and votes. Remove it only to restore (runbook section 12) or to retire the app." -o none
dotnet tool restore
dotnet ef database update --project src/LazyDad.Data --startup-project src/LazyDad.Api \
  --connection "Server=tcp:$NEW.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
az containerapp update -n lazydad-app -g $RG -o none --set-env-vars \
  "ConnectionStrings__DefaultConnection=Server=tcp:$NEW.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Managed Identity;Encrypt=True"
)
```

<details><summary>PowerShell 7</summary>

```powershell
& {
$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true   # stops at the first failure, like 3b
$S = 'lazydad-sql-swedencentral'   # the unreachable server, whose geo-backups are restored
$NEW = 'lazydad-sql-northeurope'
$REV = az containerapp show -n lazydad-app -g $RG --query properties.latestReadyRevisionName -o tsv
az containerapp revision deactivate -n lazydad-app -g $RG --revision $REV -o none   # no more writes to the old database
$ID = az sql db geo-backup list -g $RG -s $S --query "[?name=='lazydad-db'].id | [0]" -o tsv
az sql db geo-backup restore --geo-backup-id $ID --dest-database lazydad-db --dest-server $NEW -g $RG `
  --service-objective Basic --backup-storage-redundancy Geo -o none
az sql db ltr-policy set -g $RG -s $NEW -n lazydad-db --weekly-retention P7W --monthly-retention P12M -o none
az lock create -n lazydad-db-no-delete -t CanNotDelete -g $RG --namespace Microsoft.Sql `
  --parent "servers/$NEW" --resource-type databases --resource lazydad-db `
  --notes 'The jokes and votes. Remove it only to restore (runbook section 12) or to retire the app.' -o none
dotnet tool restore
dotnet ef database update --project src/LazyDad.Data --startup-project src/LazyDad.Api `
  --connection "Server=tcp:$NEW.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
az containerapp update -n lazydad-app -g $RG -o none --set-env-vars `
  "ConnectionStrings__DefaultConnection=Server=tcp:$NEW.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Managed Identity;Encrypt=True"
}
```

</details>

Run it from an up-to-date checkout of `master` (for the migrations). Like 3b, the block stops at the first failure, so
the app is only pointed at the new server once everything before it worked. Staging's database stays on the old server; staging can wait until Sweden Central is
back. A second region that's always ready is the
resilience plan's next phase.
