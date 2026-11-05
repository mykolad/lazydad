// Load Test Environment's deploy identity creates and deletes everything in the load test's group, and nothing outside.
// Named from the principal id: the assignment outlives lazydad-rg, where the identity lives (modules/subscriptionRole.bicep).
targetScope = 'resourceGroup'

param principalId string

var contributor = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource loadTestDeployerGroup 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, contributor)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributor)
  }
}
