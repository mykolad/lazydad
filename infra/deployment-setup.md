# Deployment one-time setup (Azure + GitHub)

What was set up on 2026-09-24 to support `.github/workflows/deploy-master.yml` (Deploy Master). Every step used the `az`
and `gh` CLIs. Keep this in sync with reality until it's replaced by Bicep (IaC is on the plan).

> In Git Bash, `export MSYS_NO_PATHCONV=1` first. Otherwise the `/subscriptions/...` scopes get
> rewritten as file paths and role assignments fail with `MissingSubscription`.

```bash
RG=lazydad-rg
ACR_ID=$(az acr show -n lazydadacr --query id -o tsv)
```

## 1. Identities

| Identity | Used by | Roles |
|---|---|---|
| `lazydad-acr-pull` | both Container Apps, to pull images | `AcrPull` on `lazydadacr` |
| `lazydad-github-cd` | GitHub Actions (OIDC), to deploy | `AcrPush` + `Reader` on `lazydadacr`; `Contributor` on `lazydad-app`, `lazydad-app-staging`, `lazydad-cae`; `SQL Server Contributor` on `lazydad-sql-swedencentral` (temporary firewall rules) |

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

## 2. Staging database (free offer)

```bash
az sql db create -g $RG -s lazydad-sql-swedencentral -n lazydad-db-staging \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 --min-capacity 0.5 \
  --auto-pause-delay 60 --use-free-limit --free-limit-exhaustion-behavior AutoPause --backup-storage-redundancy Local
```

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

## 4. Prod: pull with managed identity, disable the ACR admin user

```bash
az containerapp identity assign -n lazydad-app -g $RG --user-assigned "$PULL_ID"
az containerapp registry set -n lazydad-app -g $RG --server lazydadacr.azurecr.io --identity "$PULL_ID"
az containerapp update -n lazydad-app -g $RG --revision-suffix mi-pull   # a real pull proves it works
az containerapp secret remove -n lazydad-app -g $RG --secret-names lazydadacrazurecrio-lazydadacr
az acr update -n lazydadacr --admin-enabled false
```

## 5. Staging: allow listed IPs only

Staging is public by default, and every cold start runs a real LLM joke tick, so any visitor or
crawler would cost tokens. With at least one `Allow` rule, Container Apps denies everyone else
(`403 RBAC: access denied`, answered at the ingress without waking the app):

```bash
az containerapp ingress access-restriction set -n lazydad-app-staging -g $RG \
  --rule-name home --ip-address <your-public-ip>/32 --action Allow --description "Owner's home IP"
```

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

Jobs log in only through an environment (the federated subjects are `environment:staging` and
`environment:production`), so a workflow that doesn't use one can't get an Azure token.

Branch deploys to staging (Deploy Branch to Staging): `staging` also accepts `*/*` branches, and
`production` stays master-only. In GitHub's patterns `*` doesn't cross `/`, so `*/*` matches `feature/x`.
The federated credential matches the environment, not the branch, so nothing changes in Azure:

```bash
gh api -X POST repos/mykolad/lazydad/environments/staging/deployment-branch-policies -f name='*/*' -f type=branch
```

## 7. Registry cleanup: weekly purge of old images

Every deploy pushes a new `lazydad:<short-sha>` image. An **ACR Task** (it runs inside the registry,
on a cron schedule in UTC, and costs fractions of a cent per run) deletes old ones every Sunday at 03:00 UTC:

```bash
# Git Bash: export MSYS_NO_PATHCONV=1 first, or /dev/null gets rewritten.
az acr task create --registry lazydadacr --name purge-old-images --schedule "0 3 * * 0" \
  --cmd "acr purge --filter 'lazydad:^[0-9a-f]{7}.*$' --ago 30d --keep 10 --untagged" --context /dev/null
```

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
       earlier interrupted run). This is fatal on failure, and only then does it tag the new digest `deploying-<env>`;
     - **after the rollout**, it moves `deployed-<env>` to the new digest.

     Those tags survive the purge, so the manifest is never "untagged" and the pinned digest stays pullable.

Preview what it would delete, check runs, or run it now:

```bash
az acr run --registry lazydadacr --cmd "acr purge --filter 'lazydad:^[0-9a-f]{7}.*$' --ago 30d --keep 10 --untagged --dry-run" /dev/null
az acr task list-runs --registry lazydadacr --name purge-old-images -o table
az acr task run --registry lazydadacr --name purge-old-images
```

Deploy Master handles both. For a **manual rollback** outside the pipeline, do the same yourself:
deploy by digest (`az acr repository show -n lazydadacr --image lazydad:<tag> --query digest -o tsv`, then
`--image lazydadacr.azurecr.io/lazydad@<digest>`), and move the `deployed-*` tag to it or lock the image
(`az acr repository update -n lazydadacr --image lazydad:<tag> --delete-enabled false`).

Storage for context: 336 MB of Basic's 10 GB on 2026-09-25. Layers are shared, so each deploy adds
only a few MB of unique data.
