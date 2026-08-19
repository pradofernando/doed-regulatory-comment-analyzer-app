using './main.bicep'

param baseName = 'doedweb'
param appServicePlanSku = 'B1'

// Preferred region is eastus2. If the subscription has no dedicated App Service (B1+)
// quota there, set AZURE_LOCATION=centralus. Confirm with `az deployment group what-if`
// before deploying — a missing-quota preflight error names the region explicitly.
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus2')

// === You MUST set these before running azd up / az deployment ===
// REGS_API_KEY environment variable will be passed by azd from .env / shell.
param regulationsGovApiKey = readEnvironmentVariable('REGS_API_KEY', '')
param foundryProjectEndpoint = readEnvironmentVariable('FOUNDRY_PROJECT_ENDPOINT', '')
param useKeyVaultReferences = readEnvironmentVariable('USE_KEY_VAULT_REFERENCES', 'true') == 'true'
param categorizationAgentName = readEnvironmentVariable('FOUNDRY_CATEGORIZATION_AGENT_NAME', 'RegulatoryCommentCategorizationAgent')
param categorizationAgentVersion = readEnvironmentVariable('FOUNDRY_CATEGORIZATION_AGENT_VERSION', 'latest')
param groupingAgentName = readEnvironmentVariable('FOUNDRY_GROUPING_AGENT_NAME', 'RegulatoryCommentGroupingAgent')
param groupingAgentVersion = readEnvironmentVariable('FOUNDRY_GROUPING_AGENT_VERSION', 'latest')
param followUpAgentName = readEnvironmentVariable('FOUNDRY_FOLLOWUP_AGENT_NAME', '')
param followUpAgentVersion = readEnvironmentVariable('FOUNDRY_FOLLOWUP_AGENT_VERSION', 'latest')
param modelDeploymentName = readEnvironmentVariable('FOUNDRY_MODEL_DEPLOYMENT', 'gpt-5.4')

param persistenceProvider = readEnvironmentVariable('PERSISTENCE_PROVIDER', 'Sqlite')
param analysisDbConnectionString = readEnvironmentVariable('ANALYSIS_DB_CONNECTION_STRING', '')
param cosmosEndpoint = readEnvironmentVariable('COSMOS_ENDPOINT', '')
param cosmosDatabaseName = readEnvironmentVariable('COSMOS_DATABASE_NAME', 'doed-regulatory-comments')
param cosmosContainerName = readEnvironmentVariable('COSMOS_CONTAINER_NAME', 'analysis-runs')
param cosmosSummaryContainerName = readEnvironmentVariable('COSMOS_SUMMARY_CONTAINER_NAME', 'analysis-run-summaries')
param cosmosCreateIfNotExists = readEnvironmentVariable('COSMOS_CREATE_IF_NOT_EXISTS', 'false') == 'true'
param provisionCosmosResources = readEnvironmentVariable('PROVISION_COSMOS_RESOURCES', 'false') == 'true'
param cosmosAccountName = readEnvironmentVariable('COSMOS_ACCOUNT_NAME', '')

param enablePayloadStorage = readEnvironmentVariable('ENABLE_PAYLOAD_STORAGE', 'true') == 'true'
param payloadOffloadThresholdBytes = 524288
param enableAttachmentOcr = readEnvironmentVariable('ENABLE_ATTACHMENT_OCR', 'true') == 'true'
param foundryInputUsdPerMillionTokens = readEnvironmentVariable('FOUNDRY_INPUT_USD_PER_MILLION_TOKENS', '0')
param foundryOutputUsdPerMillionTokens = readEnvironmentVariable('FOUNDRY_OUTPUT_USD_PER_MILLION_TOKENS', '0')
param alertEmail = readEnvironmentVariable('ALERT_EMAIL', '')

param defaultDocumentId = 'ED-2025-SCC-0481-0001'
param batchSize = 5
