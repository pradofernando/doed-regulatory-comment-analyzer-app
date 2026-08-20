// ============================================================================
// DoED Regulatory Comments Azure Function - Infrastructure as Code (Bicep)
// ============================================================================
// 
// This Bicep template deploys all Azure resources required for the 
// DoED Regulatory Comments processing Azure Function.
//
// Resources Deployed:
// 1. Azure AI Foundry resource & project (for AI Agents)
// 2. Foundry-hosted GPT model deployment
// 3. Azure Functions (Python runtime, Flex Consumption plan)
// 4. Azure Storage Account (for blob storage output)
// 5. Application Insights (for monitoring)
// 6. Key Vault (for secrets management)
// 7. Log Analytics Workspace (for logs)
// 8. Deployment script support resources
//
// Usage:
//   az deployment group create \
//     --resource-group rg-doed-comments \
//     --template-file main.bicep \
//     --parameters main.parameters.json
//
// ============================================================================

// ============================================================================
// PARAMETERS
// These values can be customized when deploying the template
// ============================================================================

@description('Base name for all resources. Resources will be named with this prefix.')
@minLength(3)
@maxLength(15)
param baseName string = 'doed-comments'

// ============================================================================
// DEFAULT REGION: East US
// To change the deployment region, modify the default value below.
// All 9 Azure resources will be deployed to this region.
// Ensure the region supports Azure OpenAI (see @allowed list for valid options).
// ============================================================================
@description('Azure region for all resources. Must support Azure OpenAI and Azure AI Foundry.')
@allowed([
  'eastus'           // Recommended: broadest Azure OpenAI model availability
  'eastus2'
  'westus'
  'westus2'
  'westus3'
  'northcentralus'
  'southcentralus'
  'swedencentral'
  'uksouth'
  'francecentral'
  'australiaeast'
])
param location string = 'eastus'  // <-- CHANGE THIS TO DEPLOY TO A DIFFERENT REGION

@description('Default GPT-4o model deployment capacity in thousands of tokens per minute.')
@minValue(1)
@maxValue(100)
param gptCapacity int = 10

@description('text-embedding-3-large deployment capacity in thousands of tokens per minute.')
@minValue(1)
@maxValue(100)
param embeddingCapacity int = 10

@description('The Regulations.gov API key. Get one free at https://open.gsa.gov/api/regulationsgov/')
@secure()
param regulationsGovApiKey string

@description('Document ID to fetch comments from Regulations.gov')
param documentId string = 'ED-2025-SCC-0481-0001'

@description('Number of comments to process per batch for AI grouping analysis')
@minValue(1)
@maxValue(20)
param batchSize int = 5

@description('Object ID of the signed-in deployer. Used to grant temporary data-plane access for agent creation automation.')
param deployerPrincipalId string = ''

@description('Principal type of the deployer object id. Expected values are User or ServicePrincipal.')
@allowed([
  ''
  'User'
  'ServicePrincipal'
])
param deployerPrincipalType string = ''

@description('Unique stack suffix used for globally unique resource names. Leave empty to fall back to a stable resource-group-derived suffix.')
param deploymentSuffix string = ''

@description('Restore the Foundry resource if it exists in soft-delete state from a previous deployment.')
param restoreFoundry bool = false

@description('Function hosting mode. Premium uses Elastic Premium EP1. FlexConsumption uses FC1.')
@allowed([
  'Premium'
  'FlexConsumption'
])
param hostingMode string = 'FlexConsumption'

// ============================================================================
// VARIABLES
// Computed values used throughout the template
// ============================================================================

// Generate unique suffix to ensure globally unique resource names
var uniqueSuffix = empty(deploymentSuffix) ? uniqueString(resourceGroup().id) : take(toLower(replace(replace(deploymentSuffix, '-', ''), '_', '')), 13)

// Resource names with unique suffixes where required for global uniqueness
// Storage account names must be 3-24 chars, lowercase alphanumeric only
#disable-next-line BCP334
var storageAccountName = take(replace('st${baseName}${uniqueSuffix}', '-', ''), 24)
var keyVaultName = take('kv-${baseName}-${uniqueSuffix}', 24)
var appInsightsName = 'appi-${baseName}'
var logAnalyticsName = 'law-${baseName}'
var aiFoundryName = 'aif-${baseName}-${uniqueSuffix}'
var documentIntelligenceName = 'docint-${baseName}-${uniqueSuffix}'
var searchServiceName = 'srch-${baseName}-${uniqueSuffix}'
var aiProjectName = 'aiproj-${baseName}'
var foundryProjectEndpoint = 'https://${aiFoundryName}.cognitiveservices.azure.com/api/projects/${aiProjectName}'
var usePremiumHosting = hostingMode == 'Premium'
var hostingModeNamePart = usePremiumHosting ? 'prem' : ''
var functionAppName = usePremiumHosting ? 'func-${baseName}-${hostingModeNamePart}-${uniqueSuffix}' : 'func-${baseName}-${uniqueSuffix}'
var hostingPlanName = usePremiumHosting ? 'asp-${baseName}-${hostingModeNamePart}' : 'asp-${baseName}'
var hostingPlanSkuName = usePremiumHosting ? 'EP1' : 'FC1'
var hostingPlanSkuTier = usePremiumHosting ? 'ElasticPremium' : 'FlexConsumption'
var premiumRuntimeAppSettings = usePremiumHosting ? [
  {
    name: 'FUNCTIONS_WORKER_RUNTIME'
    value: 'python'
  }
  {
    name: 'FUNCTIONS_EXTENSION_VERSION'
    value: '~4'
  }
  {
    name: 'WEBSITE_RUN_FROM_PACKAGE'
    value: '1'
  }
  {
    name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
    value: 'true'
  }
  {
    name: 'ENABLE_ORYX_BUILD'
    value: 'true'
  }
] : []

// User-assigned managed identity (pre-existing resource, kept for reference)
var deploymentScriptIdentityName = 'id-deploy-${baseName}'

// ============================================================================
// STORAGE ACCOUNT
// Required for:
// - Azure Functions runtime storage
// - Blob storage for comment outputs (raw JSON, CSV, analysis results)
// ============================================================================
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  #disable-next-line BCP334 // Storage name is guaranteed to be valid with minLength constraint on baseName
  name: storageAccountName
  location: location
  
  // Standard_LRS is cost-effective for non-critical data
  // Use Standard_GRS for geo-redundancy if required
  sku: {
    name: 'Standard_LRS'
  }
  
  kind: 'StorageV2'
  
  properties: {
    // Security: Require TLS 1.2 minimum
    minimumTlsVersion: 'TLS1_2'
    
    // Security: Only allow HTTPS connections
    supportsHttpsTrafficOnly: true
    
    // Security: Disable anonymous blob access
    allowBlobPublicAccess: false
    
    // Enable hierarchical namespace for better organization (optional)
    isHnsEnabled: false
    
    // Access tier for blob storage
    accessTier: 'Hot'
  }
}

// Create the blob container for regulatory comments output
resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource commentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobServices
  name: 'regulatory-comments'
  properties: {
    // Private access - no anonymous access allowed
    publicAccess: 'None'
  }
}

// Blob container used by Flex Consumption for storing deployment packages
resource releasesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobServices
  name: 'function-releases'
  properties: {
    publicAccess: 'None'
  }
}

// ============================================================================
// KEY VAULT
// Securely stores secrets like API keys
// The Function App accesses secrets via managed identity
// ============================================================================
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    
    // Required for Azure to access the vault
    tenantId: subscription().tenantId
    
    // Use RBAC for access control (more secure than access policies)
    enableRbacAuthorization: true
    
    // Soft delete protection (required for production)
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    
    // Purge protection prevents permanent deletion during soft delete period
    enablePurgeProtection: true
  }
}

// Store the Regulations.gov API key in Key Vault
resource regulationsApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'regulations-gov-api-key'
  properties: {
    value: regulationsGovApiKey
  }
}

// ============================================================================
// LOG ANALYTICS WORKSPACE
// Central logging for all resources
// Required by Application Insights
// ============================================================================
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  
  properties: {
    sku: {
      // Pay-per-GB is most cost-effective for small workloads
      name: 'PerGB2018'
    }
    // Retain logs for 30 days (adjust as needed)
    retentionInDays: 30
  }
}

// ============================================================================
// APPLICATION INSIGHTS
// Monitoring and telemetry for the Azure Function
// Provides execution logs, performance metrics, and failure tracking
// ============================================================================
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  
  properties: {
    Application_Type: 'web'
    // Link to Log Analytics for log storage
    WorkspaceResourceId: logAnalytics.id
    // Enable sampling to reduce costs (can be adjusted)
    SamplingPercentage: 100
  }
}

// ============================================================================
// AZURE AI DOCUMENT INTELLIGENCE
// Used by the Function App to extract text from PDF and DOCX attachments.
// ============================================================================
resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2023-10-01-preview' = {
  name: documentIntelligenceName
  location: location

  sku: {
    name: 'S0'
  }

  kind: 'FormRecognizer'

  properties: {
    customSubDomainName: documentIntelligenceName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

// ============================================================================
// AZURE AI SEARCH
// Customer-owned Azure AI Search service used for agent knowledge retrieval.
// ============================================================================
#disable-next-line BCP334
resource searchService 'Microsoft.Search/searchServices@2022-09-01' = {
  name: searchServiceName
  location: location

  identity: {
    type: 'SystemAssigned'
  }

  sku: {
    name: 'standard'
  }

  properties: {
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
    disableLocalAuth: false
    hostingMode: 'default'
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    replicaCount: 1
  }
}

// ============================================================================
// AZURE AI FOUNDRY RESOURCE
// Hosts model deployments and Foundry projects using the same pattern as deploy/
// ============================================================================
resource aiFoundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: aiFoundryName
  location: location

  sku: {
    name: 'S0'
  }

  kind: 'AIServices'

  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    allowProjectManagement: true
    customSubDomainName: aiFoundryName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
    restore: restoreFoundry
  }
}

// Foundry project used by the Function App at runtime
resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  name: aiProjectName
  parent: aiFoundry
  location: location

  identity: {
    type: 'SystemAssigned'
  }

  properties: {}
}

// Deploy the default GPT-4o chat model used by Foundry agents
resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: aiFoundry
  name: 'gpt-4o'

  sku: {
    name: 'Standard'
    capacity: gptCapacity
  }

  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-11-20'
    }
  }
}

// Deploy the embedding model used for vectorization and search workflows
resource embeddingModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: aiFoundry
  name: 'text-embedding-3-large'

  dependsOn: [
    modelDeployment
  ]

  sku: {
    name: 'GlobalStandard'
    capacity: embeddingCapacity
  }

  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
  }
}

// ============================================================================
// APP SERVICE PLAN
// Premium uses EP1; Flex Consumption uses FC1 for subscriptions without
// available App Service VM quota.
// ============================================================================
resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: hostingPlanName
  location: location
  sku: {
    name: hostingPlanSkuName
    tier: hostingPlanSkuTier
    size: usePremiumHosting ? 'EP1' : null
    capacity: usePremiumHosting ? 1 : null
  }
  kind: 'functionapp'
  properties: {
    reserved: true  // Required for Linux
  }
}

// ============================================================================
// AZURE FUNCTION APP
// The main application that runs the regulatory comments processing.
// Premium uses the classic App Service runtime model; Flex uses
// functionAppConfig for runtime/deployment configuration.
// ============================================================================
resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'

  // Enable system-assigned managed identity for secure access to other resources
  identity: {
    type: 'SystemAssigned'
  }

  properties: {
    serverFarmId: hostingPlan.id

    // HTTPS only for security
    httpsOnly: true

    functionAppConfig: usePremiumHosting ? null : {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}function-releases'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 10
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'python'
        version: '3.11'
      }
    }

    siteConfig: {
      linuxFxVersion: usePremiumHosting ? 'Python|3.11' : null
      alwaysOn: usePremiumHosting ? true : null

      // Allow Azure Portal to invoke/test the function from the portal UI
      cors: {
        allowedOrigins: [
          'https://ms.portal.azure.com'
          'https://portal.azure.com'
        ]
        supportCredentials: false
      }

      // Function app settings
      appSettings: concat(premiumRuntimeAppSettings, [
        // Storage connection for Functions runtime - uses managed identity (no keys stored)
        // Requires Storage Blob Data Owner, Queue Data Contributor, and Table Data Contributor
        // roles on the storage account (assigned below in ROLE ASSIGNMENTS section)
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storageAccount.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        
        // Application Insights for monitoring
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsights.properties.InstrumentationKey
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        
        // ========================================
        // Application-specific settings
        // ========================================
        
        // Regulations.gov API key (reference from Key Vault)
        {
          name: 'REGULATIONS_GOV_API_KEY'
          value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=regulations-gov-api-key)'
        }
        
        // Document ID to fetch comments from
        {
          name: 'DOCUMENT_ID'
          value: documentId
        }
        
        // Batch size for AI grouping analysis
        {
          name: 'BATCH_SIZE'
          value: string(batchSize)
        }
        
        // Optional: maximum number of comments to process per run (empty = no limit)
        {
          name: 'MAX_COMMENTS'
          value: ''
        }
        
        // Storage account name for blob output (uses managed identity)
        {
          name: 'AZURE_STORAGE_ACCOUNT_NAME'
          value: storageAccount.name
        }
        {
          name: 'DOCUMENTINTELLIGENCE_ENDPOINT'
          value: documentIntelligence.properties.endpoint
        }
        
        // Azure AI Foundry configuration
        {
          name: 'FOUNDRY_PROJECT_ENDPOINT'
          value: foundryProjectEndpoint
        }
        {
          name: 'CATEGORIZATION_AGENT_NAME'
          value: 'RegulatoryCommentCategorizationAgent'
        }
        {
          name: 'CATEGORIZATION_AGENT_VERSION'
          value: '1'
        }
        {
          name: 'CATEGORIZATION_AGENT_MODEL'
          value: modelDeployment.name
        }
        {
          name: 'GROUPING_AGENT_NAME'
          value: 'RegulatoryCommentGroupingAgent'
        }
        {
          name: 'GROUPING_AGENT_VERSION'
          value: '1'
        }
        {
          name: 'GROUPING_AGENT_MODEL'
          value: modelDeployment.name
        }
        {
          name: 'VALIDATION_AGENT_NAME'
          value: ''
        }
        {
          name: 'VALIDATION_AGENT_VERSION'
          value: '1'
        }
        {
          name: 'VALIDATION_AGENT_MODEL'
          value: modelDeployment.name
        }
      ])
    }
  }
}

// ============================================================================
// ROLE ASSIGNMENTS
// Grant necessary permissions using Azure RBAC
// ============================================================================

// Grant Function App access to read secrets from Key Vault
resource functionKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    // Key Vault Secrets User role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(deployerPrincipalType)) {
  name: guid(keyVault.id, deployerPrincipalId, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

// Grant Function App full blob data-plane access required by Flex deployment storage.
// Storage Blob Data Owner is required for the managed-identity deployment package path.
resource functionStorageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Blob Data Owner')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerStorageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(deployerPrincipalType)) {
  name: guid(storageAccount.id, deployerPrincipalId, 'Storage Blob Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

resource searchStorageBlobReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, searchService.id, 'Storage Blob Data Reader')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: searchService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant Function App access to Storage Queues
// Required by the Functions runtime when using managed identity for AzureWebJobsStorage
resource functionStorageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Queue Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerStorageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(deployerPrincipalType)) {
  name: guid(storageAccount.id, deployerPrincipalId, 'Storage Queue Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

// Grant Function App access to Storage Tables
// Required by the Functions runtime when using managed identity for AzureWebJobsStorage
resource functionStorageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Table Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerStorageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(deployerPrincipalType)) {
  name: guid(storageAccount.id, deployerPrincipalId, 'Storage Table Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

// Grant Function App access to the deployed data-plane resources without keys.
resource functionDocumentIntelligenceUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentIntelligence.id, functionApp.id, 'Cognitive Services User')
  scope: documentIntelligence
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerDocumentIntelligenceUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(deployerPrincipalType)) {
  name: guid(documentIntelligence.id, deployerPrincipalId, 'Cognitive Services User')
  scope: documentIntelligence
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

resource functionFoundryOpenAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, functionApp.id, 'Cognitive Services OpenAI User')
  scope: aiFoundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerFoundryOpenAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && !empty(deployerPrincipalType)) {
  name: guid(aiFoundry.id, deployerPrincipalId, 'Cognitive Services OpenAI User')
  scope: aiFoundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

resource searchFoundryOpenAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, searchService.id, 'Cognitive Services OpenAI User')
  scope: aiFoundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: searchService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionFoundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, functionApp.id, 'Foundry User')
  scope: aiFoundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerFoundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(aiFoundry.id, deployerPrincipalId, 'Foundry User')
  scope: aiFoundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

// The Foundry project identity needs Foundry access to operate against the parent resource.
resource projectFoundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, aiProject.id, 'Foundry User')
  scope: aiFoundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Allow the Foundry project identity to resolve and query the deployed search service.
resource projectSearchIndexReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiProject.id, 'Search Index Data Reader')
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource projectSearchServiceContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiProject.id, 'Search Service Contributor')
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// USER-ASSIGNED MANAGED IDENTITY (pre-existing resource)
// Originally created for deployment scripts. Kept in Bicep to avoid drift.
// Agent creation now happens locally via deploy.ps1 (az rest calls).
// ============================================================================
resource deploymentScriptIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: deploymentScriptIdentityName
  location: location
}

// ============================================================================
// OUTPUTS
// Values needed for configuration after deployment
// ============================================================================

@description('Function App name for deployment')
output functionAppName string = functionApp.name

@description('Function App URL')
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'

@description('Storage Account name for blob access')
output storageAccountName string = storageAccount.name

@description('Azure AI Foundry project endpoint (already set as AZURE_AI_AGENT_ENDPOINT on the function app)')
output aiProjectEndpoint string = foundryProjectEndpoint

@description('Azure AI Foundry resource endpoint')
output foundryResourceEndpoint string = aiFoundry.properties.endpoint

@description('Azure AI Document Intelligence endpoint')
output documentIntelligenceEndpoint string = documentIntelligence.properties.endpoint

@description('Azure AI Search service name')
output searchServiceName string = searchService.name

@description('Azure AI Search endpoint')
output searchServiceEndpoint string = 'https://${searchService.name}.search.windows.net'

@description('Model deployment name')
output modelDeploymentName string = modelDeployment.name

@description('Embedding model deployment name')
output embeddingModelDeploymentName string = embeddingModelDeployment.name

@description('Application Insights instrumentation key')
output appInsightsKey string = appInsights.properties.InstrumentationKey

@description('Key Vault name')
output keyVaultName string = keyVault.name

@description('Azure AI Foundry resource name')
output aiFoundryName string = aiFoundry.name

@description('AI Project name')
output aiProjectName string = aiProject.name

@description('Resource Group name')
output resourceGroupName string = resourceGroup().name

@description('Subscription ID')
output subscriptionId string = subscription().subscriptionId
