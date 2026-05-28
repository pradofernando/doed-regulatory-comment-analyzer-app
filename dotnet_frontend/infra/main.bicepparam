using './main.bicep'

param baseName = 'doedweb'
param appServicePlanSku = 'B1'

// === You MUST set these before running azd up / az deployment ===
// REGS_API_KEY environment variable will be passed by azd from .env / shell.
param regulationsGovApiKey = readEnvironmentVariable('REGS_API_KEY', '')
param foundryProjectEndpoint = readEnvironmentVariable('FOUNDRY_PROJECT_ENDPOINT', 'https://DOE-Demo.services.ai.azure.com/api/projects/DOE-Proj')
param categorizationAgentName = readEnvironmentVariable('FOUNDRY_CATEGORIZATION_AGENT_NAME', 'RegulatoryCommentCategorizationAgent')
param categorizationAgentVersion = readEnvironmentVariable('FOUNDRY_CATEGORIZATION_AGENT_VERSION', 'latest')
param groupingAgentName = readEnvironmentVariable('FOUNDRY_GROUPING_AGENT_NAME', 'RegulatoryCommentGroupingAgent')
param groupingAgentVersion = readEnvironmentVariable('FOUNDRY_GROUPING_AGENT_VERSION', 'latest')
param followUpAgentName = readEnvironmentVariable('FOUNDRY_FOLLOWUP_AGENT_NAME', '')
param followUpAgentVersion = readEnvironmentVariable('FOUNDRY_FOLLOWUP_AGENT_VERSION', 'latest')
param modelDeploymentName = readEnvironmentVariable('FOUNDRY_MODEL_DEPLOYMENT', 'gpt-5.4')

param defaultDocumentId = 'ED-2025-SCC-0481-0001'
param batchSize = 5
