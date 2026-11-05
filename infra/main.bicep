// LazyDad's Azure side, as far as it's in Bicep yet (#87): the resource groups, the custom roles, and resources.bicep
// in lazydad-rg. Deployed at subscription scope, since it holds the groups and the subscription-wide role. See
// infra/README.md for deploying it and for what isn't in Bicep yet.
targetScope = 'subscription'

param location string = 'westeurope'

@description('The owner\'s Entra object id: data roles for local runs and for setting secret values.')
param ownerPrincipalId string

@description('GitHub\'s OIDC subject prefix for this repository (immutable format: owner and repository ids).')
param githubSubjectPrefix string = 'repo:mykolad@2202717/lazydad@1383796352'

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'lazydad-rg'
  location: location
}

// The load test's resources, created and deleted by Load Test Environment: empty between tests.
resource loadTestGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'lazydad-loadtest-rg'
  location: location
}

// What a deploy needs outside the app it deploys. The built-in roles reach too far: Contributor on the environment could
// delete it (and the prod app with it), SQL Server Contributor the prod database. A deploy only joins the environment
// (az containerapp update checks that) and opens and closes its SQL firewall rule.
resource deployerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'LazyDad Deployer')
  properties: {
    roleName: 'LazyDad Deployer'
    description: 'Deploy Environment\'s needs outside the app it deploys: join the Container Apps environment, open and close SQL firewall rules.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.App/managedEnvironments/read'
          'Microsoft.App/managedEnvironments/join/action'
          'Microsoft.Sql/servers/read'
          'Microsoft.Sql/servers/firewallRules/read'
          'Microsoft.Sql/servers/firewallRules/write'
          'Microsoft.Sql/servers/firewallRules/delete'
        ]
      }
    ]
    assignableScopes: [group.id]
  }
}

// Production's deploys keep the prod apps' ingress rules in step with Traffic Manager's health-check addresses (the
// AzureTrafficManager service tag), which Azure answers only at the subscription's level.
resource serviceTagReaderRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'LazyDad Service Tag Reader')
  properties: {
    roleName: 'LazyDad Service Tag Reader'
    description: 'Reads Azure\'s service tags (Traffic Manager\'s health-check addresses) for the prod apps\' ingress rules.'
    type: 'CustomRole'
    permissions: [
      {
        actions: ['Microsoft.Network/locations/serviceTags/read']
      }
    ]
    assignableScopes: [subscription().id]
  }
}

module resources 'resources.bicep' = {
  scope: group
  name: 'resources'
  params: {
    location: location
    ownerPrincipalId: ownerPrincipalId
    githubSubjectPrefix: githubSubjectPrefix
    deployerRoleId: deployerRole.id
  }
}

resource productionDeployerServiceTags 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, 'lazydad-github-cd', serviceTagReaderRole.id)
  properties: {
    principalId: resources.outputs.productionDeployerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceTagReaderRole.id
  }
}

module loadTestGroupAccess 'modules/loadTestGroupAccess.bicep' = {
  scope: loadTestGroup
  name: 'load-test-group-access'
  params: {
    principalId: resources.outputs.loadTestDeployerPrincipalId
  }
}

// Each GitHub environment's AZURE_CLIENT_ID variable (infra/README.md).
output githubClientIds object = resources.outputs.githubClientIds
