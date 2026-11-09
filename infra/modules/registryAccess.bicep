// Who may do what in the registry. It's in the "RBAC Registry + ABAC Repository Permissions" mode: the registry-wide
// AcrPull and AcrPush don't work, and a repository role covers every repository unless a condition limits it to one.
// So a branch preview, running as staging's deploy identity, can write lazydad-preview only, never production's lazydad.
param registryName string
param ownerPrincipalId string
// Each identity's resource id names its assignments: unlike its principal id, it's known before the deployment starts.
param pullIdentityId string
param pullPrincipalId string
param purgeTaskPrincipalId string
param productionDeployerId string
param productionDeployerPrincipalId string
param stagingDeployerId string
param stagingDeployerPrincipalId string
param loadTestDeployerId string
param loadTestDeployerPrincipalId string

var roles = {
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  repositoryReader: 'b93aa761-3e63-49ed-ac28-beffa264f7ac'
  repositoryWriter: '2a1e307c-b015-4ebd-883e-5b7698a07328'
  repositoryContributor: '2efddaa5-3f1f-4df3-97df-af3f13818f4c'
  repositoryCatalogLister: 'bfdb9389-c9a5-478a-bb2f-ba9ca092c3c7'
}

var read = ['content/read', 'metadata/read']
var write = concat(read, ['content/write', 'metadata/write'])
var delete = concat(write, ['content/delete', 'metadata/delete'])

// The condition that limits a role's repository actions to one repository: any other action passes, and these only for
// requests to that repository.
func onlyIn(repository string, actions string[]) string =>
  '((${join(map(actions, action => '!(ActionMatches{\'Microsoft.ContainerRegistry/registries/repositories/${action}\'})'), ' AND ')}) OR (@Request[Microsoft.ContainerRegistry/registries/repositories:name] StringEqualsIgnoreCase \'${repository}\'))'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

var grants = [
  // The apps pull from either repository: staging runs lazydad-preview's images, production lazydad's.
  {
    key: pullIdentityId
    principalId: pullPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryReader
    condition: ''
  }
  // The weekly purge deletes old images in both repositories.
  {
    key: 'purge-old-images'
    principalId: purgeTaskPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryContributor
    condition: ''
  }
  {
    key: 'purge-old-images'
    principalId: purgeTaskPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryCatalogLister
    condition: ''
  }
  // Every build lands in lazydad-preview, as staging's deploy identity.
  {
    key: stagingDeployerId
    principalId: stagingDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryWriter
    condition: onlyIn('lazydad-preview', write)
  }
  // Production copies the tested image from lazydad-preview into lazydad, which only it can write.
  {
    key: productionDeployerId
    principalId: productionDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryWriter
    condition: onlyIn('lazydad', write)
  }
  {
    key: productionDeployerId
    principalId: productionDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryReader
    condition: onlyIn('lazydad-preview', read)
  }
  // Load tests push their build to lazydad-loadtest and delete it afterwards.
  {
    key: loadTestDeployerId
    principalId: loadTestDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.repositoryContributor
    condition: onlyIn('lazydad-loadtest', delete)
  }
  // `az acr login` looks the registry up in Azure Resource Manager; in this mode Reader grants nothing on the images.
  {
    key: productionDeployerId
    principalId: productionDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.reader
    condition: ''
  }
  {
    key: stagingDeployerId
    principalId: stagingDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.reader
    condition: ''
  }
  {
    key: loadTestDeployerId
    principalId: loadTestDeployerPrincipalId
    principalType: 'ServicePrincipal'
    role: roles.reader
    condition: ''
  }
  // The owner: Owner manages the registry but can't read or push images. Browsing them in the portal needs these.
  {
    key: ownerPrincipalId
    principalId: ownerPrincipalId
    principalType: 'User'
    role: roles.repositoryContributor
    condition: ''
  }
  {
    key: ownerPrincipalId
    principalId: ownerPrincipalId
    principalType: 'User'
    role: roles.repositoryCatalogLister
    condition: ''
  }
]

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for grant in grants: {
    scope: registry
    name: guid(registry.id, grant.key, grant.role)
    properties: union(
      {
        principalId: grant.principalId
        principalType: grant.principalType
        roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', grant.role)
      },
      empty(grant.condition)
        ? {}
        : {
            condition: grant.condition
            conditionVersion: '2.0'
          }
    )
  }
]
