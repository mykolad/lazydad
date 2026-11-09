// A role on the subscription. Named from the principal id, not the identity's resource id: the assignment outlives the
// resource group, so an identity re-created with it (a rebuilt lazydad-rg) gets an assignment of its own, rather than
// a deployment that fails trying to move the old one to a new principal.
targetScope = 'subscription'

param principalId string
param roleDefinitionId string

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, principalId, roleDefinitionId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: roleDefinitionId
  }
}
