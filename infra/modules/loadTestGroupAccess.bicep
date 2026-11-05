// Load Test Environment's deploy identity creates and deletes everything in the load test's group, and nothing outside.
targetScope = 'resourceGroup'

param principalId string

var contributor = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource loadTestDeployerGroup 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, 'lazydad-github-loadtest', contributor)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributor)
  }
}
