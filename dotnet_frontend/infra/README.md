# Web frontend infrastructure

Bicep deploys the Azure resources for the .NET 9 Blazor Server web frontend.
The existing Foundry agents (from `../../azure_func/infra/`) are reused — this
template does **not** create or modify them.

For the complete procedure, use the [Azure Deployment Runbook](../DEPLOYMENT.md).
It is the canonical guide for prerequisites, persistence choices, every environment variable,
preview/package/deploy commands, RBAC, verification, migration, backup, rollback, cleanup,
security, and troubleshooting. This README is the quick reference for files in `infra/`.

## Files

| File | Purpose |
| --- | --- |
| `main.bicep` | Resource definitions, application settings, role assignments, alerts, and outputs. |
| `main.bicepparam` | azd/environment-variable mapping and deployment defaults. |
| `main.json` | Generated ARM output when present; edit Bicep rather than this file. |

## Resources

| Resource | Why |
| --- | --- |
| Log Analytics workspace | Backing store for App Insights logs and metrics. |
| Application Insights (workspace-based) | Telemetry for the web app. |
| Key Vault (RBAC mode) | Stores the Regulations.gov API key and Foundry endpoint URL. Agent names/versions are non-secret app settings. |
| App Service Plan (Linux B1) | Compute for the web app. Bump SKU in `main.bicepparam` if you need more memory or `alwaysOn`. |
| App Service (Linux, .NET 9) | Hosts the Blazor Server app. Uses a system-assigned managed identity. |
| Role assignments | Key Vault Secrets User plus conditional OCR, Blob, and provisioned-Cosmos data roles. |
| `Microsoft.Web/sites/config` (appsettings) | Wires the app to Key Vault references and App Insights. Separate child resource so it deploys **after** the role assignment (avoids first-start race). |
| Blob Storage *(optional, enabled by `main.bicepparam`)* | Stores gzip-compressed oversized Cosmos AI payloads. Shared-key access and public blobs are disabled. It is unused by SQLite/Azure SQL. |
| Document Intelligence *(optional, enabled by `main.bicepparam`)* | OCR fallback for scanned PDFs using managed identity. |
| Cosmos DB *(optional)* | Serverless aggregate and compact summary containers with explicit indexing policies. |
| Azure Monitor alerts | HTTP 5xx and sustained response-time alerts; notifications are attached when `ALERT_EMAIL` is set. |

## Choose a region

`main.bicepparam` binds `location` to the `AZURE_LOCATION` environment variable and defaults to
**`eastus2`**. Fall back to **`centralus`** when the target subscription has no dedicated App
Service quota in East US 2.

```pwsh
azd env set AZURE_LOCATION eastus2      # preferred
azd env set AZURE_LOCATION centralus    # fallback when eastus2 has no B1 quota
```

Dedicated (B1 and above) App Service plans consume a per-region VM quota that is often `0` on new
or sandboxed subscriptions, even where Flex Consumption works. Always confirm before deploying:

```pwsh
az deployment group what-if --resource-group <rg> --parameters .\main.bicepparam
```

A shortfall surfaces as `InternalSubscriptionIsOverQuotaForSku` and names the region and the
required limit. Either request a quota increase for that region or switch `AZURE_LOCATION`. The
Foundry project may live in a different region; cross-region calls are supported and add only
minor latency.

## Required before the template compiles

`foundryProjectEndpoint` is declared `@minLength(1)` and has no built-in default, so there is no
way to deploy against someone else's Foundry project by accident. Export the value first:

```pwsh
$env:FOUNDRY_PROJECT_ENDPOINT = "https://<resource>.services.ai.azure.com/api/projects/<project>"
```

Leaving it unset makes `az bicep build-params` fail with `BCP333 ... too short to assign to a
target for which the minimum allowable length is 1`. That error means the endpoint is missing.

## Deployment profiles

| Provider | How resources are obtained | Scale note |
| --- | --- | --- |
| `Sqlite` | File at `/home/data/analysis.db` on App Service | One App Service instance only. |
| `AzureSql` | Existing database supplied by Entra connection string | Suitable for scale-out after database RBAC is configured. |
| `Cosmos` with `PROVISION_COSMOS_RESOURCES=false` | Existing account, database, aggregate container, and summary container | External data-plane RBAC required. |
| `Cosmos` with `PROVISION_COSMOS_RESOURCES=true` | New serverless account and containers from this template | Bicep creates indexes and app data-plane RBAC. |

See [Choose a deployment profile](../DEPLOYMENT.md#choose-a-deployment-profile) before setting values.

## Quick azd reference

This is only a command index. Complete the runbook before executing `azd up`.

```pwsh
cd dotnet_frontend
azd auth login --tenant-id <tenant-id>
azd env new doed-comments-<unique-suffix> --no-prompt
azd env set AZURE_SUBSCRIPTION_ID <subscription-id>
azd env set AZURE_LOCATION eastus
azd env set REGS_API_KEY <your-key>
azd env set FOUNDRY_PROJECT_ENDPOINT "https://<resource>.services.ai.azure.com/api/projects/<project>"
azd env set FOUNDRY_CATEGORIZATION_AGENT_NAME RegulatoryCommentCategorizationAgent
azd env set FOUNDRY_CATEGORIZATION_AGENT_VERSION latest
azd env set FOUNDRY_GROUPING_AGENT_NAME RegulatoryCommentGroupingAgent
azd env set FOUNDRY_GROUPING_AGENT_VERSION latest
azd env set FOUNDRY_FOLLOWUP_AGENT_NAME RegulatoryCommentFollowUpAgent      # optional — leave unset to disable
azd env set FOUNDRY_FOLLOWUP_AGENT_VERSION latest
azd env set FOUNDRY_MODEL_DEPLOYMENT gpt-5.4                                 # informational only — prompt agent picks its own model
azd env set ENABLE_ATTACHMENT_OCR true
azd env set ENABLE_PAYLOAD_STORAGE true
azd env set ALERT_EMAIL operations@example.gov                                # optional
azd provision --preview --no-prompt
azd package --no-prompt
azd up
```

Always set `FOUNDRY_PROJECT_ENDPOINT`; do not rely on the sample fallback in
`main.bicepparam`. Treat `azd env get-values` output as secret because it includes
the Regulations.gov key.

### Select the analysis database

The default remains SQLite on the persistent `/home` mount. To use an existing Azure SQL
database, set:

```pwsh
azd env set PERSISTENCE_PROVIDER AzureSql
azd env set ANALYSIS_DB_CONNECTION_STRING "Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"
```

Grant the web app managed identity a contained Azure SQL user with reader/writer access and
temporary DDL permission for first-start schema creation.

To use an existing Cosmos DB for NoSQL account, create an aggregate container partitioned by
`/id` and a summary container partitioned by `/documentIdNormalized`. Set the following values
and grant the web app identity **Cosmos DB Built-in Data Contributor**:

```pwsh
azd env set PERSISTENCE_PROVIDER Cosmos
azd env set COSMOS_ENDPOINT "https://<account>.documents.azure.com:443/"
azd env set COSMOS_DATABASE_NAME doed-regulatory-comments
azd env set COSMOS_CONTAINER_NAME analysis-runs
azd env set COSMOS_SUMMARY_CONTAINER_NAME analysis-run-summaries
```

To provision a serverless Cosmos account and both containers with this template instead:

```pwsh
azd env set PERSISTENCE_PROVIDER Cosmos
azd env set PROVISION_COSMOS_RESOURCES true
```

Optional deployment variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `ENABLE_ATTACHMENT_OCR` | `true` | Provision Document Intelligence and managed-identity RBAC. |
| `ENABLE_PAYLOAD_STORAGE` | `true` | Provision private Blob payload storage and RBAC. |
| `PROVISION_COSMOS_RESOURCES` | `false` | Provision serverless Cosmos instead of using an existing endpoint. |
| `FOUNDRY_INPUT_USD_PER_MILLION_TOKENS` | `0` | Input-token rate used for estimated-cost telemetry. |
| `FOUNDRY_OUTPUT_USD_PER_MILLION_TOKENS` | `0` | Output-token rate used for estimated-cost telemetry. |
| `ALERT_EMAIL` | empty | Add an email receiver to the Azure Monitor action group. |

Additional parameters such as `baseName`, `appServicePlanSku`, default document ID, batch size,
and payload threshold are declared in `main.bicep`; checked-in defaults are bound in
`main.bicepparam`. Review them before production deployment.

The resource group is created for the azd environment. Confirm tenant, subscription, region,
resource names, optional services, policy results, and role-assignment scopes in the provision
preview before deploying.

## Deploy with raw `az`

```pwsh
cd dotnet_frontend/infra
az group create -n rg-doedweb -l eastus
az deployment group create `
  --resource-group rg-doedweb `
  --parameters main.bicepparam
```

`main.bicepparam` references `main.bicep` and reads values such as `REGS_API_KEY` and
`FOUNDRY_PROJECT_ENDPOINT` from the deployment process environment. Raw Bicep deployment creates
infrastructure only; application publication remains a separate step.

## Post-deploy: grant Foundry access

The Foundry project lives in a different resource group, so its RBAC isn't
covered by this template. Run the command emitted in the deployment outputs
once after the first `azd up`:

```pwsh
azd env get-values | Select-String foundryRoleAssignmentCommand
# Then run the printed command, replacing <FOUNDRY-PROJECT-RESOURCE-ID> with
# the full ARM ID of the existing Foundry project.
```

Some Foundry resource generations expose the equivalent role as Foundry User. Confirm the role
available in the target tenant and that it grants prompt-agent Responses API operations. Existing
Cosmos and Azure SQL profiles require their external grants separately; see the runbook.

## Deployment outputs

| Output | Use |
| --- | --- |
| `webAppName`, `webAppUrl`, `webAppPrincipalId` | Application URL and identity/RBAC setup. |
| `livenessUrl`, `readinessUrl` | Post-deployment health checks. |
| `keyVaultName` | Secret rotation and diagnostics. |
| `documentIntelligenceEndpoint` | OCR configuration verification. |
| `payloadContainerUri` | Blob offload configuration verification. |
| `effectiveCosmosEndpoint` | Existing or provisioned Cosmos endpoint. |
| `foundryRoleAssignmentCommand` | Required external Foundry RBAC step. |

## Validate before deployment

From `dotnet_frontend/infra`:

```pwsh
dotnet test ..\..\dotnet_frontend.Tests\DoedRegulatoryComments.Web.Tests.csproj --configuration Release
az bicep build --file .\main.bicep
az bicep build-params --file .\main.bicepparam
Push-Location ..
azd provision --preview --no-prompt
azd package --no-prompt
Pop-Location
```

After deployment, both health endpoints must return HTTP 200 and a small analysis must complete,
persist, reopen from Library, and emit telemetry. See
[Verify the deployment](../DEPLOYMENT.md#step-10-verify-the-deployment).

## Persistence and operations

Analysis run history is stored in SQLite at `/home/data/analysis.db`. The
`/home` mount on Linux App Service is persistent across restarts, scale events,
and slot swaps. If you need point-in-time backups, set up an App Service backup
or snapshot the file with `az webapp ssh`.

SQLite is a one-instance profile. Azure SQL and Cosmos are the supported scale-out choices. See
the runbook for external RBAC, Cosmos summary migration, Blob offload, backup, provider migration,
rollback, secret rotation, and environment removal.

## Security boundary

The template uses managed identity for Azure dependencies, disables public Blob data and shared
storage keys, and disables local authentication for template-managed OCR/Cosmos services.
However, App Service user authentication and private endpoints are not configured. Before public
production use, protect the app and Settings page with Microsoft Entra authentication or network
restrictions and apply organizational private-network requirements.

## Cost (approx.)

| SKU | ~Monthly cost |
| --- | --- |
| App Service Plan B1 (Linux) | ~$13 |
| Key Vault standard (no HSM) | < $1 |
| Application Insights + Log Analytics (low volume) | ~$2-5 |
| Blob payload storage (low volume) | < $1 |
| Document Intelligence OCR | Usage-based; depends on pages analyzed |
| Serverless Cosmos DB *(when enabled)* | Usage-based; depends on RU consumption |

Base total: roughly **$15-20/month** at light usage, plus optional OCR and Cosmos usage. Scale to P1v3 if you need
`alwaysOn`, larger memory for big PDF attachment extractions, or higher request
concurrency (~$70/month).

These figures are directional only. Review `azd provision --preview`, Azure Pricing Calculator,
expected OCR pages, Cosmos request units, Foundry tokens, telemetry ingestion, and retention before
approving production cost.
