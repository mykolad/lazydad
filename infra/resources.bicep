// Everything in lazydad-rg that's in Bicep yet: the logs, the vault, the identities and every role assignment. The rest
// (the registry, the database, the models, the apps, Traffic Manager) is still made by infra/deployment-setup.md and
// referenced here as existing, until the next PRs of #87 bring it in.
param location string = resourceGroup().location
param ownerPrincipalId string
param githubSubjectPrefix string
param deployerRoleId string

var roles = {
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  managedIdentityOperator: 'f1a07417-d97a-45cb-824c-7a7467783830'
  foundryUser: '53ca6127-db72-4b80-b1b0-d745d6d5456d'
}

// The apps' console output, the fallback next to Grafana and the place to look when a revision doesn't start. The daily
// cap never bites in normal use (the apps log about 8 MB a month) but stops a logging bug from running up a bill. The
// name is the one the portal generated.
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'workspace-lazydadrgseCk'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json('0.1')
    }
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

// Only what really is a secret (the Grafana tokens, the Jev keys, the sign-in peppers and client secrets, the origin
// certificate), each set by hand (infra/README.md), and the keys that wrap the sign-in cookies' key ring. Purge
// protection stays off, so a leaked token can be purged at once rather than staying recoverable for 90 days.
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'lazydad-kv'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    accessPolicies: []
  }
}

// One key per environment, so staging can't unwrap production's key ring. Deploying an existing key changes nothing:
// it's never rotated this way.
resource dataProtectionKeys 'Microsoft.KeyVault/vaults/keys@2023-07-01' = [
  for name in ['DataProtection', 'DataProtectionStaging']: {
    parent: vault
    name: name
    properties: {
      kty: 'RSA'
      keySize: 2048
      keyOps: ['encrypt', 'decrypt', 'sign', 'verify', 'wrapKey', 'unwrapKey']
      // Key Vault's default: a notice 30 days before expiry. The keys have none, so it never fires.
      rotationPolicy: {
        lifetimeActions: [
          {
            action: {
              type: 'notify'
            }
            trigger: {
              timeBeforeExpiry: 'P30D'
            }
          }
        ]
      }
    }
  }
]

// All three apps pull their images as this identity, which can do nothing else.
resource acrPull 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lazydad-acr-pull'
  location: location
}

// Both prod apps, and both prod environments (to read the origin certificate): one database user and the same rights in
// both regions. Staging's app has a system-assigned identity of its own, so nothing staging runs can act as production.
resource production 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lazydad-production'
  location: location
}

resource loadTestApp 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lazydad-loadtest-app'
  location: location
}

// One deploy identity per GitHub environment, each trusting only that environment (GitHub's immutable subject, so a
// renamed or re-created repository can't inherit it). Deploy Branch to Staging runs a branch's own code as staging's,
// which is why it has nothing in production.
resource productionDeployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lazydad-github-cd'
  location: location
}

resource productionDeployerTrust 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: productionDeployer
  name: 'github-production-immutable'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: '${githubSubjectPrefix}:environment:production'
    audiences: ['api://AzureADTokenExchange']
  }
}

resource stagingDeployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lazydad-github-staging'
  location: location
}

resource stagingDeployerTrust 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: stagingDeployer
  name: 'github-staging-immutable'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: '${githubSubjectPrefix}:environment:staging'
    audiences: ['api://AzureADTokenExchange']
  }
}

// Load Test Environment runs from any branch, so this identity changes only the load test's own resource group and uses
// what the load-test app needs from the shared ones (infra/deployment-setup.md, section 12, "Load test").
resource loadTestDeployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lazydad-github-loadtest'
  location: location
}

resource loadTestDeployerTrust 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: loadTestDeployer
  name: 'github-loadtest-immutable'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: '${githubSubjectPrefix}:environment:loadtest'
    audiences: ['api://AzureADTokenExchange']
  }
}

// Not in Bicep yet: made by infra/deployment-setup.md.
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: 'lazydadacr'
}

resource purgeTask 'Microsoft.ContainerRegistry/registries/tasks@2019-06-01-preview' existing = {
  parent: registry
  name: 'purge-old-images'
}

resource models 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: 'lazydad-openai-resource'
}

resource westEuropeEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: 'lazydad-cae'
}

resource productionApps 'Microsoft.App/containerApps@2024-03-01' existing = [
  for name in ['lazydad-app', 'lazydad-app-swedencentral']: {
    name: name
  }
]

resource stagingApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: 'lazydad-app-staging'
}

// Deploys join the environments and open their SQL firewall rule (LazyDad Deployer), and change their own apps only.
resource productionDeployerJoins 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, productionDeployer.id, deployerRoleId)
  properties: {
    principalId: productionDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: deployerRoleId
  }
}

resource stagingDeployerJoins 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, stagingDeployer.id, deployerRoleId)
  properties: {
    principalId: stagingDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: deployerRoleId
  }
}

resource productionDeployerApps 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (name, i) in ['lazydad-app', 'lazydad-app-swedencentral']: {
    scope: productionApps[i]
    name: guid(productionApps[i].id, productionDeployer.id, roles.contributor)
    properties: {
      principalId: productionDeployer.properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
    }
  }
]

resource stagingDeployerApp 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: stagingApp
  name: guid(stagingApp.id, stagingDeployer.id, roles.contributor)
  properties: {
    principalId: stagingDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
  }
}

// Load tests add their app to lazydad-cae alone, and read staging's app for its `home` rule.
resource loadTestDeployerJoins 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: westEuropeEnvironment
  name: guid(westEuropeEnvironment.id, loadTestDeployer.id, deployerRoleId)
  properties: {
    principalId: loadTestDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: deployerRoleId
  }
}

resource loadTestDeployerReadsStaging 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: stagingApp
  name: guid(stagingApp.id, loadTestDeployer.id, roles.reader)
  properties: {
    principalId: loadTestDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.reader)
  }
}

// The load-test app runs as lazydad-loadtest-app and pulls as lazydad-acr-pull: its deploy identity may attach both.
resource loadTestDeployerAttachesPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acrPull
  name: guid(acrPull.id, loadTestDeployer.id, roles.managedIdentityOperator)
  properties: {
    principalId: loadTestDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.managedIdentityOperator)
  }
}

resource loadTestDeployerAttachesApp 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: loadTestApp
  name: guid(loadTestApp.id, loadTestDeployer.id, roles.managedIdentityOperator)
  properties: {
    principalId: loadTestDeployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.managedIdentityOperator)
  }
}

module registryAccess 'modules/registryAccess.bicep' = {
  name: 'registry-access'
  params: {
    registryName: registry.name
    ownerPrincipalId: ownerPrincipalId
    pullIdentityId: acrPull.id
    pullPrincipalId: acrPull.properties.principalId
    purgeTaskPrincipalId: purgeTask.identity.principalId
    productionDeployerId: productionDeployer.id
    productionDeployerPrincipalId: productionDeployer.properties.principalId
    stagingDeployerId: stagingDeployer.id
    stagingDeployerPrincipalId: stagingDeployer.properties.principalId
    loadTestDeployerId: loadTestDeployer.id
    loadTestDeployerPrincipalId: loadTestDeployer.properties.principalId
  }
}

module vaultAccess 'modules/vaultAccess.bicep' = {
  name: 'vault-access'
  dependsOn: [dataProtectionKeys]
  params: {
    vaultName: vault.name
    ownerPrincipalId: ownerPrincipalId
    productionId: production.id
    productionPrincipalId: production.properties.principalId
    stagingAppId: stagingApp.id
    stagingAppPrincipalId: stagingApp.identity.principalId
    loadTestAppId: loadTestApp.id
    loadTestAppPrincipalId: loadTestApp.properties.principalId
  }
}

// The models: both environments' apps, and the owner for local runs. Keys are off, so these roles are the only way in.
resource productionModels 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: models
  name: guid(models.id, production.id, roles.foundryUser)
  properties: {
    principalId: production.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
  }
}

resource stagingModels 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: models
  name: guid(models.id, stagingApp.id, roles.foundryUser)
  properties: {
    principalId: stagingApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
  }
}

resource ownerModels 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: models
  name: guid(models.id, ownerPrincipalId, roles.foundryUser)
  properties: {
    principalId: ownerPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
  }
}

output productionDeployerPrincipalId string = productionDeployer.properties.principalId
output loadTestDeployerPrincipalId string = loadTestDeployer.properties.principalId
// Each GitHub environment's AZURE_CLIENT_ID variable (infra/README.md).
output githubClientIds object = {
  production: productionDeployer.properties.clientId
  staging: stagingDeployer.properties.clientId
  loadtest: loadTestDeployer.properties.clientId
}
