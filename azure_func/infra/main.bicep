// ============================================================================
// DoED Regulatory Comments Azure Function - Infrastructure as Code (Bicep)
// ============================================================================
// 
// This Bicep template deploys all Azure resources required for the 
// DoED Regulatory Comments processing Azure Function.
//
// Resources Deployed:
// 1. Azure AI Foundry Hub & Project (for AI Agents)
// 2. Azure OpenAI Service (GPT-4o model)
// 3. Azure Functions (Python runtime, Flex Consumption plan)
// 4. Azure Storage Account (for blob storage output)
// 5. Application Insights (for monitoring)
// 6. Key Vault (for secrets management)
// 7. Log Analytics Workspace (for logs)
// 8. Deployment Script (auto-creates AI Agents + wires agent IDs into Function App)
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
param baseName string = 'doed-comments2'

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
  'westus2'
  'westus3'
  'northcentralus'
  'southcentralus'
  'swedencentral'
  'uksouth'
  'francecentral'
  'australiaeast'
  // Note: 'westus' is intentionally excluded - Azure OpenAI is not available there
])
param location string = 'eastus'  // <-- CHANGE THIS TO DEPLOY TO A DIFFERENT REGION

// Model deployment is created manually - set this to match your deployment name
var modelDeploymentName = 'gpt-4.1'

@description('The Regulations.gov API key. Get one free at https://open.gsa.gov/api/regulationsgov/')
@secure()
param regulationsGovApiKey string

@description('Document ID to fetch comments from Regulations.gov')
param documentId string = 'ED-2025-SCC-0481-0001'

@description('Number of comments to process per batch for AI grouping analysis')
@minValue(1)
@maxValue(20)
param batchSize int = 5

// ============================================================================
// VARIABLES
// Computed values used throughout the template
// ============================================================================

// Generate unique suffix to ensure globally unique resource names
var uniqueSuffix = uniqueString(subscription().subscriptionId, resourceGroup().id)

// Resource names with unique suffixes where required for global uniqueness
// Storage account names must be 3-24 chars, lowercase alphanumeric only
#disable-next-line BCP334
var storageAccountName = take(replace('st${baseName}${uniqueSuffix}', '-', ''), 24)
var keyVaultName = take('kv-${baseName}-${uniqueSuffix}', 24)
var appInsightsName = 'appi-${baseName}'
var logAnalyticsName = 'law-${baseName}'
// Azure OpenAI custom subdomain must be <=24 chars
var openAiName = take('oai-${baseName}-${uniqueSuffix}', 24)
// Use first 8 chars of uniqueSuffix so hub/project names stay well within limits
var shortSuffix = take(uniqueSuffix, 8)
// Hub and Project names include a unique suffix to avoid global name conflicts
var aiHubName = take('hub-${baseName}-${shortSuffix}', 32)
var aiProjectName = take('proj-${baseName}-${shortSuffix}', 32)
var functionAppName = 'func-${baseName}-${uniqueSuffix}'
var flexPlanName = 'asp-${baseName}'

// User-assigned managed identity (pre-existing resource, kept for reference)
var deploymentScriptIdentityName = 'id-deploy-${baseName}'

// ============================================================================
// STORAGE ACCOUNT
// Required for:
// - Azure Functions runtime storage
// - Blob storage for comment outputs (raw JSON, CSV, analysis results)
// ============================================================================
#disable-next-line BCP334 // Storage name is guaranteed to be valid with minLength constraint on baseName
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
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
    
    // Soft delete protection
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
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
// AZURE OPENAI SERVICE
// Provides the GPT-5.2 model for AI-powered comment analysis
// ============================================================================
resource openAi 'Microsoft.CognitiveServices/accounts@2023-10-01-preview' = {
  name: openAiName
  location: location
  
  // S0 is the standard tier for Azure OpenAI
  sku: {
    name: 'S0'
  }
  
  kind: 'OpenAI'
  
  properties: {
    // Custom subdomain for the endpoint URL
    customSubDomainName: openAiName
    
    // Public access for simplicity; use private endpoints in production
    publicNetworkAccess: 'Enabled'
    
    // Disable local authentication to enforce Azure AD
    disableLocalAuth: false
  }
}

// NOTE: Model deployment is created manually after infrastructure is provisioned.
// Deploy 'gpt-4.1' (version 2025-04-14) via Azure AI Foundry portal or:
//   az cognitiveservices account deployment create \
//     --name <openAiResourceName> --resource-group rg-doed-comments \
//     --deployment-name gpt-4.1 --model-name gpt-4.1 \
//     --model-version 2025-04-14 --model-format OpenAI \
//     --sku-capacity 10 --sku-name Standard


// ============================================================================
// AZURE AI FOUNDRY HUB
// Container for AI projects; manages shared resources and connections
// ============================================================================
resource aiHub 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: aiHubName
  location: location
  
  // Hub type workspace
  kind: 'Hub'
  
  // System-assigned managed identity for accessing other Azure resources
  identity: {
    type: 'SystemAssigned'
  }
  
  properties: {
    friendlyName: 'DoED Comment Analysis Hub'
    description: 'AI Hub for regulatory comment analysis using Azure AI Agents'
    
    // Link to shared resources
    storageAccount: storageAccount.id
    keyVault: keyVault.id
    applicationInsights: appInsights.id
    
    // Public access for simplicity
    publicNetworkAccess: 'Enabled'
  }
}

// ============================================================================
// AZURE AI FOUNDRY PROJECT
// Workspace where AI Agents are created and managed
// ============================================================================
resource aiProject 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: aiProjectName
  location: location
  
  // Project type workspace (child of Hub)
  kind: 'Project'
  
  identity: {
    type: 'SystemAssigned'
  }
  
  properties: {
    friendlyName: 'DoED Comment Analysis Project'
    description: 'Project for analyzing regulatory comments with AI Agents'
    
    // Link to parent Hub
    hubResourceId: aiHub.id
    
    publicNetworkAccess: 'Enabled'
  }
}

// ============================================================================
// AZURE OPENAI CONNECTION TO AI HUB
// Connects the OpenAI service to the AI Foundry Hub
// This allows AI Agents to use the GPT-5.2 model
// ============================================================================
resource openAiConnection 'Microsoft.MachineLearningServices/workspaces/connections@2024-04-01' = {
  parent: aiHub
  name: 'aoai-connection'
  
  properties: {
    // Connection type
    category: 'AzureOpenAI'
    
    // Target endpoint
    target: openAi.properties.endpoint
    
    // Authentication using API key
    authType: 'ApiKey'
    credentials: {
      key: openAi.listKeys().key1
    }
    
    // Metadata for the connection
    metadata: {
      ApiVersion: '2023-10-01-preview'
      ApiType: 'azure'
      ResourceId: openAi.id
    }
  }
}

// ============================================================================
// APP SERVICE PLAN (Flex Consumption - FC1)
// Flex Consumption is the modern serverless Functions hosting model (GA 2024).
// Unlike the original Consumption (Y1) or Elastic Premium plans, FC1 does NOT
// consume VM quota, making it available on subscriptions where Y1/EP1 are blocked.
// Billed per-execution like Y1, but with faster cold starts and more config options.
// ============================================================================
resource flexPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: flexPlanName
  location: location
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true  // Required for Linux
  }
}

// ============================================================================
// AZURE FUNCTION APP (Flex Consumption)
// The main application that runs the regulatory comments processing.
// Flex Consumption uses functionAppConfig instead of linuxFxVersion to specify
// runtime, and stores deployment packages in blob storage via managed identity.
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
    serverFarmId: flexPlan.id

    // HTTPS only for security
    httpsOnly: true

    // Flex Consumption runtime and deployment configuration
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          // Deployment packages are stored here; accessed via managed identity
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
      // Note: linuxFxVersion is not set for Flex Consumption - runtime defined in functionAppConfig above.

      // Allow Azure Portal to invoke/test the function from the portal UI
      cors: {
        allowedOrigins: [
          'https://ms.portal.azure.com'
          'https://portal.azure.com'
        ]
        supportCredentials: false
      }

      // Function app settings
      appSettings: [
        // Note: FUNCTIONS_WORKER_RUNTIME and FUNCTIONS_EXTENSION_VERSION are
        // reserved settings on Flex Consumption - they are set automatically
        // from functionAppConfig.runtime above and must NOT appear here.

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
        
        // Azure AI Foundry configuration. Endpoint is overridden after deploy via `az functionapp config appsettings set` once the
        // real Foundry project endpoint is known (Foundry portal -> project -> Project properties -> endpoint).
        {
          name: 'AZURE_AI_AGENT_ENDPOINT'
          value: ''
        }
        {
          name: 'AZURE_AI_AGENT_SUBSCRIPTION_ID'
          value: subscription().subscriptionId
        }
        {
          name: 'AZURE_AI_AGENT_RESOURCE_GROUP_NAME'
          value: resourceGroup().name
        }
        {
          name: 'AZURE_AI_AGENT_PROJECT_NAME'
          value: aiProject.name
        }
        {
          name: 'AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME'
          value: modelDeploymentName
        }
        
        // AI Agent IDs - automatically populated by the deployment script below.
        // Set to PENDING here; the script overwrites them with real IDs.
        {
          name: 'CATEGORIZATION_AGENT_ID'
          value: 'PENDING_AGENT_CREATION'
        }
        {
          name: 'GROUPING_AGENT_ID'
          value: 'PENDING_AGENT_CREATION'
        }
      ]
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

// Grant Function App access to write blobs to Storage Account
resource functionStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Blob Data Contributor')
  scope: storageAccount
  properties: {
    // Storage Blob Data Contributor role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant Function App Storage Blob Data Owner on Storage Account
// Required by Flex Consumption for deployment package container access
resource functionStorageOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Blob Data Owner')
  scope: storageAccount
  properties: {
    // Storage Blob Data Owner role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant Function App access to Storage Queues
// Required by the Functions runtime when using managed identity for AzureWebJobsStorage
resource functionStorageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Queue Data Contributor')
  scope: storageAccount
  properties: {
    // Storage Queue Data Contributor role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant Function App access to Storage Tables
// Required by the Functions runtime when using managed identity for AzureWebJobsStorage
resource functionStorageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'Storage Table Data Contributor')
  scope: storageAccount
  properties: {
    // Storage Table Data Contributor role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant Function App access to Azure OpenAI
resource functionOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, functionApp.id, 'Cognitive Services OpenAI User')
  scope: openAi
  properties: {
    // Cognitive Services OpenAI User role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant AI Hub access to Storage Account
resource hubStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, aiHub.id, 'Storage Blob Data Contributor')
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: aiHub.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant AI Hub access to Key Vault
resource hubKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, aiHub.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: aiHub.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant AI Hub access to Azure OpenAI
resource hubOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, aiHub.id, 'Cognitive Services OpenAI Contributor')
  scope: openAi
  properties: {
    // Contributor role for creating deployments
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a001fd3d-188f-4b5d-821b-7da978bf7442')
    principalId: aiHub.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant Function App the Azure AI Developer role on the AI Project
// Required for the function to call the Azure AI Agents API (AzureAIAgent in Semantic Kernel)
resource functionAiDeveloperRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, functionApp.id, 'Azure AI Developer')
  scope: aiProject
  properties: {
    // Azure AI Developer role - grants access to AI Agents, deployments, and project resources
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '64702f94-c441-49e6-a78b-ef80e0188fee')
    principalId: functionApp.identity.principalId
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

@description('Azure AI Project endpoint (already set as AZURE_AI_AGENT_ENDPOINT on the function app)')
output aiProjectEndpoint string = 'https://${location}.api.azureml.ms/agents/v1.0/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroup().name}/providers/Microsoft.MachineLearningServices/workspaces/${aiProject.name}'

@description('Azure OpenAI endpoint')
output openAiEndpoint string = openAi.properties.endpoint

@description('Model deployment name (created manually)')
output modelDeploymentName string = modelDeploymentName

@description('Application Insights instrumentation key')
output appInsightsKey string = appInsights.properties.InstrumentationKey

@description('Key Vault name')
output keyVaultName string = keyVault.name

@description('AI Hub name - use this to access AI Foundry portal')
output aiHubName string = aiHub.name

@description('AI Project name')
output aiProjectName string = aiProject.name

@description('Resource Group name')
output resourceGroupName string = resourceGroup().name

@description('Subscription ID')
output subscriptionId string = subscription().subscriptionId
