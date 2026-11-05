// Who may read what in the vault: each app only its own environment's secrets and key, so a branch preview (running on
// staging) can't read production's tokens. The secrets must exist before their roles can (infra/README.md): Bicep never
// holds their values.
param vaultName string
param ownerPrincipalId string
// Each identity's resource id names its assignments: unlike its principal id, it's known before the deployment starts.
param productionId string
param productionPrincipalId string
param stagingAppId string
param stagingAppPrincipalId string
param loadTestAppId string
param loadTestAppPrincipalId string

var roles = {
  secretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  secretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  certificatesOfficer: 'a4417e6f-fecd-4de8-b567-7b0420556985'
  cryptoOfficer: '14b46e9e-c2b7-41b4-b07b-48a6ebf60603'
  cryptoServiceEncryptionUser: 'e147488a-f6f5-4113-8e2d-b22465e65bf6'
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

// The secret each identity reads. Production's identity also reads the origin certificate (lazydad-fyi-origin), which
// both prod environments bind to lazydad.fyi; the load-test app sends its telemetry with staging's token.
var secretReaders = [
  { secret: 'OtlpHeaders', key: productionId, principalId: productionPrincipalId }
  { secret: 'JevApiKey', key: productionId, principalId: productionPrincipalId }
  { secret: 'VoterKeyPepper', key: productionId, principalId: productionPrincipalId }
  { secret: 'GitHubClientSecret', key: productionId, principalId: productionPrincipalId }
  { secret: 'lazydad-fyi-origin', key: productionId, principalId: productionPrincipalId }
  { secret: 'OtlpHeadersStaging', key: stagingAppId, principalId: stagingAppPrincipalId }
  { secret: 'JevApiKeyStaging', key: stagingAppId, principalId: stagingAppPrincipalId }
  { secret: 'VoterKeyPepperStaging', key: stagingAppId, principalId: stagingAppPrincipalId }
  { secret: 'GitHubClientSecretStaging', key: stagingAppId, principalId: stagingAppPrincipalId }
  { secret: 'OtlpHeadersStaging', key: loadTestAppId, principalId: loadTestAppPrincipalId }
]

resource secrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = [
  for reader in secretReaders: {
    parent: vault
    name: reader.secret
  }
]

resource secretAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (reader, i) in secretReaders: {
    scope: secrets[i]
    name: guid(secrets[i].id, reader.key, roles.secretsUser)
    properties: {
      principalId: reader.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.secretsUser)
    }
  }
]

// The key that wraps each environment's sign-in key ring (DataProtection:KeyVaultKeyId).
var keyUsers = [
  { keyName: 'DataProtection', key: productionId, principalId: productionPrincipalId }
  { keyName: 'DataProtectionStaging', key: stagingAppId, principalId: stagingAppPrincipalId }
]

resource keys 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = [
  for user in keyUsers: {
    parent: vault
    name: user.keyName
  }
]

resource keyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (user, i) in keyUsers: {
    scope: keys[i]
    name: guid(keys[i].id, user.key, roles.cryptoServiceEncryptionUser)
    properties: {
      principalId: user.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cryptoServiceEncryptionUser)
    }
  }
]

// Owner manages the vault but can't read or write secrets, certificates or keys: those are data roles.
resource ownerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in [roles.secretsOfficer, roles.certificatesOfficer, roles.cryptoOfficer]: {
    scope: vault
    name: guid(vault.id, ownerPrincipalId, role)
    properties: {
      principalId: ownerPrincipalId
      principalType: 'User'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]
