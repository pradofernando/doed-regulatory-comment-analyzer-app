# Web frontend infra

Bicep deploys the Azure resources for the .NET 9 Blazor Server web frontend.
The existing Foundry agents (from `../../azure_func/infra/`) are reused — this
template does **not** create or modify them.

## Resources

| Resource | Why |
| --- | --- |
| Log Analytics workspace | Backing store for App Insights logs and metrics. |
| Application Insights (workspace-based) | Telemetry for the web app. |
| Key Vault (RBAC mode) | Stores the Regulations.gov API key, Foundry endpoint URL, and agent IDs. |
| App Service Plan (Linux B1) | Compute for the web app. Bump SKU in `main.bicepparam` if you need more memory or `alwaysOn`. |
| App Service (Linux, .NET 9) | Hosts the Blazor Server app. Uses a system-assigned managed identity. |
| Role assignment | App Service MI -> Key Vault Secrets User on the KV. |
| `Microsoft.Web/sites/config` (appsettings) | Wires the app to Key Vault references and App Insights. Separate child resource so it deploys **after** the role assignment (avoids first-start race). |

## Deploy with `azd`

```pwsh
cd dotnet_frontend
azd auth login
azd env new doedweb-dev
azd env set REGS_API_KEY <your-key>
azd env set FOUNDRY_PROJECT_ENDPOINT https://DOE-Demo.services.ai.azure.com/api/projects/DOE-Proj
azd env set FOUNDRY_CATEGORIZATION_AGENT_NAME RegulatoryCommentCategorizationAgent
azd env set FOUNDRY_CATEGORIZATION_AGENT_VERSION latest
azd env set FOUNDRY_GROUPING_AGENT_NAME RegulatoryCommentGroupingAgent
azd env set FOUNDRY_GROUPING_AGENT_VERSION latest
azd env set FOUNDRY_FOLLOWUP_AGENT_NAME RegulatoryCommentFollowUpAgent      # optional — leave unset to disable
azd env set FOUNDRY_FOLLOWUP_AGENT_VERSION latest
azd env set FOUNDRY_MODEL_DEPLOYMENT gpt-5.4                                 # informational only — prompt agent picks its own model
azd up
```

`azd up` will prompt for subscription and a region. The resource group is
created automatically (named `rg-<env-name>`).

## Deploy with raw `az`

```pwsh
cd dotnet_frontend/infra
az group create -n rg-doedweb -l eastus
az deployment group create `
  --resource-group rg-doedweb `
  --template-file main.bicep `
  --parameters main.bicepparam `
  --parameters regulationsGovApiKey=$env:REGS_API_KEY
```

## Post-deploy: grant Foundry access

The Foundry project lives in a different resource group, so its RBAC isn't
covered by this template. Run the command emitted in the deployment outputs
once after the first `azd up`:

```pwsh
azd env get-values | Select-String foundryRoleAssignmentCommand
# Then run the printed command, replacing <FOUNDRY-PROJECT-RESOURCE-ID> with
# the full ARM ID of the existing Foundry project.
```

## Persistence

Analysis run history is stored in SQLite at `/home/data/analysis.db`. The
`/home` mount on Linux App Service is persistent across restarts, scale events,
and slot swaps. If you need point-in-time backups, set up an App Service backup
or snapshot the file with `az webapp ssh`.

## Cost (approx.)

| SKU | ~Monthly cost |
| --- | --- |
| App Service Plan B1 (Linux) | ~$13 |
| Key Vault standard (no HSM) | < $1 |
| Application Insights + Log Analytics (low volume) | ~$2-5 |

Total: roughly **$15-20/month** at light usage. Scale to P1v3 if you need
`alwaysOn`, larger memory for big PDF attachment extractions, or higher request
concurrency (~$70/month).
