// ============================================================================
// DoED Regulatory Comments — Web Frontend (.NET 9 Blazor Server) IaC
// ============================================================================
// Provisions the Azure infrastructure required to host the Blazor web frontend
// that uses the Azure AI Foundry agents created by the existing function-app
// IaC (under ../../azure_func_v2/infra/).
//
// What this deploys (resource group scope):
//   - Log Analytics workspace
//   - Application Insights (workspace-based)
//   - Key Vault (RBAC mode) with secrets for the API key and Foundry endpoint
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

@description('Azure region for all resources. Preferred: eastus2. Some subscriptions have no dedicated App Service (B1+) quota in eastus2 — use centralus as the fallback. Verify with: az deployment group what-if.')
param location string = resourceGroup().location

@description('SKU for the Linux App Service Plan. B1 is sufficient for the workload; bump to P1v3 for more memory/CPU.')
@allowed([ 'B1', 'B2', 'B3', 'P0v3', 'P1v3', 'P2v3' ])
param appServicePlanSku string = 'B1'

@description('The Regulations.gov v4 API key. Stored as a Key Vault secret and consumed by the app via KV reference.')
@secure()
param regulationsGovApiKey string

@description('Foundry project endpoint, e.g. https://<name>.services.ai.azure.com/api/projects/<project>. Obtain from the Foundry portal: project -> "..." menu -> Project properties -> endpoint.')
@minLength(1)
param foundryProjectEndpoint string

@description('Wire the API key and Foundry endpoint into app settings as Key Vault references. Set false when tenant policy forces publicNetworkAccess=Disabled on Key Vault and the app has no private endpoint to reach it; the values are then written directly to app settings, which App Service still encrypts at rest.')
param useKeyVaultReferences bool = true

@description('Foundry prompt-agent NAME for per-comment categorization (no asst_… ID — this is the agent label visible in the Foundry Agents list).')
param categorizationAgentName string = 'RegulatoryCommentCategorizationAgent'

@description('Foundry prompt-agent version for the categorization agent. Use "latest" to always pick the currently published version.')
param categorizationAgentVersion string = 'latest'

@description('Foundry prompt-agent NAME for theme grouping + collective analysis.')
param groupingAgentName string = 'RegulatoryCommentGroupingAgent'

@description('Foundry prompt-agent version for the grouping agent.')
param groupingAgentVersion string = 'latest'

@description('Foundry prompt-agent NAME for validating grouped analysis. Optional; pass an empty string to skip validation.')
param validationAgentName string = ''

@description('Foundry prompt-agent version for the validation agent.')
param validationAgentVersion string = 'latest'

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

@description('Delegate analysis execution to the Azure Function instead of running Foundry agents in the web app.')
param useFunctionAnalysisBackend bool = false

@description('Base HTTPS URL of the analysis Function App, for example https://func-example.azurewebsites.net/.')
param analysisFunctionBaseUrl string = ''

@secure()
@description('Function host or function key used by the server-side web app.')
param analysisFunctionKey string = ''

@description('Analysis history backend. Sqlite is local/persistent App Service storage; AzureSql and Cosmos point to existing Azure resources.')
@allowed([ 'Sqlite', 'AzureSql', 'Cosmos' ])
param persistenceProvider string = 'Sqlite'

@description('Azure SQL connection string. Prefer Authentication=Active Directory Default so the App Service managed identity is used.')
@secure()
param analysisDbConnectionString string = ''

@description('Cosmos DB account endpoint used with the App Service managed identity.')
param cosmosEndpoint string = ''

@description('Cosmos DB database containing analysis-run documents.')
param cosmosDatabaseName string = 'doed-regulatory-comments'

@description('Cosmos DB container for analysis runs. Its partition key must be /id.')
param cosmosContainerName string = 'analysis-runs'

@description('Create the Cosmos database/container on startup. Leave false when infrastructure provisions them.')
param cosmosCreateIfNotExists bool = false

@description('Provision a serverless Cosmos account, database, aggregate container, and summary container in this template.')
param provisionCosmosResources bool = false

@description('Optional name for a provisioned Cosmos account. A globally unique name is generated when blank.')
param cosmosAccountName string = ''

@description('Cosmos summary container partitioned by normalized document ID.')
param cosmosSummaryContainerName string = 'analysis-run-summaries'

@description('Provision private Blob Storage for oversized analysis payloads.')
param enablePayloadStorage bool = false

@description('Existing Blob container URI used for oversized analysis payloads. Takes precedence over frontend-provisioned payload storage.')
param analysisPayloadBlobContainerUri string = ''

@description('Offload raw categorization payloads to Blob Storage after this many UTF-8 bytes.')
@minValue(65536)
@maxValue(1572864)
param payloadOffloadThresholdBytes int = 524288

@description('Provision Azure AI Document Intelligence for scanned-PDF OCR.')
param enableAttachmentOcr bool = false

@description('Estimated Foundry input-token price in USD per million tokens. Used only for telemetry.')
param foundryInputUsdPerMillionTokens string = '0'

@description('Estimated Foundry output-token price in USD per million tokens. Used only for telemetry.')
param foundryOutputUsdPerMillionTokens string = '0'

@description('Optional operations email address for Azure Monitor alert notifications.')
param alertEmail string = ''

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
var payloadStorageName = take('${replace(baseName, '-', '')}pay${uniqueSuffix}', 24)
var payloadContainerName = 'analysis-run-payloads'
var documentIntelligenceName = take('${baseName}-ocr-${uniqueSuffix}', 64)
var provisionedCosmosAccountName = empty(cosmosAccountName)
  ? take('${replace(baseName, '-', '')}cosmos${uniqueSuffix}', 44)
  : cosmosAccountName

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
      healthCheckPath: '/health/ready'
    }
  }
}

resource payloadStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (enablePayloadStorage) {
  name: payloadStorageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource payloadBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (enablePayloadStorage) {
  parent: payloadStorage
  name: 'default'
  properties: {
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource payloadContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (enablePayloadStorage) {
  parent: payloadBlobService
  name: payloadContainerName
  properties: {
    publicAccess: 'None'
  }
}

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource payloadStorageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enablePayloadStorage) {
  scope: payloadStorage
  name: guid(payloadStorage.id, webApp.id, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (enableAttachmentOcr) {
  name: documentIntelligenceName
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: documentIntelligenceName
    disableLocalAuth: true
    // 'FormRecognizer' accounts reject networkAcls.bypass ('Trusted Services' is unsupported for this kind).
    networkAcls: {
      defaultAction: 'Allow'
      ipRules: []
      virtualNetworkRules: []
    }
    publicNetworkAccess: 'Enabled'
  }
}

var cognitiveServicesDataReaderRoleId = 'b59867f0-fa02-499b-be73-45a86b5b3e1c'

resource documentIntelligenceRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableAttachmentOcr) {
  scope: documentIntelligence
  name: guid(documentIntelligence.id, webApp.id, cognitiveServicesDataReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesDataReaderRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = if (provisionCosmosResources) {
  name: provisionedCosmosAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    locations: [
      {
        failoverPriority: 0
        isZoneRedundant: false
        locationName: location
      }
    ]
    publicNetworkAccess: 'Enabled'
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = if (provisionCosmosResources) {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    options: {}
    resource: {
      id: cosmosDatabaseName
    }
  }
}

resource cosmosRunContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (provisionCosmosResources) {
  parent: cosmosDatabase
  name: cosmosContainerName
  properties: {
    options: {}
    resource: {
      id: cosmosContainerName
      indexingPolicy: {
        automatic: true
        excludedPaths: [
          {
            path: '/categorizations/[]/rawResponse/?'
          }
          {
            path: '/categorizations/[]/parsedJson/?'
          }
          {
            path: '/followUpHistory/[]/text/?'
          }
        ]
        includedPaths: [
          {
            path: '/*'
          }
        ]
        indexingMode: 'consistent'
      }
      partitionKey: {
        kind: 'Hash'
        paths: [
          '/id'
        ]
        version: 2
      }
    }
  }
}

resource cosmosSummaryContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (provisionCosmosResources) {
  parent: cosmosDatabase
  name: cosmosSummaryContainerName
  properties: {
    options: {}
    resource: {
      id: cosmosSummaryContainerName
      indexingPolicy: {
        automatic: true
        compositeIndexes: [
          [
            {
              path: '/type'
              order: 'ascending'
            }
            {
              path: '/startedAt'
              order: 'descending'
            }
          ]
          [
            {
              path: '/type'
              order: 'ascending'
            }
            {
              path: '/succeeded'
              order: 'ascending'
            }
            {
              path: '/startedAt'
              order: 'descending'
            }
          ]
        ]
        excludedPaths: [
          {
            path: '/*'
          }
        ]
        includedPaths: [
          {
            path: '/type/?'
          }
          {
            path: '/documentIdNormalized/?'
          }
          {
            path: '/startedAt/?'
          }
          {
            path: '/succeeded/?'
          }
        ]
        indexingMode: 'consistent'
      }
      partitionKey: {
        kind: 'Hash'
        paths: [
          '/documentIdNormalized'
        ]
        version: 2
      }
    }
  }
}

resource cosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (provisionCosmosResources) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, webApp.id, 'cosmos-data-contributor')
  properties: {
    principalId: webApp.identity.principalId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: cosmosAccount.id
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

// SQLite persistence lives under /home, the App Service Linux persistent mount.
var sqliteConnectionString = 'Data Source=/home/data/analysis.db'
var effectiveCosmosEndpoint = provisionCosmosResources
  ? (cosmosAccount.?properties.?documentEndpoint ?? cosmosEndpoint)
  : cosmosEndpoint
var payloadContainerUri = !empty(analysisPayloadBlobContainerUri)
  ? analysisPayloadBlobContainerUri
  : (enablePayloadStorage
      ? 'https://${payloadStorage.name}.blob.${environment().suffixes.storage}/${payloadContainerName}'
      : '')
var documentIntelligenceEndpoint = enableAttachmentOcr
  ? (documentIntelligence.?properties.?endpoint ?? '')
  : ''

resource webAppSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: webApp
  name: 'appsettings'
  properties: {
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    WEBSITE_HEALTHCHECK_MAXPINGFAILURES: '5'
    Persistence__Provider: persistenceProvider
    ConnectionStrings__AnalysisDb: persistenceProvider == 'Sqlite' ? sqliteConnectionString : analysisDbConnectionString
    Persistence__Cosmos__Endpoint: effectiveCosmosEndpoint
    Persistence__Cosmos__DatabaseName: cosmosDatabaseName
    Persistence__Cosmos__ContainerName: cosmosContainerName
    Persistence__Cosmos__SummaryContainerName: cosmosSummaryContainerName
    Persistence__Cosmos__CreateIfNotExists: string(cosmosCreateIfNotExists)
    Persistence__Payloads__BlobContainerUri: payloadContainerUri
    Persistence__Payloads__ContainerName: payloadContainerName
    Persistence__Payloads__OffloadThresholdBytes: string(payloadOffloadThresholdBytes)
    Persistence__Payloads__CreateIfNotExists: 'false'
    AnalysisBackend__Enabled: string(useFunctionAnalysisBackend)
    AnalysisBackend__BaseUrl: analysisFunctionBaseUrl
    AnalysisBackend__FunctionKey: analysisFunctionKey
    AnalysisBackend__PollIntervalSeconds: '2'
    AnalysisBackend__TimeoutMinutes: '90'
    Attachments__AllowedHosts__0: 'downloads.regulations.gov'
    Attachments__MaxDownloadBytes: '26214400'
    Attachments__MaxRedirects: '3'
    Attachments__MaxArchiveEntries: '1000'
    Attachments__MaxArchiveUncompressedBytes: '104857600'
    Attachments__MaxExtractedTextCharacters: '500000'
    Attachments__MaxPdfPages: '100'
    Attachments__MaxOcrPages: '50'
    Attachments__MinPdfTextCharactersPerPage: '20'
    Attachments__OcrEndpoint: documentIntelligenceEndpoint
    Telemetry__FoundryCost__InputUsdPerMillionTokens: foundryInputUsdPerMillionTokens
    Telemetry__FoundryCost__OutputUsdPerMillionTokens: foundryOutputUsdPerMillionTokens
    Api__BaseUrl: 'https://api.regulations.gov/v4'
    Api__ApiKey: useKeyVaultReferences ? kvRefRegsApiKey : regulationsGovApiKey
    Api__DefaultDocumentId: defaultDocumentId
    Api__FoundryEndpoint: useKeyVaultReferences ? kvRefFoundryEndpoint : foundryProjectEndpoint
    Api__CategorizationAgentName: categorizationAgentName
    Api__CategorizationAgentVersion: categorizationAgentVersion
    Api__GroupingAgentName: groupingAgentName
    Api__GroupingAgentVersion: groupingAgentVersion
    Api__ValidationAgentName: validationAgentName
    Api__ValidationAgentVersion: validationAgentVersion
    Api__FollowUpAgentName: followUpAgentName
    Api__FollowUpAgentVersion: followUpAgentVersion
    Api__ModelDeploymentName: modelDeploymentName
    Api__BatchSize: string(batchSize)
  }
  dependsOn: [
    kvSecretsUserRoleAssignment
    payloadStorageRoleAssignment
    documentIntelligenceRoleAssignment
    cosmosDataContributor
    cosmosRunContainer
    cosmosSummaryContainer
  ]
}

resource alertActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: '${baseName}-operations'
  location: 'global'
  tags: tags
  properties: {
    emailReceivers: [
      {
        emailAddress: alertEmail
        name: 'Operations'
        useCommonAlertSchema: true
      }
    ]
    enabled: true
    groupShortName: take(replace(baseName, '-', ''), 12)
  }
}

var alertActions = empty(alertEmail) ? [] : [
  {
    actionGroupId: alertActionGroup.id
  }
]

resource http5xxAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${baseName}-http-5xx'
  location: 'global'
  tags: tags
  properties: {
    actions: alertActions
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          dimensions: []
          metricName: 'Http5xx'
          metricNamespace: 'Microsoft.Web/sites'
          name: 'Http5xx'
          operator: 'GreaterThan'
          skipMetricValidation: false
          threshold: 0
          timeAggregation: 'Total'
        }
      ]
    }
    description: 'The web app returned one or more HTTP 5xx responses in five minutes.'
    enabled: true
    evaluationFrequency: 'PT1M'
    scopes: [
      webApp.id
    ]
    severity: 2
    targetResourceRegion: location
    targetResourceType: 'Microsoft.Web/sites'
    windowSize: 'PT5M'
  }
}

resource responseTimeAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${baseName}-response-time'
  location: 'global'
  tags: tags
  properties: {
    actions: alertActions
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          dimensions: []
          metricName: 'AverageResponseTime'
          metricNamespace: 'Microsoft.Web/sites'
          name: 'AverageResponseTime'
          operator: 'GreaterThan'
          skipMetricValidation: false
          threshold: 5
          timeAggregation: 'Average'
        }
      ]
    }
    description: 'The web app average response time exceeded five seconds.'
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [
      webApp.id
    ]
    severity: 3
    targetResourceRegion: location
    targetResourceType: 'Microsoft.Web/sites'
    windowSize: 'PT15M'
  }
}

output webAppName string = webApp.name
output webAppHostname string = webApp.properties.defaultHostName
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppPrincipalId string = webApp.identity.principalId
output keyVaultName string = keyVault.name
output applicationInsightsConnectionString string = appInsights.properties.ConnectionString
output persistenceProvider string = persistenceProvider
output livenessUrl string = 'https://${webApp.properties.defaultHostName}/health/live'
output readinessUrl string = 'https://${webApp.properties.defaultHostName}/health/ready'
output documentIntelligenceEndpoint string = documentIntelligenceEndpoint
output payloadContainerUri string = payloadContainerUri
output effectiveCosmosEndpoint string = effectiveCosmosEndpoint
output effectiveCosmosAccountName string = provisionCosmosResources ? cosmosAccount.name : cosmosAccountName

@description('Run these commands once after the first deployment to grant the web app permission to call the Azure AI Foundry prompt agents. Replace <FOUNDRY-PROJECT-RESOURCE-ID> with the full ARM ID of the existing Foundry project, e.g. /subscriptions/.../resourceGroups/rg-doed-comments/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<project>. Older tenants expose these two roles as the single legacy role "Azure AI User".')
output foundryRoleAssignmentCommand string = 'az role assignment create --assignee-object-id ${webApp.identity.principalId} --assignee-principal-type ServicePrincipal --role "Foundry Project Runtime User" --scope <FOUNDRY-PROJECT-RESOURCE-ID>; az role assignment create --assignee-object-id ${webApp.identity.principalId} --assignee-principal-type ServicePrincipal --role "Foundry Agent Consumer" --scope <FOUNDRY-PROJECT-RESOURCE-ID>'

@description('For Cosmos persistence, grant this principal the Cosmos DB Built-in Data Contributor role on the target account. The container partition key must be /id.')
output cosmosPrincipalId string = persistenceProvider == 'Cosmos' ? webApp.identity.principalId : ''
