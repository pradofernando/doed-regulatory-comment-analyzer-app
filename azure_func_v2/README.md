# DoED Regulatory Comments Azure Function

Automated Azure Function that runs daily at 3AM EST to fetch, process, and analyze public comments from Regulations.gov.

## Overview

This Azure Function automates the complete workflow:

1. **Fetch Comments** - Retrieves comments from Regulations.gov API
2. **Extract Attachments** - Downloads and extracts text from PDF/DOCX attachments
3. **Consolidate Data** - Combines inline text and attachment text into CSV format
4. **AI Categorization** - Uses Azure AI Agent to categorize each comment
5. **Group Analysis** - Analyzes and groups similar comments with AI Agent
6. **Store Results** - Saves frontend-compatible run records to Cosmos DB and large/raw artifacts to Blob Storage

Scheduled and manual requests use the same `analysis-requests` queue worker. The Function exposes Function-key-protected endpoints:

- `POST /api/analysis-runs` validates settings, creates a queued run, and returns `202 Accepted` with a `runId`.
- `GET /api/analysis-runs/{runId}` returns `queued`, `running`, `succeeded`, or `failed` status.

The queue worker atomically claims a run before execution, preventing duplicate queue deliveries from running concurrently. Categorization payloads above 512 KB are gzip-compressed into the private `analysis-run-payloads` container using the same format consumed by the frontend.

## Quick Start

For the full customer stack, including this Function workflow and the Blazor frontend, run the root deployment script:

```powershell
# from the repo root
.\deploy.ps1 -RegulationsGovApiKey "your-api-key" -DocumentId "YOUR_DOCUMENT_ID"
```

To deploy only the Function workflow, use this folder's deploy script:

```powershell
# 1. Deploy all Azure infrastructure, create AI Agents, and publish function code
cd azure_func_v2\infra
.\deploy.ps1 -RegulationsGovApiKey "your-api-key" -DocumentId "YOUR_DOCUMENT_ID"

# Optional: request Elastic Premium instead of the default Flex Consumption plan
.\deploy.ps1 -RegulationsGovApiKey "your-api-key" -DocumentId "YOUR_DOCUMENT_ID" -UsePremium
```

For a brand-new deployment into a new or empty resource group, the script generates a fresh stack suffix for globally unique resources. For incremental redeploys into an existing live stack, it reuses the existing suffix automatically.

The script handles everything:
- Provisions all Azure resources via Bicep
- Creates the Categorization, Grouping, and Validation agents in Foundry when the deployed endpoint is available
- Updates the Function App with the Foundry project endpoint plus agent name/version/model settings
- Publishes the Function App code

For the integrated frontend/Cosmos topology, use the root deployment script. It provisions or selects Cosmos, assigns both managed identities, creates payload storage, configures the Function endpoint/key on the server-side frontend, and enables Function-owned analysis. The Function-only script intentionally leaves manual analysis disabled unless Cosmos settings are supplied separately.

By default, the script deploys the Function App on Flex Consumption. If you pass `-UsePremium`, it opts into Elastic Premium and still falls back to Flex Consumption automatically if Premium validation fails.

If you want to override the deployed Foundry project endpoint, rerun it with a project endpoint value:

```powershell
.\deploy.ps1 -RegulationsGovApiKey "your-api-key" -DocumentId "YOUR_DOCUMENT_ID" -FoundryProjectEndpoint "https://your-resource.cognitiveservices.azure.com/api/projects/your-project"
```

When `-FoundryProjectEndpoint` is supplied, the script also:
- Uses the supplied endpoint instead of the Bicep output for agent creation and app settings

If your tenant disables key-based auth, the Foundry project endpoint is required for Entra-authenticated prompt-agent creation. The Azure ML `api.azureml.ms/agents/...` endpoint is not a valid replacement for that step.

For full step-by-step instructions see the [root README deployment guide](../README.md#deploying-to-azure).

## Schedule

- **Trigger**: Timer Trigger (CRON: `0 0 8 * * *`)
- **Schedule**: Daily at 3AM EST (8AM UTC)
- **Execution**: Automatic, no manual intervention required

## Infrastructure

All required Azure resources can be deployed using the included Bicep template located in `infra/`.

### Resources Deployed

| Resource | Purpose |
|----------|---------|
| **Azure AI Foundry Resource** | Hosts model deployments and Foundry projects |
| **Azure AI Foundry Project** | Project scope for agents and runtime access |
| **Azure Functions (Flex Consumption by default)** | Serverless compute for the pipeline; Elastic Premium is optional via `-UsePremium` |
| **Azure Storage Account** | Blob storage for outputs + Functions runtime |
| **Azure Key Vault** | Secure storage for API keys |
| **Application Insights** | Monitoring and telemetry |
| **Log Analytics Workspace** | Centralized logging |

### Infrastructure Files

```
azure_func_v2/
├── infra/
│   ├── main.bicep              # Bicep template (all resources)
│   ├── main.parameters.json    # Default parameters
│   └── deploy.ps1              # PowerShell deployment script
```

See [Deployment to Azure](#deployment-to-azure) for detailed deployment instructions.

## Prerequisites

### For Local Development

1. **Python 3.11+** - Runtime
2. **Azure CLI** - Authentication (`az login`)
3. **Azure Functions Core Tools** - Local testing (`func start`)
4. **Regulations.gov API Key** - Free from https://open.gsa.gov/api/regulationsgov/

### For Azure Deployment

All Azure resources are created by the Bicep template:

1. **Azure Subscription** - With permissions to create resources
2. **Azure CLI** - For deployment (`az --version`)
3. **Regulations.gov API Key** - Required parameter for deployment

### Python Dependencies

All dependencies are listed in `requirements.txt`:
- `azure-functions` - Azure Functions runtime
- `semantic-kernel` - Azure AI Agent integration
- `azure-identity` - Authentication
- `azure-storage-blob` - Blob storage
- `requests` - HTTP requests
- `azure-ai-documentintelligence` - Document/PDF extraction

## Configuration

### Environment Variables

Configure these settings in Azure Portal → Function App → Configuration or in `local.settings.json` for local development:

| Variable | Description | Example |
|----------|-------------|---------|
| `REGULATIONS_GOV_API_KEY` | API key from Regulations.gov | `abc123...` |
| `DOCUMENT_ID` | Document ID to fetch comments from | `ED-2025-SCC-0481-0001` |
| `DOCUMENTINTELLIGENCE_ENDPOINT` | Azure AI Document Intelligence endpoint | `https://your-doc-intel-resource.cognitiveservices.azure.com/` |
| `FOUNDRY_PROJECT_ENDPOINT` | Azure AI Foundry project endpoint | `https://your-resource.cognitiveservices.azure.com/api/projects/your-project` |
| `CATEGORIZATION_AGENT_NAME` | Foundry agent name for categorization | `RegulatoryCommentCategorizationAgent` |
| `CATEGORIZATION_AGENT_VERSION` | Version of the categorization agent | `1` |
| `CATEGORIZATION_AGENT_MODEL` | Model deployment for the categorization agent | `gpt-5.4` |
| `GROUPING_AGENT_NAME` | Foundry agent name for grouping | `RegulatoryCommentGroupingAgent` |
| `GROUPING_AGENT_VERSION` | Version of the grouping agent | `1` |
| `GROUPING_AGENT_MODEL` | Model deployment for the grouping agent | `gpt-5.4` |
| `VALIDATION_AGENT_NAME` | Optional Foundry agent name for validation | `doed-comment-agent3` |
| `VALIDATION_AGENT_VERSION` | Version of the validation agent | `1` |
| `VALIDATION_AGENT_MODEL` | Model deployment for the validation agent | `gpt-5.4` |
| `ALLOWED_MODEL_DEPLOYMENTS` | Comma-separated models accepted from manual analysis requests | `gpt-5.4,gpt-4o` |
| `BATCH_SIZE` | Number of comments per batch for grouping | `5` |
| `MAX_COMMENTS` | Limit number of comments to process (empty = all) | `10` or empty |
| `AZURE_STORAGE_ACCOUNT_NAME` | Storage account name for blob storage (uses managed identity) | `storeregulatory` |

### Local Settings (local.settings.json)

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "python",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "REGULATIONS_GOV_API_KEY": "your_api_key",
    "DOCUMENT_ID": "ED-2025-SCC-0481-0001",
    "DOCUMENTINTELLIGENCE_ENDPOINT": "https://your-doc-intel-resource.cognitiveservices.azure.com/",
    "FOUNDRY_PROJECT_ENDPOINT": "https://your-resource.cognitiveservices.azure.com/api/projects/your-project",
    "CATEGORIZATION_AGENT_NAME": "your-categorization-agent-name",
    "CATEGORIZATION_AGENT_VERSION": "1",
    "CATEGORIZATION_AGENT_MODEL": "gpt-5.4",
    "GROUPING_AGENT_NAME": "your-grouping-agent-name",
    "GROUPING_AGENT_VERSION": "1",
    "GROUPING_AGENT_MODEL": "gpt-5.4",
    "VALIDATION_AGENT_NAME": "your-validation-agent-name",
    "VALIDATION_AGENT_VERSION": "1",
    "VALIDATION_AGENT_MODEL": "gpt-5.4",
    "ALLOWED_MODEL_DEPLOYMENTS": "gpt-5.4,gpt-4o",
    "BATCH_SIZE": "5",
    "MAX_COMMENTS": "",
    "AZURE_STORAGE_ACCOUNT_NAME": "your_storage_account_name"
  }
}
```

The deployment script attempts to create or reuse `gpt-5.4` first. If that deployment is unavailable in the selected region or subscription, it configures the agents and Function App to use the Bicep-managed `gpt-4o` deployment instead.

**Note:** Copy `local.settings.json.example` to `local.settings.json` and update with your actual values.

## Local Development

### Setup

```powershell
# Navigate to function directory
cd azure_func_v2\doed_regulatory_comments_func

# Create virtual environment (optional but recommended)
python -m venv .venv
.\.venv\Scripts\Activate.ps1

# Install dependencies
pip install -r requirements.txt

# Install Azure Functions Core Tools (if not already installed)
# Download from: https://docs.microsoft.com/en-us/azure/azure-functions/functions-run-local
```

### Run Locally

```powershell
# Start the function locally
func start --verbose

# The function will show:
# - HTTP endpoint for manual triggering (if needed)
# - Next scheduled run time
# - Logs in real-time
```

## Deployment to Azure

### Bicep Template (Recommended)

Deploy all required Azure infrastructure using Infrastructure as Code (IaC).

**What gets deployed:**
| Resource | Purpose |
|----------|---------|
| Azure AI Foundry resource & project | AI Agents and model deployment for comment analysis |
| Azure Functions | Serverless compute for processing |
| Azure Storage Account | Blob storage for outputs |
| Azure Key Vault | Secure storage for API keys |
| Application Insights | Monitoring and logging |
| Log Analytics | Centralized log storage |

**Prerequisites:**
- Azure CLI installed (`az --version`)
- Logged in to Azure (`az login`)
- Bicep CLI installed (`az bicep install`)
- Regulations.gov API key (free from https://open.gsa.gov/api/regulationsgov/)

**Deploy using PowerShell (recommended — fully automated):**

```powershell
# Navigate to the infra directory
cd azure_func_v2\infra

# Run the deployment script with your API key and document ID
# Default hosting: Flex Consumption
.\deploy.ps1 `
  -RegulationsGovApiKey "your-api-key-here" `
  -DocumentId "YOUR_DOCUMENT_ID_FROM_REGULATIONS_GOV"

# Optional: request Elastic Premium hosting
.\deploy.ps1 `
  -RegulationsGovApiKey "your-api-key-here" `
  -DocumentId "YOUR_DOCUMENT_ID_FROM_REGULATIONS_GOV" `
  -UsePremium
```

This single command:
1. Creates or reuses the resource group `rg-doed-comments`
2. Deploys all Azure infrastructure via Bicep
3. Creates the Categorization, Grouping, and Validation agents in Azure AI Foundry via the Azure AI Projects SDK
4. Wires the Foundry endpoint and agent name/version/model settings into the Function App automatically
5. Publishes the Function App code via `func azure functionapp publish`

Hosting behavior:
1. Flex Consumption is the default.
2. `-UsePremium` makes Elastic Premium opt-in.
3. If Premium validation fails, the script retries on Flex Consumption automatically.

## Post-Deployment Knowledge Setup

After the infrastructure is deployed, there is one manual knowledge-grounding workflow that still happens in the Azure portals:

1. Upload the source documents you want the agents to use into a blob container in the deployed storage account.
2. Use Azure AI Search `Import data` plus the `RAG` flow to vectorize and index that blob container.
3. Add the resulting Azure AI Search index to the grouping and categorization agents through the Azure AI Search tool connection in Foundry.

The steps below follow Microsoft Learn guidance for the Azure portal wizard and the Foundry agent tool flow:

- https://learn.microsoft.com/en-us/azure/search/search-get-started-portal-import-vectors
- https://learn.microsoft.com/en-us/azure/foundry/how-to/connections-add?tabs=foundry-portal
- https://learn.microsoft.com/en-us/azure/foundry-classic/agents/how-to/tools-classic/azure-ai-search?tabs=keys%2Cazurecli

### 1. Upload documents to Blob Storage

Use the storage account created by the deployment and upload the documents you want grounded in agent responses.

Recommended approach:

1. In the Azure portal, open the deployed storage account.
2. Go to `Data storage` -> `Containers`.
3. Create a dedicated container for knowledge documents, such as `agent-knowledge`.
4. Upload the PDF, DOCX, or other supported files that should be indexed.

Use a separate container for source documents instead of the `regulatory-comments` output container. The function writes generated artifacts into `regulatory-comments`, and mixing those outputs with source documents makes indexing noisier.

Microsoft Learn reference:

- https://learn.microsoft.com/en-us/azure/search/search-get-started-portal-import-vectors

### 2. Create the Azure AI Search vector index from the blob container

Use the Azure AI Search portal wizard to create a searchable, vectorized index from the uploaded blob content.

Before running the wizard, make sure the following access requirements are met:

1. Your Azure AI Search service has role-based access enabled.
2. The search service has a system-assigned managed identity.
3. Your user has these Azure AI Search roles: `Search Service Contributor`, `Search Index Data Contributor`, and `Search Index Data Reader`.
4. The deployment now grants the search service managed identity the required `Storage Blob Data Reader` access on the storage account and `Cognitive Services OpenAI User` access on the Foundry/AIServices resource that hosts the embedding model.
5. Public access is enabled on the storage account, search service, and embedding-model resource while using the portal wizard. Microsoft documents this as a requirement for the portal-based wizard.

Then create the index:

1. In the Azure portal, open the deployed Azure AI Search service.
2. On the Overview page, select `Import data`.
3. Choose `Azure Blob Storage` as the data source.
4. Choose the `RAG` scenario.
5. On `Connect to your data`, select the subscription, storage account, and the blob container that contains the uploaded documents.
6. Select `Authenticate using managed identity` and leave the identity type as `System-assigned`.
7. On `Vectorize your text`, select the embedding model provider and model deployment you want to use for integrated vectorization.
8. Continue through the wizard, optionally review the generated fields on `Advanced settings`, and then create the objects.
9. Record the Azure AI Search index name created by the wizard.

Microsoft Learn references:

- https://learn.microsoft.com/en-us/azure/search/search-get-started-portal-import-vectors

### 3. Add the Azure AI Search service as a Foundry project connection

Before the agents can use the search index, add the Azure AI Search resource as a connection in the Foundry project.

1. Open Microsoft Foundry.
2. Make sure you can open the deployed project and that your account has a role that can add connections, such as `Foundry User`, `Foundry Owner`, or `Azure Contributor`.
3. In Foundry, select `Operate`.
4. Select `Admin`.
5. Select the deployed project.
6. Select `Add connection`.
7. Choose `Azure AI Search`.
8. Browse to the deployed Azure AI Search resource.
9. Choose the authentication method you want to use and add the connection.

Microsoft Learn references:

- https://learn.microsoft.com/en-us/azure/foundry/how-to/connections-add?tabs=foundry-portal#create-a-new-connection

### 4. Add the Azure AI Search tool to the categorization and grouping agents

Once the connection exists and the vector index has been created, add Azure AI Search through the agent's tool configuration and connect it to the search service and index.

Microsoft Learn references for this step:

- https://learn.microsoft.com/en-us/azure/foundry/how-to/connections-add?tabs=foundry-portal#create-a-new-connection
- https://learn.microsoft.com/en-us/azure/foundry-classic/agents/how-to/tools-classic/azure-ai-search?tabs=keys%2Cazurecli#add-the-tool-to-an-agent

Repeat these steps for both agents:

- `RegulatoryCommentCategorizationAgent`
- `RegulatoryCommentGroupingAgent`

Portal steps:

1. In Foundry, go to `Agents`.
2. Open the agent.
3. Select `Tools`.
4. Select `Add`.
5. Select `Browse all tools`.
6. Select `Azure AI Search`.
7. Provide or select the credentials and connection details for the deployed Azure AI Search service.
8. Select the vector index created by the Azure AI Search wizard.
9. Set a display name if the portal prompts for one.
10. Choose the search type. For most grounding scenarios, `Hybrid` or `Hybrid + semantic` is the most useful default.
11. Select `Connect`.

Microsoft Learn references:

- https://learn.microsoft.com/en-us/azure/foundry/how-to/connections-add?tabs=foundry-portal#create-a-new-connection
- https://learn.microsoft.com/en-us/azure/foundry-classic/agents/how-to/tools-classic/azure-ai-search?tabs=keys%2Cazurecli#add-the-tool-to-an-agent

### 5. Verify the end-to-end grounding path

After both agents are connected to the index:

1. Open the Azure AI Search index and verify documents were indexed.
2. Run a test query in Search Explorer if you want to confirm the indexed chunks are retrievable.
3. Open each Foundry agent and confirm the Azure AI Search tool appears under `Tools`.
4. Run a prompt against the categorization and grouping agents that should require content from the uploaded documents.

If the wizard or agent connection fails, check these first:

1. Public network access is enabled on the involved resources while using the portal wizard.
2. The search service managed identity has both `Storage Blob Data Reader` on the storage account and `Cognitive Services OpenAI User` on the Foundry/AIServices resource used for embeddings.
3. The search index includes both searchable text fields and searchable vector fields.
4. The Foundry project and Azure AI Search resource are in the same tenant.

After it completes, find the Function App name in the output and trigger a test run:

**Delete everything and redeploy cleanly:**

```powershell
# Navigate to the infra directory
cd azure_func_v2\infra

# Delete the entire deployment resource group and wait for completion
.\delete-all.ps1

# Recreate everything from Bicep and republish the Function App
.\deploy.ps1 `
  -RegulationsGovApiKey "your-api-key-here" `
  -DocumentId "YOUR_DOCUMENT_ID_FROM_REGULATIONS_GOV"

# Or opt into Elastic Premium for the rebuild attempt
.\deploy.ps1 `
  -RegulationsGovApiKey "your-api-key-here" `
  -DocumentId "YOUR_DOCUMENT_ID_FROM_REGULATIONS_GOV" `
  -UsePremium
```

## Monitoring

### View Logs

**Azure Portal:**
1. Go to Function App → Functions → regulatory_comments_daily
2. Click "Monitor" tab
3. View execution history, logs, and metrics

**VS Code:**
1. Install "Azure Functions" extension
2. Connect to your subscription
3. Right-click function → "Start Streaming Logs"

**Azure CLI:**
```powershell
az webapp log tail --name func-regulatory-comments --resource-group rg-regulatory-comments
```

### Application Insights (Recommended)

Enable Application Insights for advanced monitoring:

1. Go to Function App → Application Insights
2. Click "Turn on Application Insights"
3. Create or select an Application Insights resource
4. View detailed telemetry, performance, and failures

## Output Storage

All outputs are saved to Azure Blob Storage in the `regulatory-comments` container:

```
regulatory-comments/
├── 1_fetch/
│   ├── comments_raw_20260120_080000.json          # Raw API response
│   └── comments_extracted_20260120_080000.json    # Simplified comments
├── 2_consolidate/
│   └── comments_consolidated_20260120_080000.csv  # CSV with attachment text
└── 3_analysis/
    ├── categorizations_20260120_080000.json       # Individual categorizations
    ├── grouped_analysis_20260120_080000.json      # Final analysis (JSON - technical)
    └── grouped_analysis_20260120_080000.csv       # Final analysis (CSV - for end users)
```

### Accessing Output Files

**Azure Portal:**
1. Go to Storage Account → Containers → regulatory-comments
2. Browse and download files

**Azure Storage Explorer:**
1. Download: https://azure.microsoft.com/en-us/features/storage-explorer/
2. Connect to your subscription
3. Navigate to storage account → Blob Containers → regulatory-comments

**Azure CLI:**
```powershell
# List all blobs
az storage blob list --container-name regulatory-comments --account-name storegulatorycomments --output table

# Download a specific file
az storage blob download --container-name regulatory-comments --name "3_analysis/grouped_analysis_20260120_080000.json" --file "output.json" --account-name storegulatorycomments
```

## Workflow Details

### Phase 1: Fetch Comments
- Connects to Regulations.gov API
- Fetches all comments for specified document ID
- Handles pagination automatically
- Includes attachment metadata
- Saves raw JSON and extracted comments

### Phase 2: Consolidate with Attachments
- Downloads PDF and DOCX attachments
- Extracts text using PyPDF2 and python-docx
- Combines inline comment text with attachment text
- Creates CSV with all comment data
- Handles rate limiting and retries

### Phase 3: AI Categorization
- Uses Azure AI Agent (categorization_agent_id)
- Processes each comment individually
- Streams responses for real-time monitoring
- Saves categorizations as JSON
- Includes submission numbers and metadata

### Phase 4: Group Analysis
- Uses Azure AI Agent (grouping_agent_id)
- Processes categorizations in batches
- Maintains thread context across batches
- Generates collective analysis
- Identifies themes, patterns, and sentiment