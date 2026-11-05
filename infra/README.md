# Infrastructure

LazyDad's Azure side is moving from the runbook (`deployment-setup.md`) into Bicep, one part at a time (#87). Each
part is adopted as it is: its `what-if` against the live setup shows no changes before it's merged.

| In Bicep | Still from the runbook |
|---|---|
| The resource groups (`lazydad-rg`, `lazydad-loadtest-rg`) | The registry and its purge task (section 2) |
| The Log Analytics workspace (section 1) | The SQL server and databases (section 4) |
| The vault and its Data Protection keys (sections 3 and 7) | The models (section 5) |
| The managed identities and their GitHub trusts (sections 6 and 12) | The Container Apps environments and apps (sections 1, 7 and 10) |
| The custom roles, `LazyDad Deployer` and `LazyDad Service Tag Reader` (section 6) | Traffic Manager (section 10) |
| Every role assignment, on whichever resource | |

`main.bicep` deploys at subscription scope (it holds the resource groups and the subscription-wide role) and deploys
`resources.bicep` into `lazydad-rg`. Resources not in Bicep yet are referenced as `existing`, for the roles on them.

## Deploying

By hand, as an Owner of the subscription, from the repository's root. Preview with `what-if` first: anything other than
what you meant to change (above all a `Delete` or a `Modify` on something you didn't touch) means the file and Azure
disagree, so look before deploying.

```powershell
az login
$owner = az ad signed-in-user show --query id -o tsv
az deployment sub what-if --location westeurope --name lazydad --template-file infra/main.bicep `
  --parameters ownerPrincipalId=$owner
az deployment sub create --location westeurope --name lazydad --template-file infra/main.bicep `
  --parameters ownerPrincipalId=$owner
```

The same in bash:

```bash
owner=$(az ad signed-in-user show --query id -o tsv)
az deployment sub what-if --location westeurope --name lazydad --template-file infra/main.bicep \
  --parameters ownerPrincipalId="$owner"
az deployment sub create --location westeurope --name lazydad --template-file infra/main.bicep \
  --parameters ownerPrincipalId="$owner"
```

The deployment's `githubClientIds` output is each GitHub environment's `AZURE_CLIENT_ID` variable.

**Role assignments are named from what they grant** (`guid(scope, identity, role)`), so deploying again changes
nothing, and a role granted by hand next to Bicep's fails the deployment (`RoleAssignmentExists`) rather than being
kept twice: delete the hand-made one, and grant it in Bicep.

**The secrets must exist before the roles on them.** Bicep never holds a secret's value; the roles that let each app read
its own secrets are on the secrets themselves. In a new vault, the first deployment fails at those: set the secrets
(runbook sections 7, 10 and 11), then deploy again.
