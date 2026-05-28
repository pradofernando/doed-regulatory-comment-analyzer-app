// ============================================================================
// DoED Regulatory Comments — Web Frontend (.NET 9 Blazor Server) IaC
// ============================================================================
// Provisions the Azure infrastructure required to host the Blazor web frontend
// that uses the Azure AI Foundry agents created by the existing function-app
// IaC (under ../../azure_func/infra/).
//
// What this deploys (resource group scope):
//   - Log Analytics workspace
//   - Application Insights (workspace-based)
//   - Key Vault (RBAC mode) with secrets for API key, Foundry endpoint, agent IDs
//   - App Service Plan (Linux B1)
//   - App Service (Linux, .NET 9) with system-assigned managed identity
//   - Role assignment: App Service MI -> Key Vault Secrets User (on the KV)
//   - App settings (separate child resource that depends on the role assignment
//     so the first KV-reference resolve doesn't race)
//
// What this does NOT deploy (intentionally):
//   - Azure AI Foundry project / agents (reuses existing ones)
//   - The Azure AI User role assignment on the Foundry project — that lives in
//     a different resource group/scope, so we emit the exact CLI command in an
//     output for the operator to run once after the first deployment.
// ============================================================================

@description('Three-to-fifteen-char base name used to compose all resource names.')
@minLength(3)
@maxLength(15)
param baseName string = 'doedweb'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('SKU for the Linux App Service Plan. B1 is sufficient for the workload; bump to P1v3 for more memory/CPU.')
@allowed([ 'B1', 'B2', 'B3', 'P0v3', 'P1v3', 'P2v3' ])
param appServicePlanSku string = 'B1'

@description('The Regulations.gov v4 API key. Stored as a Key Vault secret and consumed by the app via KV reference.')
@secure()
param regulationsGovApiKey string

@description('Foundry project endpoint, e.g. https://<name>.services.ai.azure.com/api/projects/<project>. Obtain from the Foundry portal: project -> "..." menu -> Project properties -> endpoint.')
param foundryProjectEndpoint string

@description('Foundry prompt-agent NAME for per-comment categorization (no asst_… ID — this is the agent label visible in the Foundry Agents list).')
param categorizationAgentName string = 'RegulatoryCommentCategorizationAgent'

@description('Foundry prompt-agent version for the categorization agent. Use "latest" to always pick the currently published version.')
param categorizationAgentVersion string = 'latest'

@description('Foundry prompt-agent NAME for theme grouping + collective analysis.')
param groupingAgentName string = 'RegulatoryCommentGroupingAgent'

@description('Foundry prompt-agent version for the grouping agent.')
param groupingAgentVersion string = 'latest'

@description('Foundry prompt-agent NAME for the follow-up Q&A chat. Optional — pass empty string to disable.')
param followUpAgentName string = ''

@description('Foundry prompt-agent version for the follow-up agent.')
param followUpAgentVersion string = 'latest'

@description('Foundry model deployment name backing the agents (informational only — the prompt agent picks its own model in the portal).')
param modelDeploymentName string = 'gpt-5.4'

@description('Default Regulations.gov document ID the UI pre-fills.')
param defaultDocumentId string = 'ED-2025-SCC-0481-0001'

@description('Default batch size sent to the grouping agent (1-20).')
@minValue(1)
@maxValue(20)
param batchSize int = 5

@description('Tags applied to every resource.')
param tags object = {
  workload: 'doed-regulatory-comments-web'
  managedBy: 'bicep'
}

var uniqueSuffix = uniqueString(subscription().subscriptionId, resourceGroup().id, baseName)
var logAnalyticsName = '${baseName}-law-${uniqueSuffix}'
var appInsightsName = '${baseName}-appi-${uniqueSuffix}'
var keyVaultName = take('${baseName}kv${uniqueSuffix}', 24)
var planName = '${baseName}-plan-${uniqueSuffix}'
var appName = '${baseName}-app-${uniqueSuffix}'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource secretRegsApiKey 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'RegulationsGov-ApiKey'
  properties: {
    value: regulationsGovApiKey
    contentType: 'text/plain'
  }
}

resource secretFoundryEndpoint 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'Foundry-ProjectEndpoint'
  properties: {
    value: foundryProjectEndpoint
    contentType: 'text/plain'
  }
}

// Note: agent NAMES + VERSIONS are not secrets (they're visible in the Foundry portal Agents list),
// so we pass them as plain app settings rather than Key Vault references.

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: appServicePlanSku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: union(tags, {
    'azd-service-name': 'web'
  })
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|9.0'
      alwaysOn: appServicePlanSku != 'B1'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// Key Vault Secrets User role (RBAC-mode KV) for the App Service managed identity.
// Fixed GUID for the "Key Vault Secrets User" built-in role.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvSecretsUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// App settings are a separate child resource so they're only written AFTER the MI has KV
// access — otherwise the first KV-reference resolve races the role assignment.
var kvRefRegsApiKey = '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretRegsApiKey.name})'
var kvRefFoundryEndpoint = '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretFoundryEndpoint.name})'

// SQLite persistence lives under /home which is the App Service Linux persistent mount.
var sqliteConnectionString = 'Data Source=/home/data/analysis.db'

resource webAppSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: webApp
  name: 'appsettings'
  properties: {
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    ConnectionStrings__AnalysisDb: sqliteConnectionString
    Api__BaseUrl: 'https://api.regulations.gov/v4'
    Api__ApiKey: kvRefRegsApiKey
    Api__DefaultDocumentId: defaultDocumentId
    Api__FoundryEndpoint: kvRefFoundryEndpoint
    Api__CategorizationAgentName: categorizationAgentName
    Api__CategorizationAgentVersion: categorizationAgentVersion
    Api__GroupingAgentName: groupingAgentName
    Api__GroupingAgentVersion: groupingAgentVersion
    Api__FollowUpAgentName: followUpAgentName
    Api__FollowUpAgentVersion: followUpAgentVersion
    Api__ModelDeploymentName: modelDeploymentName
    Api__BatchSize: string(batchSize)
  }
  dependsOn: [
    kvSecretsUserRoleAssignment
  ]
}

output webAppName string = webApp.name
output webAppHostname string = webApp.properties.defaultHostName
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppPrincipalId string = webApp.identity.principalId
output keyVaultName string = keyVault.name
output applicationInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Run this command once after the first deployment to grant the web app permission to call the Azure AI Foundry agents. Replace <FOUNDRY-PROJECT-RESOURCE-ID> with the full ARM ID of the existing Foundry project, e.g. /subscriptions/.../resourceGroups/rg-doed-comments/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<project>.')
output foundryRoleAssignmentCommand string = 'az role assignment create --assignee-object-id ${webApp.identity.principalId} --assignee-principal-type ServicePrincipal --role "Azure AI User" --scope <FOUNDRY-PROJECT-RESOURCE-ID>'
