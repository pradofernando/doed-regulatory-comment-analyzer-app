# DoED Regulatory Comment Analyzer

A web app that pulls **public comments** from [Regulations.gov](https://www.regulations.gov) and uses **Azure AI Foundry agents** to read them, categorize each one, and produce a single combined analysis. Built so a non-technical user can run an entire policy-comment analysis from a browser.

> **Status:** the **.NET 9 Blazor web app** is the user experience and the **`azure_func_v2/` Function App** owns scheduled and manual analysis execution. The root Python scripts are retained as a standalone legacy pipeline.

---

## What does it actually do?

1. **Fetch** every public comment from a Regulations.gov document or docket (by ID, e.g. `ED-2025-SCC-0481-0001`).
2. **Download attachments safely** using HTTPS and host allowlists, bounded streaming, MIME/signature checks, OpenXML expansion limits, and PDF page/text limits. Scanned PDFs can use optional Document Intelligence OCR.
3. **Categorize each comment individually** — a Foundry prompt agent reads it and emits a structured JSON record (themes, sentiment, key points).
4. **Group all categorizations** into a single collective report — a second Foundry prompt agent finds common themes across all submissions and produces the final narrative.
5. **Chat with the analysis** — an optional third Foundry agent lets you ask follow-up questions about what was found.
6. **Save / re-open / export** — runs can be stored in SQLite, Azure SQL, or Cosmos DB. You can page through the Library, re-open old runs, download categorizations, or export the final report.

---

## Production hardening now included

The web app includes the following operational and scalability work:

- **Attachment boundary controls:** HTTPS-only URLs, DNS-host allowlisting, explicit redirect validation, standard-port enforcement, IP-literal rejection, streamed byte limits, MIME/file-signature matching, ZIP expansion limits, PDF page limits, and extracted-text limits.
- **Scanned-PDF OCR:** optional Azure AI Document Intelligence `prebuilt-read` fallback with a separate OCR page cap and managed-identity authentication.
- **Scalable Cosmos Library:** continuation-token pagination, exact normalized document IDs, schema-v2 documents, a compact summary container, leased legacy-summary backfill, optimistic concurrency, and RU telemetry.
- **Large payload offload:** large Cosmos categorization payloads are gzip-compressed into private Blob Storage while Cosmos retains bounded metadata and a blob reference.
- **Operational telemetry:** OpenTelemetry/Application Insights signals for jobs, phase duration, Foundry latency/tokens/estimated cost/retries/429s, attachment outcomes, OCR, and Cosmos request units. Prompt and comment content are excluded from custom telemetry.
- **Health and alerts:** `/health/live`, persistence-aware `/health/ready`, App Service health probing, and Azure Monitor alerts for HTTP 5xx and sustained response latency.
- **Quality gates:** unit, repository, mocked HTTP contract, job cancellation, host integration, deterministic AI contract evaluation, NuGet vulnerability auditing, and Bicep/Bicep-parameter compilation in GitHub Actions.

The latest validated local suite contains 91 .NET tests plus Function contract tests. The exact count can grow as coverage is added; CI is the source of truth.

---

## Repo layout

```text
.
├── dotnet_frontend/                  ← The web app (primary). Blazor Server, .NET 9.
│   ├── Components/Pages/             ← UI: Comments, Analysis, Settings, etc.
│   ├── Services/                     ← Regulations.gov client + Foundry analysis service.
│   ├── Data/                         ← SQLite store for past analysis runs.
│   ├── infra/                        ← Bicep + bicepparam for deploying to Azure App Service.
│   ├── App_Data/                     ← (Local-only, gitignored) settings + SQLite DB.
│   ├── README.md                     ← Frontend architecture and configuration reference.
│   └── DEPLOYMENT.md                 ← Complete Azure deployment and operations runbook.
│
├── azure_func_v2/                    ← Current Agent Framework Azure Functions workflow and IaC.
│   ├── doed_regulatory_comments_func/
│   └── infra/                        ← Bicep, prompt-agent creation, and deployment scripts.
│
├── fetch_regulations_comments.py     ← Standalone Python scripts (the original pipeline).
├── consolidate_comments_to_csv.py
├── process_csv_rows.py
├── format_grouped_analysis.py
├── download_attachments.py
│
├── presentation_slides/              ← Helper to build a PPTX summary.
├── sample_data/                      ← Example comments JSON for testing.
├── requirements.txt                  ← Python deps for the scripts + Function App.
├── .env.example                      ← Copy to `.env` and fill in (gitignored).
└── .gitignore
```

Most UI work lives in `dotnet_frontend/`; production analysis execution lives in `azure_func_v2/`.

---

## Quick start: run the web app locally

### Prerequisites

- **.NET 9 SDK** — install from <https://dotnet.microsoft.com/download/dotnet/9.0>.
- **Azure CLI** — install from <https://learn.microsoft.com/cli/azure/install-azure-cli>, then run `az login` once (the app uses your Azure identity to call Foundry — no API keys needed for AI).
- **A Regulations.gov API key** — free, takes ~1 minute: <https://open.gsa.gov/api/regulationsgov/>.
- **An Azure AI Foundry project** with three prompt agents created (see [next section](#set-up-your-foundry-agents)).

### Run it

```powershell
# from the repo root
cd dotnet_frontend
dotnet run --launch-profile https
```

Open <https://localhost:7018>. (If your browser complains about the dev certificate, run `dotnet dev-certs https --trust` once.)

### First-time setup inside the app

1. Click **Settings** in the left nav.
2. Paste your **Regulations.gov API key**.
3. Paste your **Foundry project endpoint** (looks like `https://<resource>.services.ai.azure.com/api/projects/<project>` — find it in the Foundry portal under *project → "…" menu → Project properties*).
4. Enter the **Name** and **Version** of each of your three agents (Categorization, Grouping, Follow-up). Versions can be `latest`.
5. Click **Save**.
6. Go to **Comments**, paste a document ID (e.g. `ED-2025-SCC-0481-0001`), click **Fetch comments**, then **Run AI analysis**.

That's it. Results land on the **Analysis** page, and you can chat with the follow-up agent at the bottom.

---

## Set up your Foundry agents

The app calls the new **Foundry "prompt agents"** through the Responses API. You need three agents in your Foundry project:

| Agent | Suggested name | Purpose |
| --- | --- | --- |
| **Categorization** | `RegulatoryCommentCategorizationAgent` | Reads one comment at a time, emits a JSON categorization (themes, sentiment, recommendations). |
| **Grouping** | `RegulatoryCommentGroupingAgent` | Reads batches of categorizations and produces a single combined analysis. Multi-turn — the app threads batches together. |
| **Validation** *(optional)* | `RegulatoryCommentValidationAgent` | Reviews grouped output and applies minimal corrections when coverage or grouping is invalid. |
| **Follow-up** *(optional)* | `RegulatoryCommentFollowUpAgent` | Stateful chat about the completed analysis. Leave blank to disable the chat panel. |

Each agent needs an instruction/prompt template appropriate for its job. The Function v2 deployment creates categorization, grouping, and validation agents from [`azure_func_v2/AGENT_PROMPTS.md`](azure_func_v2/AGENT_PROMPTS.md). All agents can share the same underlying model deployment.

---

## Deploy to Azure

The web app deploys to **Azure App Service (Linux)** with a Bicep template that also creates
Application Insights and Key Vault. Optional switches provision managed-identity Document
Intelligence OCR, private Blob payload storage, and serverless Cosmos DB with paged summaries.

Four persistence profiles are supported:

| Profile | Intended use |
| --- | --- |
| SQLite on App Service `/home` | Evaluation and single-instance deployments. |
| Existing Azure SQL | Relational production storage and App Service scale-out. |
| Existing Cosmos DB | Reuse an externally managed Cosmos account. |
| Template-managed serverless Cosmos DB | Provision a new account, aggregate container, summary container, and data-plane RBAC. |

The root deployment orchestrates Function v2 and the frontend. It now defaults to a shared, template-managed serverless Cosmos account, enables Function-owned analysis, configures Function-key authentication, assigns managed-identity access, and uses Function Blob Storage for oversized payloads:

```powershell
.\deploy.ps1 `
	-RegulationsGovApiKey $env:REGS_API_KEY
```

To retain existing Cosmos results, pass the existing endpoint and account name. Add `-CosmosResourceGroupName` when it is not the frontend resource group:

```powershell
.\deploy.ps1 `
    -RegulationsGovApiKey $env:REGS_API_KEY `
    -CosmosEndpoint "https://<account>.documents.azure.com:443/" `
    -CosmosAccountName "<account>" `
    -CosmosResourceGroupName "<resource-group>"
```

The Function accepts manual submissions at `POST /api/analysis-runs`, queues them with scheduled runs, persists status/results to Cosmos, and offloads categorization payloads above 512 KB to Blob Storage. The frontend receives the Function URL/key as server-side settings and reads completed records through its existing Cosmos repository.

The canonical deployment guide is [dotnet_frontend/DEPLOYMENT.md](dotnet_frontend/DEPLOYMENT.md). It includes:

- Tooling, Azure permissions, tenant login, and provider registration.
- Every required and optional azd environment variable.
- Separate instructions for SQLite, Azure SQL, existing Cosmos, and provisioned Cosmos.
- OCR, Blob payload storage, pricing telemetry, and alert configuration.
- Bicep/azd preview, packaging, deployment, and raw Azure CLI alternatives.
- Foundry, Cosmos, Storage, Document Intelligence, Key Vault, and SQL RBAC.
- Health checks, smoke tests, Application Insights verification, and metrics.
- Data migration, backup, rollback, secret rotation, cleanup, and troubleshooting.
- A production-readiness checklist.

Minimal shape only; do not skip the full runbook:

```powershell
cd dotnet_frontend
azd auth login --tenant-id <tenant-id>
azd env new doed-comments-<unique-suffix> --no-prompt
azd env set AZURE_SUBSCRIPTION_ID <subscription-id>
azd env set AZURE_LOCATION eastus
azd env set REGS_API_KEY <your-regulations-gov-key>
azd env set FOUNDRY_PROJECT_ENDPOINT "https://<resource>.services.ai.azure.com/api/projects/<project>"
azd env set FOUNDRY_CATEGORIZATION_AGENT_NAME RegulatoryCommentCategorizationAgent
azd env set FOUNDRY_GROUPING_AGENT_NAME RegulatoryCommentGroupingAgent
azd provision --preview --no-prompt
azd package --no-prompt
azd up
```

After deployment, grant the App Service managed identity access to the external Foundry project. Existing Cosmos and Azure SQL profiles also require external data-plane/database grants. The runbook gives the exact procedures.

Pull requests run release build, unit/contract/integration tests, deterministic synthetic AI
evaluation, NuGet vulnerability auditing, and Bicep compilation through
[`ci.yml`](.github/workflows/ci.yml).

---

## Configuration cheat sheet

| Setting | Where to set it locally | Where it goes in Azure | Sensitive? |
| --- | --- | --- | --- |
| Regulations.gov API key | Settings page in the running app, or `Api:ApiKey` in `appsettings.json` | Key Vault → app setting `Api__ApiKey` | Yes — keep out of git. |
| Foundry endpoint | Settings page | Key Vault → `Api__FoundryEndpoint` | No (URL only) but kept in KV anyway. |
| Agent names + versions | Settings page | App settings `Api__*AgentName` / `Api__*AgentVersion` | No — they're visible in the Foundry portal. |
| Default document ID | Settings page or `appsettings.json` | App setting `Api__DefaultDocumentId` | No. |
| Batch size | Settings page | App setting `Api__BatchSize` | No. |
| Persistence provider | `Persistence__Provider` | App setting `Persistence__Provider` | No. |
| Cosmos endpoint and containers | `Persistence__Cosmos__*` | App settings `Persistence__Cosmos__*` | Endpoint: no; emulator connection string: yes. |
| Blob payload container | `Persistence__Payloads__BlobContainerUri` | Managed-identity app setting | No. |
| OCR endpoint and limits | `Attachments__*` | App settings `Attachments__*` | No. |
| Application Insights | `APPLICATIONINSIGHTS_CONNECTION_STRING` | Injected by Bicep | Treat as configuration data. |
| Foundry token prices | `Telemetry__FoundryCost__*` | App settings `Telemetry__FoundryCost__*` | No. |

All the runtime overrides are stored in `dotnet_frontend/App_Data/api-settings.json` locally — that folder is in `.gitignore` so your keys never end up in a commit.

---

## Security & what is *not* in git

This repo is intentionally clean of secrets. The following are **gitignored** and you'll never see them on GitHub:

- `.env` (root, used by the Python scripts)
- `azure_func_v2/doed_regulatory_comments_func/local.settings.json`
- `dotnet_frontend/App_Data/` (settings + SQLite DB)
- `.azure/` (azd env state)
- All `bin/`, `obj/`, virtual-env, and pycache folders

Template versions live alongside them: [`.env.example`](.env.example) and [`azure_func_v2/doed_regulatory_comments_func/local.settings.json.example`](azure_func_v2/doed_regulatory_comments_func/local.settings.json.example).

If you fork or clone this for your own org, double-check that any default endpoint URLs in source files such as `dotnet_frontend/Services/ApiSettings.cs` are still empty or generic before sharing.

> Production warning: the current Bicep template secures Azure service calls with managed identity, but it does **not** configure end-user authentication or private networking. Enable App Service Authentication with Microsoft Entra ID or restrict ingress before exposing the app. The Settings page changes global runtime settings and can persist an API key locally, so it must not remain publicly writable.

---

## The older Python pipeline (optional)

If you'd rather run the analysis as a CLI batch job (or use the timer-triggered Azure Function), the original scripts still work:

```powershell
# one-time
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
copy .env.example .env   # then edit .env with your keys

# the pipeline
python fetch_regulations_comments.py     # 1. download comments from Regulations.gov
python consolidate_comments_to_csv.py    # 2. extract attachment text into a CSV
python process_csv_rows.py               # 3. categorize + group via Azure AI agents
```

Deployment for the current Function App is in [`azure_func_v2/README.md`](azure_func_v2/README.md).

---

## Troubleshooting

- **Settings changes disappear after restart** — verify `App_Data/api-settings.json` exists and the content root is writable/persistent. The store normally reloads that file at startup; Azure production should prefer Key Vault-backed app settings.
- **`401 Unauthorized` when running analysis** — your local Azure CLI session has expired. Run `az login` again and refresh the page.
- **`404 Not Found` from the Foundry API** — double-check the project endpoint URL matches what's shown in *Project properties* (it should end in `/api/projects/<project-name>`).
- **`Run AI analysis` button is disabled** — at least the endpoint, Categorization agent name, and Grouping agent name must be filled in on the Settings page.
- **`Rate limit (429)` while analyzing a large docket** — the app already retries with exponential backoff; if it still fails, lower the **Batch size** on the Settings page (try 2 or 3).
- **`403 AuthorizationFailed` during deployment** — verify the target tenant, Contributor permission, and permission to create role assignments. See the deployment runbook.
- **`/health/ready` is unhealthy** — validate the selected SQLite, Azure SQL, or Cosmos configuration and its managed-identity permissions.
- **Old Cosmos runs are temporarily absent from Library** — verify the `/documentIdNormalized` summary container and allow the leased one-time summary backfill to complete.
- **A Cosmos run exceeds 2 MB** — enable Blob payload storage or select Azure SQL; bounded aggregate metadata must still remain below the Cosmos item limit.

---

## License

Internal use, Department of Education analysis project. No public license granted.
