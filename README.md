# DoED Regulatory Comment Analyzer

A web app that pulls **public comments** from [Regulations.gov](https://www.regulations.gov) and uses **Azure AI Foundry agents** to read them, categorize each one, and produce a single combined analysis. Built so a non-technical user can run an entire policy-comment analysis from a browser.

> **Status:** the **.NET 9 Blazor web app** in `dotnet_frontend/` is the primary tool. Everything else in this repo (the Python scripts at the root + the `azure_func/` Function App) is the earlier, command-line version of the same workflow — kept for reference and for batch jobs.

---

## What does it actually do?

1. **Fetch** every public comment from a Regulations.gov document or docket (by ID, e.g. `ED-2025-SCC-0481-0001`).
2. **Download attachments** (PDFs and Word docs) and extract their text so the AI sees the full comment, not just the inline note.
3. **Categorize each comment individually** — a Foundry prompt agent reads it and emits a structured JSON record (themes, sentiment, key points).
4. **Group all categorizations** into a single collective report — a second Foundry prompt agent finds common themes across all submissions and produces the final narrative.
5. **Chat with the analysis** — an optional third Foundry agent lets you ask follow-up questions about what was found.
6. **Save / re-open / export** — every run is persisted to a local SQLite database; you can re-open old runs, download the categorizations CSV/JSON, or export the final report.

---

## Repo layout

```
.
├── dotnet_frontend/                  ← The web app (primary). Blazor Server, .NET 9.
│   ├── Components/Pages/             ← UI: Comments, Analysis, Settings, etc.
│   ├── Services/                     ← Regulations.gov client + Foundry analysis service.
│   ├── Data/                         ← SQLite store for past analysis runs.
│   ├── infra/                        ← Bicep + bicepparam for deploying to Azure App Service.
│   ├── App_Data/                     ← (Local-only, gitignored) settings + SQLite DB.
│   └── README.md                     ← Frontend-specific notes.
│
├── azure_func/                       ← Older Python Azure Functions implementation (batch job).
│   ├── doed_regulatory_comments_func/
│   └── infra/                        ← Bicep template for the Function-based deployment.
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

You'll spend 99% of your time in `dotnet_frontend/`.

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
| **Follow-up** *(optional)* | `RegulatoryCommentFollowUpAgent` | Stateful chat about the completed analysis. Leave blank to disable the chat panel. |

Each agent needs an instruction/prompt template appropriate for its job — see the existing prompts in your Foundry project, or use the example prompts in `azure_func/README.md` as a starting point. All three can share the same underlying model (e.g. `gpt-5.4`).

---

## Deploy to Azure

The web app deploys to **Azure App Service (Linux)** with a Bicep template that also creates Application Insights and Key Vault.

```powershell
cd dotnet_frontend
azd auth login
azd env new doedweb-dev

# fill in the values — these are read by main.bicepparam
azd env set REGS_API_KEY <your-regulations-gov-key>
azd env set FOUNDRY_PROJECT_ENDPOINT https://<your-resource>.services.ai.azure.com/api/projects/<your-project>
azd env set FOUNDRY_CATEGORIZATION_AGENT_NAME RegulatoryCommentCategorizationAgent
azd env set FOUNDRY_GROUPING_AGENT_NAME      RegulatoryCommentGroupingAgent
azd env set FOUNDRY_FOLLOWUP_AGENT_NAME      RegulatoryCommentFollowUpAgent   # optional

azd up
```

After the first deploy you also need to grant the web app's managed identity the **Azure AI User** role on the Foundry project — the `azd up` output prints the exact `az role assignment create` command to run.

Full details and the raw-`az` alternative are in [`dotnet_frontend/infra/README.md`](dotnet_frontend/infra/README.md).

---

## Configuration cheat sheet

| Setting | Where to set it locally | Where it goes in Azure | Sensitive? |
| --- | --- | --- | --- |
| Regulations.gov API key | Settings page in the running app, or `Api:ApiKey` in `appsettings.json` | Key Vault → app setting `Api__ApiKey` | Yes — keep out of git. |
| Foundry endpoint | Settings page | Key Vault → `Api__FoundryEndpoint` | No (URL only) but kept in KV anyway. |
| Agent names + versions | Settings page | App settings `Api__*AgentName` / `Api__*AgentVersion` | No — they're visible in the Foundry portal. |
| Default document ID | Settings page or `appsettings.json` | App setting `Api__DefaultDocumentId` | No. |
| Batch size | Settings page | App setting `Api__BatchSize` | No. |

All the runtime overrides are stored in `dotnet_frontend/App_Data/api-settings.json` locally — that folder is in `.gitignore` so your keys never end up in a commit.

---

## Security & what is *not* in git

This repo is intentionally clean of secrets. The following are **gitignored** and you'll never see them on GitHub:

- `.env` (root, used by the Python scripts)
- `azure_func/doed_regulatory_comments_func/local.settings.json`
- `dotnet_frontend/App_Data/` (settings + SQLite DB)
- `.azure/` (azd env state)
- All `bin/`, `obj/`, virtual-env, and pycache folders

Template versions live alongside them: [`.env.example`](.env.example) and [`azure_func/doed_regulatory_comments_func/local.settings.json.example`](azure_func/doed_regulatory_comments_func/local.settings.json.example).

If you fork or clone this for your own org, double-check that any default endpoint URLs in source files (e.g. `dotnet_frontend/Services/ApiSettings.cs`, `azure_func/infra/main.bicep`) are still empty/generic before sharing.

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

Deployment for the Function App version is in [`azure_func/README.md`](azure_func/README.md).

---

## Troubleshooting

- **Settings page shows blank fields after restart** — by design: `App_Data/api-settings.json` is per-machine. Re-enter and save.
- **`401 Unauthorized` when running analysis** — your local Azure CLI session has expired. Run `az login` again and refresh the page.
- **`404 Not Found` from the Foundry API** — double-check the project endpoint URL matches what's shown in *Project properties* (it should end in `/api/projects/<project-name>`).
- **`Run AI analysis` button is disabled** — at least the endpoint, Categorization agent name, and Grouping agent name must be filled in on the Settings page.
- **`Rate limit (429)` while analyzing a large docket** — the app already retries with exponential backoff; if it still fails, lower the **Batch size** on the Settings page (try 2 or 3).

---

## License

Internal use, Department of Education analysis project. No public license granted.
