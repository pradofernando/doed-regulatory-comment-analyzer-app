# DoED Regulatory Comments — .NET Frontend

Blazor Server (.NET 9) app that lets you interact with the regulatory comments backend.
By default it talks to the public **Regulations.gov v4 API** (the same API the Python
Azure Function in [`../azure_func`](../azure_func) uses), but the **base URL and API key
can be overridden at runtime from the Settings page** — useful when you want to point
the UI at a custom backend, an APIM gateway, or a local mock.

The app also includes hardened attachment ingestion, optional scanned-PDF OCR, detached analysis
jobs, three persistence providers, paged Cosmos summaries, large-payload Blob offload,
OpenTelemetry/Application Insights instrumentation, health endpoints, and deterministic AI
contract evaluation.

## Pages

| Route | What it does |
| --- | --- |
| `/` | Landing page with quick links. |
| `/comments` | Form to fetch comments by document or docket ID; shows them in a table. |
| `/comments/{id}` | Single comment detail with full text + attachment links. |
| `/analysis` | Run categorization/grouping agents, watch progress, inspect themes, export reports, and ask follow-up questions. |
| `/library` | Page through, rename, reopen, and delete saved analysis runs. |
| `/settings` | Override the API base URL, API key, and default document ID. Persists to `App_Data/api-settings.json`. |

## Runtime flow

1. `RegulationsGovClient` retrieves comments and details.
2. `AttachmentExtractor` obtains text from bounded PDF/DOCX inputs and optionally calls Document Intelligence for sparse PDFs.
3. `AnalysisJobManager` runs a job outside the Blazor circuit so navigation does not cancel it.
4. `FoundryAnalysisService` calls the categorization agent once per comment, then sends categorization batches through the grouping agent.
5. `IAnalysisRepository` persists the result through SQLite, Azure SQL, or Cosmos DB.
6. The Library reopens saved runs; the optional follow-up agent continues a Responses API chain.
7. `OperationalTelemetry` reports bounded metrics without recording prompts, model responses, comment bodies, attachment contents, or API keys.

## Configure the default API

Defaults are read from `appsettings.json` (`Api` section):

```json
{
  "Api": {
    "BaseUrl": "https://api.regulations.gov/v4",
    "ApiKey": "",
    "DefaultDocumentId": "ED-2025-SCC-0481-0001"
  }
}
```

You can also set the API key via env var (matches the Python project):

```powershell
$env:REGULATIONS_GOV_API_KEY = "your-key"
```

Anything edited on the `/settings` page wins over both of the above and is written
to `App_Data/api-settings.json` next to the running app.

Configuration precedence for API and Foundry settings is:

1. Persisted Settings-page overrides.
2. App Service settings/environment variables and `appsettings.<Environment>.json`.
3. `appsettings.json`.

The Settings page operates on a process-global singleton. In production it must be protected by
authentication or network restrictions; otherwise one user can change settings for every user.

## Run locally

```powershell
cd dotnet_frontend
# Optional: provide the Regulations.gov key
$env:REGULATIONS_GOV_API_KEY = "your-key"

dotnet run --launch-profile http
# App available at http://localhost:5007
```

For HTTPS:

```powershell
dotnet dev-certs https --trust
dotnet run --launch-profile https
# https://localhost:7018
```

Azure SDK authentication is environment-aware:

- Development uses `DefaultAzureCredential`, which can use Azure CLI or VS Code sign-in.
- Non-development environments use the deterministic system-assigned `ManagedIdentityCredential`.

## Analysis history database

Select the backend with `Persistence__Provider`. The supported values are `Sqlite`,
`AzureSql`, and `Cosmos`. SQLite remains the default for local development.

### Azure SQL

Use an Azure SQL connection string with Microsoft Entra authentication so no database
password is stored in configuration:

```powershell
$env:Persistence__Provider = "AzureSql"
$env:ConnectionStrings__AnalysisDb = "Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"
```

Create a contained user for the App Service managed identity and grant data reader/data
writer access. Initial schema creation also requires DDL permission; grant it for the first
startup, then remove it after the four analysis tables have been created.

### Azure Cosmos DB for NoSQL

Cosmos stores one analysis run and its categorizations, themes, and follow-up history as a
single aggregate document. A compact summary container supports continuation-token Library
pagination and exact normalized document-ID lookups. Existing aggregate documents are
backfilled into the summary container once. Configure:

```powershell
$env:Persistence__Provider = "Cosmos"
$env:Persistence__Cosmos__Endpoint = "https://<account>.documents.azure.com:443/"
$env:Persistence__Cosmos__DatabaseName = "doed-regulatory-comments"
$env:Persistence__Cosmos__ContainerName = "analysis-runs"
$env:Persistence__Cosmos__SummaryContainerName = "analysis-run-summaries"
```

Grant the local developer or App Service managed identity the **Cosmos DB Built-in Data
Contributor** role on the account. Production uses the App Service system-assigned managed
identity; the optional `Persistence__Cosmos__ConnectionString` setting is intended only for the
local Cosmos DB emulator. Set `Persistence__Cosmos__CreateIfNotExists=true` only when the
credential is allowed to create the database and both containers.

Cosmos uses two physical document shapes:

| Document | Partition key | Notes |
| --- | --- | --- |
| Analysis aggregate | `/id` | Full bounded run metadata, themes, chat history, and categorization metadata. |
| Analysis summary | `/documentIdNormalized` | Compact Library projection with continuation-token paging and exact normalized ID filtering. |

Documents are schema-versioned. When the summary container is added to an older deployment,
a leased one-time backfill creates summary documents. Another app instance can use aggregate
fallback while the lease owner migrates summaries.

The aggregate container excludes raw AI responses, parsed JSON, and chat text from indexing.
When Blob payload storage is configured, raw categorization payloads over 512 KB are gzip
compressed and moved to Blob Storage before the aggregate is written:

```powershell
$env:Persistence__Payloads__BlobContainerUri = "https://<account>.blob.core.windows.net/analysis-run-payloads"
```

Grant the app identity **Storage Blob Data Contributor**. Existing inline Cosmos documents
remain readable. Cosmos DB still limits an item to 2 MB; choose `AzureSql` if bounded metadata
alone can exceed that limit.

Blob payloads use gzip JSON, contain the raw/parsed categorization payload only, and are named
under `analysis-runs/<run-id>/`. Delete operations remove derived summary/blob data before the
authoritative aggregate so an interrupted delete can be retried.

## Attachment security and OCR

Attachment downloads are HTTPS-only, host-allowlisted, streamed with a 25 MB cap, and accepted
only when the MIME type matches a supported PDF or Word signature. PDF extraction stops after
100 pages. Sparse PDFs can optionally use Azure AI Document Intelligence `prebuilt-read`, also
with a separate page cap:

```powershell
$env:Attachments__AllowedHosts__0 = "downloads.regulations.gov"
$env:Attachments__MaxDownloadBytes = "26214400"
$env:Attachments__MaxRedirects = "3"
$env:Attachments__MaxArchiveEntries = "1000"
$env:Attachments__MaxArchiveUncompressedBytes = "104857600"
$env:Attachments__MaxExtractedTextCharacters = "500000"
$env:Attachments__MaxPdfPages = "100"
$env:Attachments__MaxOcrPages = "50"
$env:Attachments__MinPdfTextCharactersPerPage = "20"
$env:Attachments__OcrEndpoint = "https://<resource>.cognitiveservices.azure.com/"
```

OCR is disabled when the endpoint is blank. Azure hosting uses the App Service managed identity;
local development uses the signed-in Azure CLI or VS Code identity.

### Attachment controls

| Control | Default | Behavior |
| --- | ---: | --- |
| Allowed hosts | `downloads.regulations.gov` | Exact DNS hosts or explicit wildcard subdomains only. IP-literal URLs are rejected. |
| Transport | HTTPS/443 | HTTP, credentials in URLs, and nonstandard ports are rejected. |
| Redirects | 3 | Automatic redirects are disabled; every hop is validated before the next request and before forwarding the API key. |
| Download bytes | 25 MB | Responses are streamed and stopped once the cap is exceeded, even without `Content-Length`. |
| MIME/signature | PDF and Word | Declared MIME type must match PDF, OpenXML Word, or legacy Word signatures. |
| OpenXML entries | 1,000 | Limits archive entry count. |
| OpenXML expansion | 100 MB | Limits total declared uncompressed ZIP size. |
| Extracted text | 500,000 characters | Caps local or OCR text retained per attachment. |
| Local PDF pages | 100 | Stops local PDF text extraction at the configured page count. |
| OCR pages | 50 | Limits Document Intelligence analysis independently. |
| OCR threshold | 20 meaningful characters/page | Sparse pages trigger OCR when it is configured. |

`AttachmentText` records page count, pages processed, truncation, and whether OCR supplied the
text. `CategorizationResult.TextSource` distinguishes inline, attachment, and OCR-backed input.

## Telemetry and health

When `APPLICATIONINSIGHTS_CONNECTION_STRING` is present, OpenTelemetry exports request and
dependency traces plus bounded custom metrics for analysis phases, Foundry tokens/retries/429s,
estimated model cost, attachment failures/OCR, and Cosmos RU consumption. Comment text, prompts,
model responses, and API keys are never attached to custom telemetry.

Set current model prices to enable non-zero cost estimates:

```powershell
$env:Telemetry__FoundryCost__InputUsdPerMillionTokens = "1.25"
$env:Telemetry__FoundryCost__OutputUsdPerMillionTokens = "10.00"
```

- `/health/live` checks the process.
- `/health/ready` checks primary persistence connectivity and is used by App Service health checks.

The optional Cosmos summary container is an optimization: readiness validates the authoritative
aggregate container, while Library operations fall back when the summary container is absent.

Custom metric names:

| Metric | Meaning |
| --- | --- |
| `analysis.jobs` | Started/completed/failed/cancelled jobs. |
| `analysis.duration` | End-to-end job duration. |
| `analysis.phase.duration` | Attachment, categorization, grouping, and other phase duration. |
| `foundry.tokens` | Input/output token usage reported by the Responses API. |
| `foundry.estimated_cost` | Estimate using configured per-million token rates. |
| `foundry.request.duration` | Foundry request latency and outcome. |
| `foundry.retries` | Retry attempts. |
| `foundry.rate_limits` | Foundry 429/rate-limit responses. |
| `attachments.download.size` | Validated attachment byte size by bounded format tag. |
| `attachments.failures` | Rejection/extraction reasons with bounded tags. |
| `attachments.ocr` | OCR success/failure count. |
| `cosmos.request_charge` | Request-unit consumption by operation. |

## Tests and evaluation

```powershell
dotnet test ../dotnet_frontend.Tests/DoedRegulatoryComments.Web.Tests.csproj
dotnet test ../dotnet_frontend.Tests/DoedRegulatoryComments.Web.Tests.csproj --filter Category=AiEvaluation
az bicep build --file infra/main.bicep
```

The versioned synthetic fixture at `../dotnet_frontend.Tests/Fixtures/ai-evaluation.v1.json`
checks categorization vocabulary and grouped-analysis completeness without calling Foundry.
`.github/workflows/ci.yml` runs release build, evaluation, all tests, dependency audit, and Bicep
compilation for pull requests and pushes to `main` or `feprado`.

Coverage includes generated multi-page PDFs, unsafe redirects, byte/MIME/signature/archive
limits, Foundry HTTP contracts, job completion/cancellation/persistence, relational paging,
Cosmos mapping/schema/concurrency/token behavior, Blob payload codecs, health-host integration,
exporters, filters, stores, and deterministic AI contracts.

## Deploy to Azure

Use the complete [Azure Deployment Runbook](DEPLOYMENT.md). It covers:

- Required tools, permissions, provider registration, tenant authentication, and azd environments.
- SQLite, Azure SQL, existing Cosmos, and template-managed Cosmos profiles.
- OCR, Blob payload storage, cost telemetry, and alert options.
- Preflight preview, package validation, deployment, RBAC, health checks, and smoke tests.
- Production authentication/private-networking caveats, backups, migration, rollback, updates, cleanup, and troubleshooting.

The shorter [infra README](infra/README.md) is an infrastructure index and quick command reference.

## Production notes

- The Bicep template does not configure end-user authentication. Add Microsoft Entra App Service Authentication or network restrictions before public production use.
- The Settings page can persist the Regulations.gov key in `App_Data/api-settings.json`; protect it and prefer Key Vault-backed app settings in Azure.
- SQLite is a single-instance profile. Use Azure SQL or Cosmos before App Service scale-out.
- The provisioned Azure dependencies use managed identity, but public service endpoints remain enabled. Add VNet integration/private endpoints when required by policy.
- Pin agent versions instead of `latest` when auditability and repeatability matter.
- Back up the selected persistence provider before schema, provider, or infrastructure changes.

## Swap the API at runtime

1. Start the app.
2. Open **Settings** in the sidebar.
3. Replace the **API base URL** (and key, if your backend uses one).
4. Click **Save**.
5. Go to **Comments** and click **Fetch comments** — the request now hits your
   custom endpoint instead of Regulations.gov.

The backend is expected to expose a JSON:API-shaped `GET /comments` endpoint
(`data[]` + optional `meta` and `included[]`) and `GET /comments/{id}`. If your
custom backend has a different shape, adjust
[`Services/RegulationsGovModels.cs`](Services/RegulationsGovModels.cs) and
[`Services/RegulationsGovClient.cs`](Services/RegulationsGovClient.cs).
