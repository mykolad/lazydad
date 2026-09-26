# Deployment one-time setup (Azure + GitHub)

What was set up on 2026-09-24 to support `.github/workflows/deploy-master.yml` (Deploy Master). Every step used the `az`
and `gh` CLIs. Keep this in sync with reality until it's replaced by Bicep (IaC is on the plan).

Each command block is bash (Git Bash on Windows). Under it, a collapsed **PowerShell 7** version does the same, or a
note says the bash commands run unchanged. Both use the same variable names; set them once per session with the block
below. Except `$PID` and `$ENV`, which PowerShell reserves (process id, and too close to the `$env:` drive): those are
`$APP_PID` and `$ENV_NAME` there.

> In Git Bash, `export MSYS_NO_PATHCONV=1` first. Otherwise the `/subscriptions/...` scopes get
> rewritten as file paths and role assignments fail with `MissingSubscription`.

> In PowerShell on Windows, `az` is `az.cmd`, so every argument also passes through `cmd`. Arguments in single quotes
> (the PowerShell blocks use them for anything with spaces, `;`, `,`, `^` or `$`) arrive intact, but `cmd` drops the
> double quotes inside JSON (none of these blocks pass JSON to `az`), and treats `<...>` in an argument without spaces as
> a redirection: replace every `<placeholder>` before running a command.

```bash
RG=lazydad-rg
ACR_ID=$(az acr show -n lazydadacr --query id -o tsv)
```

<details><summary>PowerShell 7</summary>

```powershell
$RG = 'lazydad-rg'
$ACR_ID = az acr show -n lazydadacr --query id -o tsv
```

</details>

## 1. Identities

| Identity | Used by | Roles |
|---|---|---|
| `lazydad-acr-pull` | both Container Apps, to pull images | `AcrPull` on `lazydadacr` |
| `lazydad-github-cd` | GitHub Actions (OIDC), to deploy | `AcrPush` + `Reader` on `lazydadacr`; `Contributor` on `lazydad-app`, `lazydad-app-staging`, `lazydad-cae`; `SQL Server Contributor` on `lazydad-sql-swedencentral` (temporary firewall rules) |

Section 11 later gives staging its own deploy identity (`lazydad-github-staging`), so that branch previews can't
reach production, and narrows these roles.

```bash
az identity create -g $RG -n lazydad-acr-pull -l westeurope
az identity create -g $RG -n lazydad-github-cd -l westeurope
PULL_PID=$(az identity show -g $RG -n lazydad-acr-pull --query principalId -o tsv)
CD_PID=$(az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv)

az role assignment create --assignee-object-id $PULL_PID --assignee-principal-type ServicePrincipal --role AcrPull --scope "$ACR_ID"
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role AcrPush --scope "$ACR_ID"
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role Reader  --scope "$ACR_ID"   # az acr login resolves the registry via ARM
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role "SQL Server Contributor" \
  --scope "$(az sql server show -g $RG -n lazydad-sql-swedencentral --query id -o tsv)"
# Contributor on lazydad-app, lazydad-app-staging (after step 3) and the environment (join/action on update):
for id in $(az resource list -g $RG --query "[?name=='lazydad-app' || name=='lazydad-app-staging' || name=='lazydad-cae'].id" -o tsv); do
  az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role Contributor --scope "$id"
done
```

<details><summary>PowerShell 7</summary>

```powershell
az identity create -g $RG -n lazydad-acr-pull -l westeurope
az identity create -g $RG -n lazydad-github-cd -l westeurope
$PULL_PID = az identity show -g $RG -n lazydad-acr-pull --query principalId -o tsv
$CD_PID = az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv

az role assignment create --assignee-object-id $PULL_PID --assignee-principal-type ServicePrincipal --role AcrPull --scope $ACR_ID
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role AcrPush --scope $ACR_ID
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role Reader  --scope $ACR_ID   # az acr login resolves the registry via ARM
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role 'SQL Server Contributor' `
  --scope (az sql server show -g $RG -n lazydad-sql-swedencentral --query id -o tsv)
# Contributor on lazydad-app, lazydad-app-staging (after step 3) and the environment (join/action on update):
foreach ($id in az resource list -g $RG --query "[?name=='lazydad-app' || name=='lazydad-app-staging' || name=='lazydad-cae'].id" -o tsv) {
  az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role Contributor --scope $id
}
```

</details>

## 2. Staging database (free offer)

```bash
az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db-staging \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 --min-capacity 0.5 \
  --auto-pause-delay 60 --use-free-limit --free-limit-exhaustion-behavior AutoPause --backup-storage-redundancy Local
```

<details><summary>PowerShell 7</summary>

```powershell
az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db-staging `
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 --min-capacity 0.5 `
  --auto-pause-delay 60 --use-free-limit --free-limit-exhaustion-behavior AutoPause --backup-storage-redundancy Local
```

</details>

The connection string is prod's with `Database=lazydad-db-staging` (same server login).
Apply the schema once with `dotnet ef database update --connection "<staging>"`; Deploy Master keeps it current after that.

## 3. Staging Container App

```bash
PULL_ID=$(az identity show -g $RG -n lazydad-acr-pull --query id -o tsv)
az containerapp create -n lazydad-app-staging -g $RG --environment lazydad-cae \
  --image lazydadacr.azurecr.io/lazydad:<tag> \
  --user-assigned "$PULL_ID" --registry-server lazydadacr.azurecr.io --registry-identity "$PULL_ID" \
  --ingress external --target-port 8080 --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1Gi \
  --secrets "sql-conn=<staging conn>" "openai-endpoint=<endpoint>" "openai-key=<key>" \
  --env-vars ConnectionStrings__DefaultConnection=secretref:sql-conn \
             LlmProviders__AzureOpenAI__Endpoint=secretref:openai-endpoint \
             LlmProviders__AzureOpenAI__ApiKey=secretref:openai-key
```

<details><summary>PowerShell 7</summary>

```powershell
$PULL_ID = az identity show -g $RG -n lazydad-acr-pull --query id -o tsv
az containerapp create -n lazydad-app-staging -g $RG --environment lazydad-cae `
  --image 'lazydadacr.azurecr.io/lazydad:<tag>' `
  --user-assigned $PULL_ID --registry-server lazydadacr.azurecr.io --registry-identity $PULL_ID `
  --ingress external --target-port 8080 --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1Gi `
  --secrets 'sql-conn=<staging conn>' 'openai-endpoint=<endpoint>' 'openai-key=<key>' `
  --env-vars ConnectionStrings__DefaultConnection=secretref:sql-conn `
             LlmProviders__AzureOpenAI__Endpoint=secretref:openai-endpoint `
             LlmProviders__AzureOpenAI__ApiKey=secretref:openai-key
```

</details>

## 4. Prod: pull with managed identity, disable the ACR admin user

```bash
az containerapp identity assign -n lazydad-app -g $RG --user-assigned "$PULL_ID"
az containerapp registry set -n lazydad-app -g $RG --server lazydadacr.azurecr.io --identity "$PULL_ID"
az containerapp update -n lazydad-app -g $RG --revision-suffix mi-pull   # a real pull proves it works
az containerapp secret remove -n lazydad-app -g $RG --secret-names lazydadacrazurecrio-lazydadacr
az acr update -n lazydadacr --admin-enabled false
```

*PowerShell 7: the same commands.*

## 5. Staging: allow listed IPs only

Staging is public by default, and every cold start runs a real LLM joke tick, so any visitor or
crawler would cost tokens. With at least one `Allow` rule, Container Apps denies everyone else
(`403 RBAC: access denied`, answered at the ingress without waking the app):

```bash
az containerapp ingress access-restriction set -n lazydad-app-staging -g $RG \
  --rule-name home --ip-address <your-public-ip>/32 --action Allow --description "Owner's home IP"
```

<details><summary>PowerShell 7</summary>

```powershell
az containerapp ingress access-restriction set -n lazydad-app-staging -g $RG `
  --rule-name home --ip-address '<your-public-ip>/32' --action Allow --description "Owner's home IP"
```

</details>

Deploy Master adds the runner's IP for the duration of the smoke tests and removes it afterwards
(`restricted-ingress: true` in `deploy-master.yml`). If your home IP changes, update the `home` rule.

## 6. GitHub: OIDC trust, environments, variables, secrets

The federated credentials use GitHub's **immutable subject** format,
`repo:<owner>@<owner_id>/<repo>@<repo_id>:environment:<env>`. This repo emits it (it was created
after 2026-07-15; check with `gh api repos/mykolad/lazydad/actions/oidc/customization/sub`, which
shows `use_immutable_subject: true`). The numeric IDs never change or get reused, so a renamed,
transferred or re-created repository can't inherit the trust. The name-based subjects
(`repo:mykolad/lazydad:...`) would never match this repo's tokens.

```bash
PREFIX="repo:mykolad@$(gh api users/mykolad --jq .id)/lazydad@$(gh api repos/mykolad/lazydad --jq .id)"
for env in staging production; do
  az identity federated-credential create -g $RG --identity-name lazydad-github-cd -n github-$env-immutable \
    --issuer https://token.actions.githubusercontent.com \
    --subject "$PREFIX:environment:$env" --audiences api://AzureADTokenExchange

  gh api -X PUT repos/mykolad/lazydad/environments/$env \
    --input - <<< '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}'
  gh api -X POST repos/mykolad/lazydad/environments/$env/deployment-branch-policies -f name=master -f type=branch
done

gh variable set AZURE_CLIENT_ID       --body "$(az identity show -g $RG -n lazydad-github-cd --query clientId -o tsv)"
gh variable set AZURE_TENANT_ID       --body "$(az account show --query tenantId -o tsv)"
gh variable set AZURE_SUBSCRIPTION_ID --body "$(az account show --query id -o tsv)"

# Pipe the values in so they never appear on a command line:
printf '%s' "<staging conn>" | gh secret set SQL_CONNECTION_STRING --env staging
printf '%s' "<prod conn>"    | gh secret set SQL_CONNECTION_STRING --env production
```

<details><summary>PowerShell 7</summary>

```powershell
$PREFIX = "repo:mykolad@$(gh api users/mykolad --jq .id)/lazydad@$(gh api repos/mykolad/lazydad --jq .id)"
foreach ($envName in 'staging', 'production') {
  # ${PREFIX} in braces: "$PREFIX:environment" would read as a scoped variable.
  az identity federated-credential create -g $RG --identity-name lazydad-github-cd -n "github-$envName-immutable" `
    --issuer https://token.actions.githubusercontent.com `
    --subject "${PREFIX}:environment:$envName" --audiences api://AzureADTokenExchange

  '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}' |
    gh api -X PUT "repos/mykolad/lazydad/environments/$envName" --input -
  gh api -X POST "repos/mykolad/lazydad/environments/$envName/deployment-branch-policies" -f name=master -f type=branch
}

gh variable set AZURE_CLIENT_ID       --body (az identity show -g $RG -n lazydad-github-cd --query clientId -o tsv)
gh variable set AZURE_TENANT_ID       --body (az account show --query tenantId -o tsv)
gh variable set AZURE_SUBSCRIPTION_ID --body (az account show --query id -o tsv)

# Without --body, gh prompts for the value with hidden input: it's never on a command line or in the history.
# (Not a pipe: PowerShell would add a trailing newline to the secret.)
gh secret set SQL_CONNECTION_STRING --env staging
gh secret set SQL_CONNECTION_STRING --env production
```

</details>

Jobs log in only through an environment (the federated subjects are `environment:staging` and
`environment:production`), so a workflow that doesn't use one can't get an Azure token. Since section 11, each
environment trusts its own identity (`AZURE_CLIENT_ID` is set per environment).

Branch deploys to staging (Deploy Branch to Staging): `staging` also accepts `*/*` branches, and
`production` stays master-only. In GitHub's patterns `*` doesn't cross `/`, so `*/*` matches `feature/x`.
The federated credential matches the environment, not the branch, so nothing changes in Azure:

```bash
gh api -X POST repos/mykolad/lazydad/environments/staging/deployment-branch-policies -f name='*/*' -f type=branch
```

<details><summary>PowerShell 7</summary>

```powershell
gh api -X POST repos/mykolad/lazydad/environments/staging/deployment-branch-policies -f 'name=*/*' -f type=branch
```

</details>

## 7. Registry cleanup: weekly purge of old images

Every deploy pushes a new `lazydad:<short-sha>` image. An **ACR Task** (it runs inside the registry,
on a cron schedule in UTC, and costs fractions of a cent per run) deletes old ones every Sunday at 03:00 UTC:

```bash
# Git Bash: export MSYS_NO_PATHCONV=1 first, or /dev/null gets rewritten.
az acr task create --registry lazydadacr --name purge-old-images --schedule "0 3 * * 0" \
  --cmd "acr purge --filter 'lazydad:^[0-9a-f]{7}.*$' --ago 30d --keep 10 --untagged" --context /dev/null
```

<details><summary>PowerShell 7</summary>

```powershell
az acr task create --registry lazydadacr --name purge-old-images --schedule '0 3 * * 0' `
  --cmd 'acr purge --filter ''lazydad:^[0-9a-f]{7}.*$'' --ago 30d --keep 10 --untagged' --context /dev/null
```

</details>

What it keeps:
- **every image from the last 30 days** (`--ago 30d`)
- **plus the 10 newest older images.** `--keep` counts only the tags that would otherwise be
  deleted, not all tags.
- `--untagged` removes manifests that nothing references anymore. `--keep` applies to those
  separately too: the 10 newest eligible untagged manifests are also kept, so a dry run can show fewer
  manifest deletions than you'd expect. The untagged entries from the April `docker buildx` pushes are
  still referenced by their index tags, so they're removed once those tags age out.
- **whatever an environment runs**, even if failed deploys pushed many newer images and no deploy
  succeeded for over 30 days. That takes two things together:
  1. **Revisions are pinned to the image digest** (`lazydad@sha256:…`), not the commit tag. Container Apps
     resolves the configured image again on every replica start, so a revision pointing at a tag
     would fail to restart or scale once purge deleted that tag.
  2. **The running manifest always keeps a non-commit tag.** The filter only matches commit-style
     tags (`^[0-9a-f]{7}`), and Deploy Environment tags in two phases, so the job can stop at any point:
     - **before the rollout**, it re-tags the image of the revision **serving traffic** as `deployed-<env>`
       (not the app's desired image, which after a failed rollout names the failed one; repairing any
       earlier interrupted run), and also as `previous-<env>` (so a manual rollback can return to it; the automatic rollback gets its
       digest from the deploy job itself).
       This is fatal on failure, and only then does it tag the new digest `deploying-<env>`;
     - **after the rollout**, it moves `deployed-<env>` to the new digest.

     Those tags survive the purge, so the manifest is never "untagged" and the pinned digest stays pullable.

Preview what it would delete, check runs, or run it now:

```bash
az acr run --registry lazydadacr --cmd "acr purge --filter 'lazydad:^[0-9a-f]{7}.*$' --ago 30d --keep 10 --untagged --dry-run" /dev/null
az acr task list-runs --registry lazydadacr --name purge-old-images -o table
az acr task run --registry lazydadacr --name purge-old-images
```

<details><summary>PowerShell 7</summary>

```powershell
az acr run --registry lazydadacr --cmd 'acr purge --filter ''lazydad:^[0-9a-f]{7}.*$'' --ago 30d --keep 10 --untagged --dry-run' /dev/null
az acr task list-runs --registry lazydadacr --name purge-old-images -o table
az acr task run --registry lazydadacr --name purge-old-images
```

</details>

Deploy Master and Roll Back Production (both through Deploy Environment) handle both. For a **manual rollback** outside the pipelines
(e.g. to an image built before images carried their version, which Roll Back Production refuses), do the same yourself:
deploy by digest (`az acr repository show -n lazydadacr --image lazydad:<tag> --query digest -o tsv`, then
`--image lazydadacr.azurecr.io/lazydad@<digest>`), and move the `deployed-*` tag to it or lock the image
(`az acr repository update -n lazydadacr --image lazydad:<tag> --delete-enabled false`). An image built
before #17 reports version `dev` unless you also pass `--set-env-vars App__Version=<tag>`; the next pipeline
deploy removes that setting again.

Storage for context: 336 MB of Basic's 10 GB on 2026-09-25. Layers are shared, so each deploy adds
only a few MB of unique data.

## 8. Azure OpenAI with managed identities, no API key (issue #10)

The app uses the API key while `LlmProviders:AzureOpenAI:ApiKey` is set, and Entra ID when it's empty:
`DefaultAzureCredential` picks the app's managed identity in Azure and your `az login` locally. So each
environment switches when its key setting is removed, with no code change. Do staging first.

```bash
OPENAI_ID=$(az cognitiveservices account show -n lazydad-openai-resource -g $RG --query id -o tsv)

# 1. A system-assigned identity per app (data access stays per environment), with inference rights.
#    "Foundry User" covers the OpenAI and the non-OpenAI deployments (Kimi-K2.5); verified with a user account.
#    "Cognitive Services OpenAI User" is narrower, but may not cover non-OpenAI models.
for app in lazydad-app-staging lazydad-app; do
  az containerapp identity assign -n $app -g $RG --system-assigned -o none
  PID=$(az containerapp show -n $app -g $RG --query identity.principalId -o tsv)
  az role assignment create --assignee-object-id $PID --assignee-principal-type ServicePrincipal \
    --role "Foundry User" --scope "$OPENAI_ID"
done
# Your own account needs the same role for local runs without a key.

# 2. Switch one app at a time, staging first (wait a few minutes after the role assignment: RBAC takes a
#    while to apply). Dropping the key setting makes a new revision that uses Entra ID; the openai-key
#    secret stays for now, so switching back is one command.
APP=lazydad-app-staging     # then lazydad-app
az containerapp update -n $APP -g $RG --remove-env-vars LlmProviders__AzureOpenAI__ApiKey -o none
#    Check the new revision's startup tick on /status: a joke from every model, leaderboard not "failed".
#    If a model rejects the identity, switch back:
#      az containerapp update -n $APP -g $RG --set-env-vars LlmProviders__AzureOpenAI__ApiKey=secretref:openai-key -o none
#    Once it works, remove the now-unused secret, then repeat for production.
az containerapp secret remove -n $APP -g $RG --secret-names openai-key

# 3. Only once BOTH apps run without the key (disabling key auth breaks anything still using it): remove the
#    other copies, then turn key auth off for good.
az keyvault secret delete --vault-name lazydad-kv -n AzureOpenAIApiKey
dotnet user-secrets remove "LlmProviders:AzureOpenAI:ApiKey" --project src/LazyDad.Api
# Images from before the Entra ID support only know the key: stop Roll Back Production from choosing them.
gh variable set ROLLBACK_MIN_COMMIT --body "<merge commit of the Entra ID PR on master>"
az resource update --ids "$OPENAI_ID" --set properties.disableLocalAuth=true
```

<details><summary>PowerShell 7</summary>

```powershell
$OPENAI_ID = az cognitiveservices account show -n lazydad-openai-resource -g $RG --query id -o tsv

# 1. A system-assigned identity per app, with inference rights (see the bash version for the role choice).
foreach ($name in 'lazydad-app-staging', 'lazydad-app') {
  az containerapp identity assign -n $name -g $RG --system-assigned -o none
  # Not $PID: that's PowerShell's own (read-only) process id.
  $APP_PID = az containerapp show -n $name -g $RG --query identity.principalId -o tsv
  az role assignment create --assignee-object-id $APP_PID --assignee-principal-type ServicePrincipal `
    --role 'Foundry User' --scope $OPENAI_ID
}
# Your own account needs the same role for local runs without a key.

# 2. Switch one app at a time, staging first (wait a few minutes after the role assignment).
$APP = 'lazydad-app-staging'     # then lazydad-app
az containerapp update -n $APP -g $RG --remove-env-vars LlmProviders__AzureOpenAI__ApiKey -o none
#    Check the new revision's startup tick on /status. If a model rejects the identity, switch back:
#      az containerapp update -n $APP -g $RG --set-env-vars LlmProviders__AzureOpenAI__ApiKey=secretref:openai-key -o none
#    Once it works, remove the now-unused secret, then repeat for production.
az containerapp secret remove -n $APP -g $RG --secret-names openai-key

# 3. Only once BOTH apps run without the key: remove the other copies, then turn key auth off for good.
az keyvault secret delete --vault-name lazydad-kv -n AzureOpenAIApiKey
dotnet user-secrets remove 'LlmProviders:AzureOpenAI:ApiKey' --project src/LazyDad.Api
# Images from before the Entra ID support only know the key: stop Roll Back Production from choosing them.
gh variable set ROLLBACK_MIN_COMMIT --body '<merge commit of the Entra ID PR on master>'
az resource update --ids $OPENAI_ID --set properties.disableLocalAuth=true
```

</details>

## 9. Azure SQL with Entra ID, no passwords (issue #11)

Everything connects as `sqladmin` today. The target is least-privilege Entra identities and no SQL
passwords anywhere. It uses the apps' **system-assigned identities**, one per app, so staging can't reach
the prod database. Section 8 (Azure OpenAI) assigns the same ones; if you haven't done that yet:

```bash
for app in lazydad-app-staging lazydad-app; do
  az containerapp identity assign -n $app -g $RG --system-assigned -o none
done
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($name in 'lazydad-app-staging', 'lazydad-app') {
  az containerapp identity assign -n $name -g $RG --system-assigned -o none
}
```

</details>

| Principal | Database | Roles |
|---|---|---|
| `lazydad-app` (system-assigned) | `lazydad-db` | `db_datareader`, `db_datawriter` |
| `lazydad-app-staging` (system-assigned) | `lazydad-db-staging` | `db_datareader`, `db_datawriter` |
| `lazydad-github-cd` (production's migrations) | `lazydad-db` | `db_ddladmin`, `db_datareader`, `db_datawriter` |
| `lazydad-github-staging` (staging's migrations, section 11) | `lazydad-db-staging` | `db_ddladmin`, `db_datareader`, `db_datawriter` |

**1. Create the database users**, as the server's Entra admin (your account). Use the portal's Query editor,
Azure Data Studio or `sqlcmd -G`. `WITH OBJECT_ID` avoids a directory lookup by display name:

```bash
az containerapp show -n lazydad-app -g $RG --query identity.principalId -o tsv          # <app-oid>
az containerapp show -n lazydad-app-staging -g $RG --query identity.principalId -o tsv  # <staging-oid>
az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv                # <cd-oid>
```

*PowerShell 7: the same commands.*

```sql
-- In lazydad-db:
CREATE USER [lazydad-app] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<app-oid>';
ALTER ROLE db_datareader ADD MEMBER [lazydad-app];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-app];
CREATE USER [lazydad-github-cd] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<cd-oid>';
ALTER ROLE db_ddladmin ADD MEMBER [lazydad-github-cd];
ALTER ROLE db_datareader ADD MEMBER [lazydad-github-cd];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-github-cd];

-- In lazydad-db-staging: the same, with [lazydad-app-staging] and <staging-oid> instead of the prod app, and
-- [lazydad-github-staging] (section 11; its object id instead of <cd-oid>) instead of [lazydad-github-cd].
```

**2. Switch staging, then prod**, one app at a time. The password connection string stays in `sql-conn`
until the lock-down, so switching back is one command:

```bash
APP=lazydad-app-staging; DB=lazydad-db-staging; ENV=staging     # then: lazydad-app / lazydad-db / production
# A new secret with the managed-identity connection string (system-assigned needs no User Id), and a
# revision that uses it. The old revision serves until the new one is up.
az containerapp secret set -n $APP -g $RG --secrets \
  "sql-conn-entra=Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Database=$DB;Authentication=Active Directory Managed Identity;Encrypt=True"
az containerapp update -n $APP -g $RG --set-env-vars ConnectionStrings__DefaultConnection=secretref:sql-conn-entra \
  --revision-suffix entra-sql -o none
# Check the new revision's startup tick on /status: jokes saved, leaderboard not "failed". If it can't
# reach the database, switch back:
#   az containerapp update -n $APP -g $RG --set-env-vars ConnectionStrings__DefaultConnection=secretref:sql-conn --revision-suffix password-sql -o none

# CD: without the environment secret, Deploy Environment migrates with Entra ID as the environment's deploy identity
# (lazydad-github-cd for production, lazydad-github-staging for staging: section 11).
gh secret delete SQL_CONNECTION_STRING --env $ENV
```

<details><summary>PowerShell 7</summary>

```powershell
# $ENV_NAME, not $ENV: too close to PowerShell's $env: drive.
$APP = 'lazydad-app-staging'; $DB = 'lazydad-db-staging'; $ENV_NAME = 'staging'     # then: lazydad-app / lazydad-db / production
az containerapp secret set -n $APP -g $RG --secrets `
  "sql-conn-entra=Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Database=$DB;Authentication=Active Directory Managed Identity;Encrypt=True"
az containerapp update -n $APP -g $RG --set-env-vars ConnectionStrings__DefaultConnection=secretref:sql-conn-entra `
  --revision-suffix entra-sql -o none
# Check the new revision's startup tick on /status. If it can't reach the database, switch back:
#   az containerapp update -n $APP -g $RG --set-env-vars ConnectionStrings__DefaultConnection=secretref:sql-conn --revision-suffix password-sql -o none

# CD: without the environment secret, Deploy Environment migrates with Entra ID as the environment's deploy identity
# (lazydad-github-cd for production, lazydad-github-staging for staging: section 11).
gh secret delete SQL_CONNECTION_STRING --env $ENV_NAME
```

</details>

Run **Deploy Master** (or Deploy Branch to Staging with *run-migrations* for staging): the migration step logs
"Migrating <database> with Entra ID", and the smoke tests prove the app reads and writes. If the migration
can't log in, put the secret back (`gh secret set SQL_CONNECTION_STRING --env $ENV`) and check the
deploy identity's user in that database (`lazydad-github-staging` or `lazydad-github-cd`). Then repeat for production.

**3. Lock down**, only once both apps **and** both migration runs work without the password:

```bash
# Every stored copy of the password goes. Key Vault only soft-deletes, so purge too (allowed while purge
# protection is off; otherwise it stays recoverable until the retention period ends).
az keyvault secret delete --vault-name lazydad-kv -n SqlConnectionString
az keyvault secret purge  --vault-name lazydad-kv -n SqlConnectionString
for app in lazydad-app-staging lazydad-app; do az containerapp secret remove -n $app -g $RG --secret-names sql-conn; done
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True" \
  --project src/LazyDad.Api
# sqladmin stops working everywhere, so any copy of the password left anywhere is useless from here on.
az sql server ad-only-auth enable -g $RG -n lazydad-sql-swedencentral
```

<details><summary>PowerShell 7</summary>

```powershell
az keyvault secret delete --vault-name lazydad-kv -n SqlConnectionString
az keyvault secret purge  --vault-name lazydad-kv -n SqlConnectionString
foreach ($name in 'lazydad-app-staging', 'lazydad-app') { az containerapp secret remove -n $name -g $RG --secret-names sql-conn }
dotnet user-secrets set 'ConnectionStrings:DefaultConnection' `
  'Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True' `
  --project src/LazyDad.Api
az sql server ad-only-auth enable -g $RG -n lazydad-sql-swedencentral
```

</details>

Entra-only authentication can be turned off again (`ad-only-auth disable`) if something was missed.
The CI job `clean-database-migrations` uses SQL auth against its own throwaway container, so it's unaffected.

## 10. Monitoring and logs: Grafana Cloud

The app sends traces, metrics and logs over OpenTelemetry (OTLP) straight to a Grafana Cloud stack in the EU
(free tier; no collector or agent to run). Only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set: local runs, tests and
older images send nothing. What goes out, and what doesn't:

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
The console logs still go to Log Analytics as the fallback (step 4).

**1. Token, into Key Vault.** In Grafana Cloud: *Connections → OpenTelemetry (OTLP) → View connection details*,
generate a token (it can write metrics, logs and traces); the instance ID is shown there too. Keep the token out of
the repo and chat. The one copy lives in Key Vault as `OtlpHeaders` (a placeholder since the original setup), in
`OTEL_EXPORTER_OTLP_HEADERS`'s format: basic auth `<instance id>:<token>`, the space URL-encoded (per the OTLP spec).
Both apps reference it, so a new token is one update.

```bash
KV_ID=$(az keyvault show -n lazydad-kv --query id -o tsv)
INSTANCE_ID=<instance id from the connection details>
read -rs TOKEN   # paste the token; not echoed, not in the shell history
AUTH=$(printf '%s:%s' "$INSTANCE_ID" "$TOKEN" | base64 -w0); unset TOKEN
# Check the credentials first (the app doesn't log export failures): 200 or 204 = accepted, 401 = wrong token or instance ID.
curl -s -o /dev/null -w '%{http_code}\n' -X POST -H "Authorization: Basic $AUTH" -H 'Content-Type: application/json' \
  -d '{"resourceLogs":[]}' https://otlp-gateway-prod-eu-north-0.grafana.net/otlp/v1/logs
az keyvault secret set --vault-name lazydad-kv -n OtlpHeaders --value "Authorization=Basic%20$AUTH" -o none
unset AUTH
```

<details><summary>PowerShell 7</summary>

```powershell
$KV_ID = az keyvault show -n lazydad-kv --query id -o tsv
$INSTANCE_ID = '<instance id from the connection details>'
$TOKEN = Read-Host 'Grafana token' -MaskInput   # not echoed; Read-Host input isn't saved in the history
# "${INSTANCE_ID}:" in braces: "$INSTANCE_ID:" would read as a scoped variable.
$AUTH = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${INSTANCE_ID}:$TOKEN")); Remove-Variable TOKEN
# Check the credentials first: 200 or 204 = accepted, 401 = wrong token or instance ID.
(Invoke-WebRequest -Method Post -Uri https://otlp-gateway-prod-eu-north-0.grafana.net/otlp/v1/logs `
  -Headers @{ Authorization = "Basic $AUTH" } -ContentType application/json `
  -Body '{"resourceLogs":[]}' -SkipHttpErrorCheck).StatusCode
az keyvault secret set --vault-name lazydad-kv -n OtlpHeaders --value "Authorization=Basic%20$AUTH" -o none
# Confirm the vault holds exactly that value (prints only True or False):
(az keyvault secret show --vault-name lazydad-kv -n OtlpHeaders --query value -o tsv) -eq "Authorization=Basic%20$AUTH"
Remove-Variable AUTH
```

</details>

**2. Per app, staging first.** Deploys keep these settings (Deploy Environment only swaps the image), and images
from before this change ignore them, so rollbacks are fine. Each app reads the secret with its system-assigned
identity: prod's already reads the vault (`Key Vault Secrets User` on all of it); staging gets that role on this
one secret only, so it can't read prod's other secrets.

```bash
# Staging only: its identity (section 8 or 9 may have assigned it already; this is then a no-op) and access to the secret.
az containerapp identity assign -n lazydad-app-staging -g $RG --system-assigned -o none
STAGING_ID=$(az containerapp show -n lazydad-app-staging -g $RG --query identity.principalId -o tsv)
az role assignment create --assignee-object-id $STAGING_ID --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$KV_ID/secrets/OtlpHeaders" -o none
# Wait a few minutes for the role to apply: a reference the identity can't read fails the new revision (the old one keeps serving).

APP=lazydad-app-staging; ENV=staging     # then: lazydad-app / production
az containerapp secret set -n $APP -g $RG \
  --secrets "otlp-headers=keyvaultref:https://lazydad-kv.vault.azure.net/secrets/OtlpHeaders,identityref:system"
az containerapp update -n $APP -g $RG -o none --set-env-vars \
  OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-eu-north-0.grafana.net/otlp \
  OTEL_EXPORTER_OTLP_HEADERS=secretref:otlp-headers \
  OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=$ENV
```

<details><summary>PowerShell 7</summary>

```powershell
# Staging only: its identity (a no-op if section 8 or 9 assigned it already) and access to the secret.
az containerapp identity assign -n lazydad-app-staging -g $RG --system-assigned -o none
$STAGING_ID = az containerapp show -n lazydad-app-staging -g $RG --query identity.principalId -o tsv
az role assignment create --assignee-object-id $STAGING_ID --assignee-principal-type ServicePrincipal `
  --role 'Key Vault Secrets User' --scope "$KV_ID/secrets/OtlpHeaders" -o none
# Wait a few minutes for the role to apply: a reference the identity can't read fails the new revision.

$APP = 'lazydad-app-staging'; $ENV_NAME = 'staging'     # then: lazydad-app / production
az containerapp secret set -n $APP -g $RG `
  --secrets 'otlp-headers=keyvaultref:https://lazydad-kv.vault.azure.net/secrets/OtlpHeaders,identityref:system'
az containerapp update -n $APP -g $RG -o none --set-env-vars `
  OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-eu-north-0.grafana.net/otlp `
  OTEL_EXPORTER_OTLP_HEADERS=secretref:otlp-headers `
  "OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=$ENV_NAME"
```

</details>

Check in Grafana's *Explore*: logs `{service_name="lazydad-app-staging"}`, a `joke tick` trace from the new
revision's startup tick, and the metric `lazydad_jokes_total`. If nothing arrives although the `curl` check passed,
compare the app's settings with the commands above (`az containerapp show -n $APP -g $RG --query
properties.template.containers[0].env`). To switch telemetry off again:
`az containerapp update -n $APP -g $RG --remove-env-vars OTEL_EXPORTER_OTLP_ENDPOINT -o none`.

Then, on prod only, remove the placeholders from the original setup (the app never read them; the endpoint isn't secret):

```bash
az containerapp update -n lazydad-app -g $RG --remove-env-vars OpenTelemetry__Endpoint OpenTelemetry__Headers -o none
az containerapp secret remove -n lazydad-app -g $RG --secret-names otlp-endpoint
az keyvault secret delete --vault-name lazydad-kv -n OtlpEndpoint
```

*PowerShell 7: the same commands.*

A new token later (expired or leaked): update `OtlpHeaders` as in step 1, then restart each app's active revision;
the value is read when a replica starts. Revoke the old token in Grafana.

```bash
for app in lazydad-app-staging lazydad-app; do
  az containerapp revision restart -n $app -g $RG \
    --revision "$(az containerapp show -n $app -g $RG --query properties.latestReadyRevisionName -o tsv)"
done
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($name in 'lazydad-app-staging', 'lazydad-app') {
  az containerapp revision restart -n $name -g $RG `
    --revision (az containerapp show -n $name -g $RG --query properties.latestReadyRevisionName -o tsv)
}
```

</details>

**3. Uptime check and alerts (prod only).** Staging's ingress admits listed IPs only, and it scales to zero, so it
would look down and idle all the time.

- *Testing & synthetics → Synthetics → Add check → HTTP*:
  `https://lazydad-app.wittyfield-6bfb5662.westeurope.azurecontainerapps.io/healthz`, every 5 minutes from 2–3 EU
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

**4. Log Analytics: cap the fallback.** The console logs keep going to the Container Apps environment's workspace
(30 days). They were about 8 MB a month, at most 2.2 MB a day (2026-09-26), so a 0.1 GB daily cap never bites
in normal use but stops a logging bug from running up a bill:

```bash
az monitor log-analytics workspace update -g $RG -n workspace-lazydadrgseCk --quota 0.1
```

*PowerShell 7: the same commands.*

## 11. Branch previews can't reach production: a staging-only deploy identity

**Why.** Deploy Branch to Staging runs the chosen branch's own code in the `staging` GitHub environment: its
workflow files, and its `dotnet build` (which can run any MSBuild target a package brings along). Until this
section, the `staging` environment logged in as `lazydad-github-cd`, the identity that also deploys production.
So code in a previewed branch, say a compromised package in a dependency update, could change production. Two
more paths went through passwords that staging can read: its `SQL_CONNECTION_STRING` secret and the staging
app's `sql-conn` secret are the `sqladmin` login, which reaches every database on the server.

**The target:**

| GitHub environment | Logs in as | Can change |
|---|---|---|
| `staging` (`*/*` branches and `master`) | `lazydad-github-staging` | `lazydad-app-staging`; push images; SQL firewall rules; the staging DB schema |
| `production` (`master` only) | `lazydad-github-cd` | `lazydad-app`; push images; SQL firewall rules; the prod DB schema |

Both still push to the same registry, so staging could re-point tags like `previous-production` or `<sha>`. So
nothing that decides what production runs trusts a tag: Deploy Master deploys the digest its own build produced,
its automatic rollback returns to the digest the production job read from production's revisions (a job output),
and the Roll Back workflow only accepts an image whose digest production's own revisions ran.

**How GitHub picks the identity.** `azure/login` uses `vars.AZURE_CLIENT_ID`. A variable set on an environment
overrides the repository's for jobs in that environment, and every job that logs in to Azure runs in one. Azure
accepts the login because each identity has a *federated credential* for exactly one subject,
`repo:mykolad@<id>/lazydad@<id>:environment:<env>` (section 6). So the staging identity can't be used from the
`production` environment, and the other way round.

Do this before sections 8 and 9 if you haven't done those yet: section 9 then creates the staging database's
deploy user for `lazydad-github-staging` right away (step 3 here does it otherwise).

**1. A custom role for what a deploy needs outside its app.** The built-in roles reach too far: `Contributor`
on the Container Apps environment could delete it (and the prod app with it), and `SQL Server Contributor`
could delete the prod database. A deploy only has to *join* the environment (`az containerapp update` checks
that) and open and close its SQL firewall rule:

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
az role definition create --role-definition @lazydad-deployer.json
rm lazydad-deployer.json
```

<details><summary>PowerShell 7</summary>

```powershell
$RG_ID = az group show -n $RG --query id -o tsv
# A file, not an argument: az.cmd would strip the JSON's quotes (see the note at the top).
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
az role definition create --role-definition '@lazydad-deployer.json'
Remove-Item lazydad-deployer.json
```

</details>

**2. The staging identity**, its roles, and its trust in the `staging` environment only:

```bash
az identity create -g $RG -n lazydad-github-staging -l westeurope
STG_PID=$(az identity show -g $RG -n lazydad-github-staging --query principalId -o tsv)
STG_APP_ID=$(az containerapp show -n lazydad-app-staging -g $RG --query id -o tsv)
for role in AcrPush Reader; do   # push images; Reader because az acr login looks the registry up in ARM
  az role assignment create --assignee-object-id $STG_PID --assignee-principal-type ServicePrincipal --role $role --scope "$ACR_ID"
done
az role assignment create --assignee-object-id $STG_PID --assignee-principal-type ServicePrincipal --role Contributor --scope "$STG_APP_ID"
az role assignment create --assignee-object-id $STG_PID --assignee-principal-type ServicePrincipal --role "LazyDad Deployer" --scope "$RG_ID"

PREFIX="repo:mykolad@$(gh api users/mykolad --jq .id)/lazydad@$(gh api repos/mykolad/lazydad --jq .id)"
az identity federated-credential create -g $RG --identity-name lazydad-github-staging -n github-staging-immutable \
  --issuer https://token.actions.githubusercontent.com \
  --subject "$PREFIX:environment:staging" --audiences api://AzureADTokenExchange
```

<details><summary>PowerShell 7</summary>

```powershell
az identity create -g $RG -n lazydad-github-staging -l westeurope
$STG_PID = az identity show -g $RG -n lazydad-github-staging --query principalId -o tsv
$STG_APP_ID = az containerapp show -n lazydad-app-staging -g $RG --query id -o tsv
foreach ($role in 'AcrPush', 'Reader') {   # push images; Reader because az acr login looks the registry up in ARM
  az role assignment create --assignee-object-id $STG_PID --assignee-principal-type ServicePrincipal --role $role --scope $ACR_ID
}
az role assignment create --assignee-object-id $STG_PID --assignee-principal-type ServicePrincipal --role Contributor --scope $STG_APP_ID
az role assignment create --assignee-object-id $STG_PID --assignee-principal-type ServicePrincipal --role 'LazyDad Deployer' --scope $RG_ID

$PREFIX = "repo:mykolad@$(gh api users/mykolad --jq .id)/lazydad@$(gh api repos/mykolad/lazydad --jq .id)"
az identity federated-credential create -g $RG --identity-name lazydad-github-staging -n github-staging-immutable `
  --issuer https://token.actions.githubusercontent.com `
  --subject "${PREFIX}:environment:staging" --audiences api://AzureADTokenExchange
```

</details>

**3. Its database user**, in `lazydad-db-staging` only, as the server's Entra admin (like section 9). It runs
the staging migrations once the environment has no `SQL_CONNECTION_STRING` secret (step 5). Skip this if
section 9 already created it.

```bash
az identity show -g $RG -n lazydad-github-staging --query principalId -o tsv   # <staging-cd-oid>
```

*PowerShell 7: the same command.*

```sql
-- In lazydad-db-staging:
CREATE USER [lazydad-github-staging] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<staging-cd-oid>';
ALTER ROLE db_ddladmin ADD MEMBER [lazydad-github-staging];
ALTER ROLE db_datareader ADD MEMBER [lazydad-github-staging];
ALTER ROLE db_datawriter ADD MEMBER [lazydad-github-staging];
```

**4. Point each GitHub environment at its identity.** Set both environment variables before removing the
repository-wide one (step 6), so no run is ever left without a client id:

```bash
gh variable set AZURE_CLIENT_ID --env staging    --body "$(az identity show -g $RG -n lazydad-github-staging --query clientId -o tsv)"
gh variable set AZURE_CLIENT_ID --env production --body "$(az identity show -g $RG -n lazydad-github-cd --query clientId -o tsv)"
```

<details><summary>PowerShell 7</summary>

```powershell
gh variable set AZURE_CLIENT_ID --env staging    --body (az identity show -g $RG -n lazydad-github-staging --query clientId -o tsv)
gh variable set AZURE_CLIENT_ID --env production --body (az identity show -g $RG -n lazydad-github-cd --query clientId -o tsv)
```

</details>

Test it: *Actions → Deploy Branch to Staging → Run workflow* on `master`, with *run-migrations* ticked. The
build (image push), the migrations (still with the password while that secret exists), the rollout and the
smoke tests all run as the new identity. The next Deploy Master run checks production's side.

**5. Take the passwords away from staging.** Staging has to run on its managed identity first: section 8 step 2
(Azure OpenAI) and section 9 step 2 (SQL) for `lazydad-app-staging`. Then remove what branch code could read
that works outside staging:

```bash
gh secret delete SQL_CONNECTION_STRING --env staging   # migrations switch to Entra ID as lazydad-github-staging
az containerapp secret remove -n lazydad-app-staging -g $RG --secret-names sql-conn openai-key
```

*PowerShell 7: the same commands.*

Run Deploy Branch to Staging with *run-migrations* again: the migration step logs "Migrating
lazydad-db-staging with Entra ID". (`sqladmin` itself keeps working for prod until section 9's lock-down.)

**6. Remove the old way in.** `lazydad-github-cd` no longer needs anything in staging:

```bash
az identity federated-credential delete -g $RG --identity-name lazydad-github-cd -n github-staging-immutable --yes
az role assignment delete --assignee "$(az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv)" \
  --role Contributor --scope "$(az containerapp show -n lazydad-app-staging -g $RG --query id -o tsv)"
gh variable delete AZURE_CLIENT_ID   # the repository-wide one; each environment has its own now
```

<details><summary>PowerShell 7</summary>

```powershell
az identity federated-credential delete -g $RG --identity-name lazydad-github-cd -n github-staging-immutable --yes
az role assignment delete --assignee (az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv) `
  --role Contributor --scope (az containerapp show -n lazydad-app-staging -g $RG --query id -o tsv)
gh variable delete AZURE_CLIENT_ID   # the repository-wide one; each environment has its own now
```

</details>

```sql
-- In lazydad-db-staging, if section 9 created it there:
DROP USER IF EXISTS [lazydad-github-cd];
```

**7. Least privilege for production's identity too (optional, same idea).** It still has `Contributor` on the
Container Apps environment and `SQL Server Contributor`, which reach further than a deploy needs. Swap them for
the custom role, then check with a Deploy Master run:

```bash
CD_PID=$(az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv)
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role "LazyDad Deployer" --scope "$RG_ID"
az role assignment delete --assignee $CD_PID --role Contributor --scope "$(az containerapp env show -n lazydad-cae -g $RG --query id -o tsv)"
az role assignment delete --assignee $CD_PID --role "SQL Server Contributor" --scope "$(az sql server show -g $RG -n lazydad-sql-swedencentral --query id -o tsv)"
```

<details><summary>PowerShell 7</summary>

```powershell
$CD_PID = az identity show -g $RG -n lazydad-github-cd --query principalId -o tsv
az role assignment create --assignee-object-id $CD_PID --assignee-principal-type ServicePrincipal --role 'LazyDad Deployer' --scope $RG_ID
az role assignment delete --assignee $CD_PID --role Contributor --scope (az containerapp env show -n lazydad-cae -g $RG --query id -o tsv)
az role assignment delete --assignee $CD_PID --role 'SQL Server Contributor' --scope (az sql server show -g $RG -n lazydad-sql-swedencentral --query id -o tsv)
```

</details>

Check what each identity can do, at any time:

```bash
for id in lazydad-github-staging lazydad-github-cd; do
  echo "== $id"
  az role assignment list --assignee "$(az identity show -g $RG -n $id --query principalId -o tsv)" --all \
    --query "[].{role:roleDefinitionName, scope:scope}" -o table
done
```

<details><summary>PowerShell 7</summary>

```powershell
foreach ($id in 'lazydad-github-staging', 'lazydad-github-cd') {
  "== $id"
  az role assignment list --assignee (az identity show -g $RG -n $id --query principalId -o tsv) --all `
    --query '[].{role:roleDefinitionName, scope:scope}' -o table
}
```

</details>

What a previewed branch can still do afterwards: anything to staging (its app, its database, LLM calls, which
cost tokens), push images that production never runs, and open a SQL firewall rule (useless without a login).
It can't change what production runs, or make production run anything else.

**One gap remains: availability.** Both identities can write to the whole registry (`AcrPush`), so a branch could
also move production's protection tags (`deployed-production`, `previous-production`) off the image production
runs. The weekly purge would then delete that manifest, and production's next restart or scale-out would fail to
pull it (the running replica keeps running; a Deploy Master run fixes it). An ACR lock doesn't help: `AcrPush`
can lift locks too. The fix is repository-scoped permissions (the registry's "RBAC Registry + ABAC Repository
Permissions" mode): previews write only to a `lazydad-preview` repository, and production's identity copies
the image it deploys into a production-only `lazydad` repository with `az acr import` (same digest). That's a
registry migration of its own, planned separately.
