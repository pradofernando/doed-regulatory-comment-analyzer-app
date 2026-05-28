# Plan: Q&A + Selection + Export + Non-dev UX + Azure deployment

Six feature tracks for the Blazor frontend. Tracks 1–4 are independent UI/service work and can land in any order. Track 5 is local-dev hygiene (env files, gpt-5.4 wiring, gitignore). Track 6 takes the app live on Azure with persistent history of every analysis and chat. Track 4 (UX polish) touches every page so it goes last; Track 6 builds on Tracks 1–3 so it goes after them.

## Track 1 — Manual comment selection (Comments.razor + AnalysisStore)

Today the Run AI analysis button sends every comment from the current fetch. Change it so the user picks which ones, with selections persisted across page changes until explicitly cleared.

**Steps**
1. Add `Dictionary<string, CommentResource> SelectedComments` to `AnalysisStore` (keyed by comment ID so de-dup works across pages); add `Clear()`, `ToggleAll(IEnumerable<CommentResource>)`, `IsSelected(string id)`.
2. In `Comments.razor` results table, add a leading checkbox column wired to `Analysis.SelectedComments`.
3. Above the table, add a selection toolbar with: `[Select all visible]` `[Newest 25]` `[Oldest 25]` `[Next 25 unselected]` `[Clear selection]` and a live count "`X of Y on this page selected · Z total across all fetches`".
4. Change the "Run AI analysis" button to use `Analysis.SelectedComments.Values` (fall back to all visible only if selection is empty, with a confirm toast).
5. Keep `_request.PageSize` / `_request.Page` exactly as today — they govern fetch size, not analysis scope.
6. On the Analysis page, add a small "Analyzing N selected comments from K total fetched" line in the run header so the user can see what scope went in.

**Relevant files**
- [dotnet_frontend/Services/AnalysisStore.cs](dotnet_frontend/Services/AnalysisStore.cs) — add selection dictionary + helpers.
- [dotnet_frontend/Components/Pages/Comments.razor](dotnet_frontend/Components/Pages/Comments.razor) — checkbox column, toolbar, change `RunAnalysis()` to pass selection.
- [dotnet_frontend/Components/Pages/Analysis.razor](dotnet_frontend/Components/Pages/Analysis.razor) — header line showing "N selected of K fetched".

**Verification**
1. Fetch 50 newest comments. Click `Oldest 25` → bottom 25 checked. Click `Newest 25` → top 25 also checked (total 50). Click `Clear selection` → all unchecked.
2. Fetch page 1 (25 rows), select 10. Fetch page 2 (25 more rows), select another 10 → counter reads "10 of 25 on this page selected · 20 total across all fetches".
3. Click Run AI analysis → only the 20 selected go to Foundry (verify by `AnalysisRun.TotalComments == 20`).

## Track 2 — Follow-up Q&A chat (Settings + ApiSettings + FoundryAnalysisService + Analysis.razor)

Per your choice, this uses a NEW dedicated Foundry agent that you'll create separately and paste into Settings. The new agent (and the existing categorization + grouping agents) will be upgraded to the new **gpt-5.4** model deployment — see Track 5 for where to get the deployment name and how to wire it through `.env` / `local.settings.json` / `api-settings.json`.

**Steps**
1. Add `FollowUpAgentId` (string) to `ApiSettings` with empty default; add `DefaultFollowUpAgentId = ""` constant.
2. In `ApiSettingsStore`, plumb the new field through `Update()`, `Clone()`, the override-file merge, and an env-var override `FOLLOWUP_AGENT_ID`.
3. In Settings page, add a third input field "Follow-up Q&A agent ID" under "AI agents" with helper text "Optional. Enables the chat panel after a Collective analysis run."
4. Add to `FoundryAnalysisService`:
   - `Task<string> StartFollowUpThreadAsync(AnalysisRun run, ApiSettings settings, CancellationToken ct)` — creates a new thread, sends a single priming message containing: overall summary, theme groups (name + description + count + stance + common arguments + member submission numbers), and a compact per-comment index (submission#, comment ID, commenter, organization, posted date, category, sentiment, summary). Returns the thread ID.
   - `Task<string> AskFollowUpAsync(string threadId, ApiSettings settings, string question, CancellationToken ct)` — calls `SendAndAwaitAsync` against `FollowUpAgentId` on that thread; returns the assistant text. Reuses the existing retry-on-429 loop.
5. Store the follow-up thread ID + chat history on `AnalysisRun`:
   - `public string? FollowUpThreadId { get; set; }`
   - `public List<FollowUpTurn> FollowUpHistory { get; set; } = new();` where `FollowUpTurn { string Role, string Text, DateTimeOffset At }`.
6. In `Analysis.razor`, render a new `<section class="surface-card">` below Recommendations:
   - Title "Ask follow-up questions"
   - If `FollowUpAgentId` empty → grey hint "Add a follow-up agent ID in Settings to enable this."
   - If empty thread → "Start chat" button (calls `StartFollowUpThreadAsync`, sets `FollowUpThreadId`).
   - Scrollable history (user bubbles right, agent bubbles left, both with timestamps).
   - Input textarea + Send button (disabled while waiting). On submit: append user turn, call `AskFollowUpAsync`, append agent turn, scroll to bottom.
   - Suggested-question chips above the input: "Which theme has the most opposition?", "Summarize comments from [Organization]", "What did submission #N say?"

**Relevant files**
- [dotnet_frontend/Services/ApiSettings.cs](dotnet_frontend/Services/ApiSettings.cs) — add `FollowUpAgentId`.
- [dotnet_frontend/Services/ApiSettingsStore.cs](dotnet_frontend/Services/ApiSettingsStore.cs) — plumb the new field through init / merge / Update / Clone / env override.
- [dotnet_frontend/Components/Pages/Settings.razor](dotnet_frontend/Components/Pages/Settings.razor) — new input field.
- [dotnet_frontend/Services/FoundryAnalysisService.cs](dotnet_frontend/Services/FoundryAnalysisService.cs) — `StartFollowUpThreadAsync`, `AskFollowUpAsync`. Reuse `SendAndAwaitAsync`.
- [dotnet_frontend/Services/AnalysisModels.cs](dotnet_frontend/Services/AnalysisModels.cs) — add `FollowUpTurn`, properties on `AnalysisRun`.
- [dotnet_frontend/Components/Pages/Analysis.razor](dotnet_frontend/Components/Pages/Analysis.razor) — chat panel below Recommendations.

**Verification**
1. With no `FollowUpAgentId` configured → chat panel shows "Add a follow-up agent ID in Settings to enable this." and no input.
2. After pasting an agent ID + saving Settings → Analysis page shows "Start chat". Click → priming message sent silently, input becomes active.
3. Ask "Which theme has the most support?" → agent responds within ~5s referencing the actual theme names from the run.
4. Ask "What did submission #3 say?" → agent quotes/summarizes that specific commenter (verifies the priming context worked).
5. Cause a 429 mid-question → retry loop kicks in, eventually answers; no UI crash.

## Track 3 — Word + Excel export of the collective analysis

DocumentFormat.OpenXml 3.5.1 (already installed for DOCX parsing) also writes XLSX — no new packages needed.

**Steps**
1. Create `Services/CollectiveAnalysisExporter.cs` with two static methods:
   - `byte[] BuildWord(AnalysisRun run, IReadOnlyList<CommentResource> selectedComments, IReadOnlyDictionary<string, AttachmentExtractionResult> attachmentText)`
   - `byte[] BuildExcel(AnalysisRun run, IReadOnlyList<CommentResource> selectedComments, IReadOnlyDictionary<string, AttachmentExtractionResult> attachmentText)`
2. **Word layout** (single `.docx`):
   - **Cover**: Title "Collective Analysis", subtitle = document ID, run completed timestamp, "N comments analyzed".
   - **Section 1 — Executive Summary**: `OverallSummary` + `OverallSentiment` (callout style).
   - **Section 2 — Themes** (one H2 per `ThemeGroup`):
     - Description, count, stance pie-style table (support/oppose/neutral/mixed columns).
     - "Common arguments" bulleted list.
     - "Commenters in this theme" sub-list — for each member submission#: bold name (or "Anonymous"), org, posted date in italics, then the FULL comment text (inline or extracted attachment text from `attachmentText[c.Id].CombinedText` or `DetailComment`), then a small italic line "AI category: X · sentiment: Y · summary: Z" pulled from `CategorizationResult.Parsed`.
   - **Section 3 — Patterns** (bulleted).
   - **Section 4 — Recommendations** (bulleted).
   - **Appendix — Per-comment categorizations table**: submission#, comment ID, name, org, category, sentiment, summary.
3. **Excel layout** (workbook with 4 sheets):
   - **Sheet 1 "Summary"**: rows = Document ID, Run date, Total comments, Overall sentiment, Overall summary, blank, Patterns (one per row), blank, Recommendations (one per row).
   - **Sheet 2 "Themes"**: columns = Theme | Description | Count | Support | Oppose | Neutral | Mixed | Common arguments (joined with `; `).
   - **Sheet 3 "Commenters by theme"**: columns = Theme | Submission# | Comment ID | Commenter | Organization | Posted | Title | Text source | Category | Sentiment | AI summary | Full text (truncated to 32K chars per cell).
   - **Sheet 4 "Per-comment categorizations"**: same columns as the Word appendix, plus a `Raw JSON` column with the agent's verbatim response.
   - Freeze top row + autofit columns where the OpenXML SDK permits.
4. Register `CollectiveAnalysisExporter` (static — no DI registration needed; alternatively make it sealed + injectable if you want logging).
5. In `Analysis.razor`, add two buttons to the existing toolbar (next to the existing JSON download): `[Download Word]` and `[Download Excel]`. They build the bytes and stream via `IJSRuntime` (same pattern Comments.razor uses for CSV/JSON download — confirm by reading Comments.razor's existing `DownloadCsv()` implementation).
6. File naming: `collective-analysis_{documentId}_{yyyyMMdd-HHmm}.{docx|xlsx}`.

**Relevant files**
- [dotnet_frontend/Services/CollectiveAnalysisExporter.cs](dotnet_frontend/Services/CollectiveAnalysisExporter.cs) — NEW file with both builders.
- [dotnet_frontend/Components/Pages/Analysis.razor](dotnet_frontend/Components/Pages/Analysis.razor) — add the two download buttons + handlers.
- [dotnet_frontend/Services/AnalysisStore.cs](dotnet_frontend/Services/AnalysisStore.cs) — already holds `Comments`; need to also stash the `Dictionary<string, AttachmentExtractionResult>` produced during the run so the exporter has the full text. Add `AttachmentText` property; populate it from `FoundryAnalysisService.RunAsync` before returning.

**Verification**
1. Run an analysis on 10 selected comments, then click Download Word → file opens in Word, has cover page, themes are H2 headings, each theme contains all member commenters with full text.
2. Click Download Excel → opens cleanly in Excel, Sheet 3 has one row per (theme, commenter) pair, Sheet 1 totals match the run.
3. Trigger a run where 5 of 10 comments are attachment-only → Word/Excel still show full text (pulled from `AttachmentExtractionResult.CombinedText` / `DetailComment`).
4. Validate `docx` opens in Word Online (catches malformed OpenXML).

## Track 4 — Non-dev UX polish (all pages)

Hide every technical artifact (URLs, badges, agent IDs, raw JSON) behind a "Show technical details" toggle on every non-Settings page. Reword developer terms to plain English.

**Steps**
1. Create a tiny shared component `Components/Shared/TechDetailsToggle.razor` — a `<details><summary>Show technical details</summary>@ChildContent</details>` wrapper styled to match the design tokens. Used on Comments, CommentDetail, Analysis.
2. **Home.razor** — rewrite the hero copy + cards for a non-technical audience:
   - Hero subtitle → "Browse, organize, and analyze public comments submitted on Department of Education rulemakings. No setup required — get started below."
   - Replace the "Bring your own API" card with "AI-powered themes" describing the categorize + group flow in 1 sentence ("Send a batch of comments to AI and get back themes, sentiment, and a downloadable report").
   - Remove the `<a class="hero__actions">Configure API</a>` button; replace with a softer secondary "How it works" anchor that scrolls to a new bullet list at the bottom.
   - Keep the visual style; only swap text.
3. **Comments.razor**:
   - Rename labels: "Document or docket ID" → "Regulation or docket number", "Comments to analyze" → "How many to load", "Use docket filter" → "Search the entire docket", "Run AI analysis" → "Analyze selected comments".
   - Move the "Active API" header line + the `<code>@_result.RequestedUrl</code>` line + the warning badges into a `<TechDetailsToggle>` collapsed by default.
   - Reword "Heads up: none of these comments have inline text…" alert to "Some of these comments are stored as attached PDFs — we'll read them for you when you click Analyze."
4. **CommentDetail.razor**: wrap the `comment.id` `<code>` snippets and any agency/document-type metadata grid in a `<TechDetailsToggle>`. Keep title, commenter, organization, date, body text, and attachment list visible by default.
5. **Analysis.razor**:
   - Rename heading "Collective analysis" → keep as-is (you confirmed); rename "Per-comment categorizations" → "How each comment was tagged".
   - Move the `RawResponse` `<pre>` and the per-comment JSON blobs into `<TechDetailsToggle>` collapsed by default.
   - Reword the "Failed to parse JSON…" warning to "We couldn't get a structured result this time — the raw response is below if you want to see it."
6. **Settings.razor** — unchanged. This is the dev surface.
7. **MainLayout / NavMenu** — rename nav link "AI Analysis" → "Analysis"; sidebar footer hint stays since it points at Settings (dev surface).
8. Update [dotnet_frontend/wwwroot/app.css](dotnet_frontend/wwwroot/app.css) with styles for `.tech-details` (smaller font, muted color, no border, indented).

**Relevant files**
- [dotnet_frontend/Components/Shared/TechDetailsToggle.razor](dotnet_frontend/Components/Shared/TechDetailsToggle.razor) — NEW reusable disclosure component.
- [dotnet_frontend/Components/Pages/Home.razor](dotnet_frontend/Components/Pages/Home.razor) — copy rewrite.
- [dotnet_frontend/Components/Pages/Comments.razor](dotnet_frontend/Components/Pages/Comments.razor) — label rewrites + wrap technical bits.
- [dotnet_frontend/Components/Pages/CommentDetail.razor](dotnet_frontend/Components/Pages/CommentDetail.razor) — wrap ID/metadata bits.
- [dotnet_frontend/Components/Pages/Analysis.razor](dotnet_frontend/Components/Pages/Analysis.razor) — rename section heading + wrap raw blobs.
- [dotnet_frontend/Components/Layout/NavMenu.razor](dotnet_frontend/Components/Layout/NavMenu.razor) — rename nav link.
- [dotnet_frontend/wwwroot/app.css](dotnet_frontend/wwwroot/app.css) — `.tech-details` styles.

**Verification**
1. Open `/` as a fresh user → no API URLs, no asst IDs, no `code` tags visible in the hero or cards.
2. Open `/comments` → fetch form has plain-English labels; Active API line is hidden until I expand "Show technical details".
3. Open `/analysis` after a run → no raw JSON visible by default; expanding the toggle shows the full RawResponse pre-block.
4. Open `/settings` → unchanged, all dev knobs still exposed.

## Track 5 — Credentials, gpt-5.4 model wiring, and `.gitignore` hardening

FErnadndo will be (a) creating the new follow-up agent in Microsoft Foundry, (b) upgrading the existing categorization + grouping agents to use the **gpt-5.4** model deployment, and (c) needs working `.env` / `local.settings.json` files locally (NOT just `.example` versions). All real secrets must stay out of git. Where each value comes from must be documented inline in both the real and the example files so a future teammate can recreate the setup.

**Steps**
1. **Create `.env` at repo root** (real values, gitignored) by copying `.env.example` and filling in:
   - `AZURE_AI_AGENT_ENDPOINT` — Foundry portal → your project → top-right "…" → Project properties → copy the `endpoint` URL ending in `/api/projects/<project-name>`.
   - `AZURE_AI_AGENT_SUBSCRIPTION_ID` — Azure portal → Subscriptions → copy the subscription ID hosting the Foundry resource.
   - `AZURE_AI_AGENT_RESOURCE_GROUP_NAME` — Foundry portal → project → Management center → Resources → resource group name.
   - `AZURE_AI_AGENT_PROJECT_NAME` — Foundry portal → project name (top-left).
   - `AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME` — Foundry portal → Models + endpoints → find the **gpt-5.4** deployment → copy the **Deployment name** column (not the model name). Example: `gpt-5.4` or `gpt-5p4-prod`.
   - `REGULATIONS_GOV_API_KEY` — already provisioned; reuse the existing key.
2. **Create `azure_func/doed_regulatory_comments_func/local.settings.json`** (real values, gitignored) by copying `local.settings.json.example` and filling in:
   - Same Foundry / Azure values as above (the function app reads them through `os.environ`).
   - `CATEGORIZATION_AGENT_ID` — Foundry portal → Agents → categorization agent → copy `asst_…` ID from the URL or the right-hand details pane.
   - `GROUPING_AGENT_ID` — same path for the grouping agent.
   - `FOLLOWUP_AGENT_ID` (NEW key, added in Track 2) — same path for the new follow-up agent.
   - `BATCH_SIZE` — start with `5`; lower if you see rate-limit errors against the gpt-5.4 TPM quota.
3. **Create `dotnet_frontend/App_Data/api-settings.json`** (real values, gitignored) — this is the file the Blazor app reads at startup via `ApiSettingsStore`. Mirror the structure already used by `ApiSettings.cs` and include all three agent IDs + endpoint + the gpt-5.4 deployment name fields. If the file already exists with the gpt-4o agents, just append the follow-up agent ID.
4. **Update `.gitignore`** at repo root to add (`.env` is already there):
   ```
   # Local secrets — never commit
   azure_func/doed_regulatory_comments_func/local.settings.json
   dotnet_frontend/App_Data/api-settings.json
   dotnet_frontend/bin/
   dotnet_frontend/obj/
   ```
   Run `git rm --cached <path>` for any of these that are already tracked so the new ignore rule takes effect.
5. **Scrub `.env.example`** — replace the real `REGULATIONS_GOV_API_KEY` (`TgV8…`) with the placeholder `your_regulations_gov_api_key_here` and rotate the leaked key in Regulations.gov before pushing publicly.
6. **Add a header comment block to each `.example` file** that explicitly tells a new teammate where to source every value. Format both files like:
   ```
   # ─────────────────────────────────────────────────────────────
   #  HOW TO FILL THIS IN
   #  Copy this file to `.env` (or `local.settings.json` for the
   #  function app) — the copy is gitignored, this template is not.
   #
   #  AZURE_AI_AGENT_ENDPOINT
   #    Foundry portal → your project → "…" menu (top-right) →
   #    Project properties → copy the `endpoint` URL.
   #
   #  AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME
   #    Foundry portal → Models + endpoints → find your gpt-5.4
   #    deployment row → copy the "Deployment name" column.
   #    NOTE: This is the DEPLOYMENT name (you chose it when you
   #    clicked Deploy), NOT the underlying model name.
   #
   #  CATEGORIZATION_AGENT_ID / GROUPING_AGENT_ID / FOLLOWUP_AGENT_ID
   #    Foundry portal → Agents → open the agent → copy the
   #    `asst_…` ID from the page URL or right-hand details pane.
   #    Each agent should be configured to use the gpt-5.4
   #    deployment (Agent settings → Model).
   #
   #  REGULATIONS_GOV_API_KEY
   #    Request at https://open.gsa.gov/api/regulationsgov/ —
   #    issued via api.data.gov, sent as the `X-Api-Key` header.
   # ─────────────────────────────────────────────────────────────
   ```
7. **Add XML doc comments to `ApiSettings.cs`** above each default constant explaining where the value comes from. Example:
   ```csharp
   /// <summary>Foundry project endpoint. Get it from Foundry portal → project → "…" → Project properties → endpoint URL.</summary>
   public const string DefaultFoundryEndpoint = "https://DOE-Demo.services.ai.azure.com/api/projects/DOE-Proj";

   /// <summary>Foundry agent ID for per-comment categorization. Get it from Foundry portal → Agents → open agent → copy asst_… ID. Agent should use the gpt-5.4 model deployment.</summary>
   public const string DefaultCategorizationAgentId = ""; // asst_… set per environment, not in source
   ```
   Repeat for grouping agent and the new follow-up agent constant. Also add a `DefaultModelDeploymentName = "gpt-5.4"` constant with the same documentation pattern so the frontend has a sensible fallback if the env var is missing.
8. **Add an `AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME` field to `ApiSettings`** (string, defaults to `DefaultModelDeploymentName`) and surface it on the Settings page under "AI agents" with helper text "The Foundry model deployment name (e.g. `gpt-5.4`). Find it in Foundry portal → Models + endpoints." Plumb through `ApiSettingsStore` the same way as the agent IDs. The frontend doesn't pick the model directly (agents do), but capturing it makes the dev surface honest about what the agents are running on and is needed if we later wire up the AOAI Chat Completions API as a fallback.
9. **Update Foundry agents** (manual, in the Foundry portal — not a code change):
   - Open each agent (categorization, grouping, new follow-up).
   - Settings → Model → select the gpt-5.4 deployment.
   - Save. Verify the model badge on the agent's overview page reads `gpt-5.4`.

**Relevant files**
- [.env](.env) — NEW, real values, gitignored.
- [.env.example](.env.example) — scrub real API key, add the inline "HOW TO FILL THIS IN" header.
- [azure_func/doed_regulatory_comments_func/local.settings.json](azure_func/doed_regulatory_comments_func/local.settings.json) — NEW, real values, gitignored. Includes the new `FOLLOWUP_AGENT_ID`.
- [azure_func/doed_regulatory_comments_func/local.settings.json.example](azure_func/doed_regulatory_comments_func/local.settings.json.example) — add the inline "HOW TO FILL THIS IN" header (as a JSON comment block at the top — Functions tolerates `//` comments) and a new `FOLLOWUP_AGENT_ID` placeholder.
- [dotnet_frontend/App_Data/api-settings.json](dotnet_frontend/App_Data/api-settings.json) — real values, gitignored. Add `FollowUpAgentId` + `ModelDeploymentName`.
- [.gitignore](.gitignore) — add the four entries above.
- [dotnet_frontend/Services/ApiSettings.cs](dotnet_frontend/Services/ApiSettings.cs) — add `ModelDeploymentName` field + `DefaultModelDeploymentName` constant + XML doc comments on every default constant explaining provenance.
- [dotnet_frontend/Services/ApiSettingsStore.cs](dotnet_frontend/Services/ApiSettingsStore.cs) — plumb `ModelDeploymentName` through init/merge/Update/Clone and add `AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME` env override.
- [dotnet_frontend/Components/Pages/Settings.razor](dotnet_frontend/Components/Pages/Settings.razor) — surface `ModelDeploymentName` input.

**Verification**
1. `git status` after creating the new files shows ONLY the example files and code as modified — `.env`, `local.settings.json`, and `api-settings.json` must be ignored (`git check-ignore -v <path>` confirms).
2. Run the function app locally → it reads from `local.settings.json` and successfully calls Foundry with the gpt-5.4 deployment (check first batch logs for `model=gpt-5.4`).
3. Run the Blazor frontend → Settings page shows the gpt-5.4 deployment name pre-filled from `App_Data/api-settings.json`. Open `ApiSettings.cs` in VS Code → hover each constant → XML doc tooltip appears with the "where to get it" instructions.
4. Open `.env.example` as a fresh teammate → the header comment alone is enough to know what to put in each variable without asking anyone.
5. `grep -r "<leaked-regs-key-prefix>" .` returns ZERO matches anywhere in the repo (real key fully scrubbed from examples and committed code).

## Track 6 — Azure deployment + persistent history (App Service + Azure SQL + Library page)

Today the analysis lives in `AnalysisStore` (in-memory, per circuit) and disappears on restart. To run this as a real product where FErnadndo can come back days later and reopen every analysis + chat, we need three things together: persistent storage, a Library UI to browse / reopen runs, and Azure hosting wired into IaC + `azd`. This track depends on Tracks 1–3 (selection, follow-up chat, exports) because the persisted row stores those artifacts.

### Hosting + database (recommended SKUs)

- **Azure App Service** Linux, .NET 9 stack, Always On = on, WebSockets = on (required for Blazor Server SignalR). Start at **B1** (~$13/mo) for dev; move to **P0v3** for production demos.
- **Azure SQL Database** serverless tier (`GP_S_Gen5_1`, 5 GB, auto-pause after 1h). ~$5–15/mo when mostly idle, scales up on demand. Picked over Cosmos DB because (a) we want queries like "list my runs for docket X sorted by date", trivial in SQL; (b) chat history is structurally relational (thread→turns); (c) EF Core gives the cleanest dev loop. The big JSON-shaped `AnalysisRun` payload still gets stored verbatim in an `nvarchar(max)` column for hydration, with a few denormalized columns alongside it for fast list/filter.
- **Azure Key Vault** for the Regulations.gov API key. No Foundry secret needed — Managed Identity handles agent auth.
- **Managed Identity** on the App Service with: **SQL DB Contributor** on its DB (or membership in a SQL contained-user role), **Key Vault Secrets User** on the vault, and **Azure AI Developer** + **Cognitive Services User** on the Foundry project (replaces the local `az login` flow that `DefaultAzureCredential` uses today).
- **Application Insights** wired in for request/exception telemetry.
- The existing Function App stays as-is and is brought into the new IaC root (see step 1) so a single `azd up` provisions everything.

### Database schema (EF Core)

Add a `Data/` folder with the following entities. Indexes on `AnalysisRunRecord (UserId, CreatedAt DESC)` and `AnalysisRunRecord (DocumentId)`.

- **`AnalysisRunRecord`** — Id (Guid PK), UserId (string, from Easy Auth), DocumentId, Title (nullable, user-editable), CreatedAt, CompletedAt, CommentCount, OverallSummary (`nvarchar(max)`), OverallSentiment, ThemeCount, RawRunJson (`nvarchar(max)` — the full serialized `AnalysisRun` for hydration), DeletedAt (nullable — soft delete).
- **`AnalyzedCommentRecord`** — Id, RunId FK, CommentResourceId, CommenterName, OrgName, PostedAt, Category, Sentiment, AISummary, FullText (`nvarchar(max)`).
- **`FollowUpThreadRecord`** — Id, RunId FK, FoundryThreadId, StartedAt, LastTouched.
- **`FollowUpTurnRecord`** — Id, ThreadId FK, OrderIndex, Role, Text (`nvarchar(max)`), At.

### Library page (`/library`)

New page with a sortable, filterable list of past runs:

- Columns: Date · Document/Docket · Title (editable inline) · # comments · # themes · Overall sentiment · Actions `[Open] [Continue chat] [Download Word] [Download Excel] [Delete]`.
- Filters: docket prefix dropdown (distinct `DocumentId` per user), date-range picker, free-text search against `Title` + `OverallSummary`.
- Pagination at 25 per page; total count in the header.
- **Open** → loads `RawRunJson` into `AnalysisStore.LastRun`, hydrates `AnalysisStore.Comments` from `AnalyzedCommentRecord` rows, then navigates to `/analysis?runId={id}` (deep link so refresh / share works).
- **Continue chat** → loads the matching `FollowUpThreadRecord` + turns into `AnalysisRun.FollowUpHistory`, navigates to `/analysis?runId={id}#chat` with the chat panel scrolled into view and input focused. If the Foundry thread is older than 30 days (or otherwise gone), show a warning and offer "Start a new chat with the same context" (re-primes a fresh thread using the same priming logic from Track 2).
- **Delete** → soft delete (flip `DeletedAt`); nightly cleanup job (or simple `WHERE DeletedAt < dateadd(day, -30, getutcdate())` purge on app startup) hard-deletes after 30 days.
- The Word/Excel buttons in the library row reuse Track 3's `CollectiveAnalysisExporter` against the hydrated run — no need to regenerate from Foundry.

### Steps

1. **Consolidate IaC at the repo root**: create `infra/main.bicep` + `infra/main.parameters.json` that provisions the App Service stack AND wraps the existing function infra. Move `azure_func/infra/main.bicep` into a module imported from the new root. Create `azure.yaml` at repo root declaring both services (`dotnet_frontend` as `appservice`, `azure_func` as `function`).
2. **Add EF Core packages** to `dotnet_frontend.csproj`: `Microsoft.EntityFrameworkCore.SqlServer` (9.x), `Microsoft.EntityFrameworkCore.Design`, `Microsoft.Data.SqlClient`. Connection string for production uses `Authentication=Active Directory Default` so the Managed Identity authenticates against SQL.
3. **Create entities + `AnalysisDbContext`** under `Data/`. Register the DbContext in DI as Scoped: `builder.Services.AddDbContext<AnalysisDbContext>(opt => opt.UseSqlServer(cfg.GetConnectionString("Sql")))`. Local dev = LocalDB / SQL Server Express in `appsettings.Development.json`; Azure = the managed-identity connection string from app setting `ConnectionStrings__Sql`.
4. **Create `Services/HistoryService.cs`** (Scoped) with:
   - `Task<Guid> SaveRunAsync(AnalysisRun run, IReadOnlyList<CommentResource> selected, IReadOnlyDictionary<string, AttachmentExtractionResult> attachmentText, string userId)` — single transaction for run + comments.
   - `Task<AnalysisRun?> LoadRunAsync(Guid id, string userId)` — deserializes `RawRunJson`.
   - `Task<IReadOnlyList<AnalysisRunListItem>> ListRunsAsync(string userId, ListFilter filter)` — returns the lightweight DTO the Library table renders.
   - `Task SoftDeleteRunAsync(Guid id, string userId)`.
   - `Task UpdateTitleAsync(Guid id, string userId, string title)`.
   - `Task AppendFollowUpAsync(Guid runId, FollowUpTurn turn)` — also called by Track 2's chat send handler so every turn is durable.
5. **Auto-save on completion**: at the end of `FoundryAnalysisService.RunAsync`, call `HistoryService.SaveRunAsync` and stash the returned Guid on `AnalysisStore.LastRun.PersistedId` so the UI knows the run is saved. No "Save" button — saves are automatic.
6. **Add `/library` page** (`Components/Pages/Library.razor`) per the spec above. Add a `[Library]` link to `NavMenu.razor` between Analysis and Settings.
7. **Deep-link hydration on `/analysis`**: accept `[Parameter, SupplyParameterFromQuery] public Guid? RunId { get; set; }`. If set and `AnalysisStore.LastRun?.PersistedId != RunId`, call `HistoryService.LoadRunAsync` and hydrate the store before rendering. This makes URLs shareable inside the org (anyone with the link + access reopens the exact same run).
8. **Authentication**: enable App Service Easy Auth with Microsoft identity provider (Entra ID). Read user ID from the `X-MS-CLIENT-PRINCIPAL-ID` header in a new Scoped `CurrentUserAccessor` service; locally, fall back to a `local-dev-user` so the app still works without `az login`. Every `HistoryService` call takes `userId` so users only see their own runs (returns 404 if a deep-link `runId` belongs to someone else).
9. **Secrets to Key Vault**: move `RegulationsGovApiKey` to Key Vault; reference from App Service config as `@Microsoft.KeyVault(SecretUri=https://…)`. Foundry uses Managed Identity, so no key there. `ApiSettingsStore` already reads env vars, so production picks up the KV-resolved values with no code change.
10. **Migrations + first deploy**:
    - `dotnet ef migrations add InitialSchema`
    - `dotnet ef database update` against local DB.
    - `azd up` to provision Azure.
    - On App Service startup in production, call `db.Database.Migrate()` once so the schema is created/updated automatically.
11. **CI/CD (optional but recommended)**: GitHub Actions workflow using OIDC federated credential → `azd deploy` on push to `main`. Skip if you prefer manual `azd deploy` for now.

### Relevant files

- [infra/main.bicep](infra/main.bicep) — NEW top-level IaC: App Service Plan (Linux), App Service (.NET 9), SQL Server + DB (serverless), Key Vault, App Insights, Managed Identity + role assignments. Imports the function module.
- [infra/main.parameters.json](infra/main.parameters.json) — NEW.
- [infra/modules/function.bicep](infra/modules/function.bicep) — NEW (extracted from `azure_func/infra/main.bicep`).
- [azure.yaml](azure.yaml) — NEW at repo root: declares both services for `azd`.
- [dotnet_frontend/dotnet_frontend.csproj](dotnet_frontend/dotnet_frontend.csproj) — add EF Core packages.
- [dotnet_frontend/Data/AnalysisDbContext.cs](dotnet_frontend/Data/AnalysisDbContext.cs) — NEW.
- [dotnet_frontend/Data/Entities/AnalysisRunRecord.cs](dotnet_frontend/Data/Entities/AnalysisRunRecord.cs) — NEW.
- [dotnet_frontend/Data/Entities/AnalyzedCommentRecord.cs](dotnet_frontend/Data/Entities/AnalyzedCommentRecord.cs) — NEW.
- [dotnet_frontend/Data/Entities/FollowUpThreadRecord.cs](dotnet_frontend/Data/Entities/FollowUpThreadRecord.cs) — NEW.
- [dotnet_frontend/Data/Entities/FollowUpTurnRecord.cs](dotnet_frontend/Data/Entities/FollowUpTurnRecord.cs) — NEW.
- [dotnet_frontend/Services/HistoryService.cs](dotnet_frontend/Services/HistoryService.cs) — NEW.
- [dotnet_frontend/Services/CurrentUserAccessor.cs](dotnet_frontend/Services/CurrentUserAccessor.cs) — NEW. Reads Easy Auth header or returns dev user.
- [dotnet_frontend/Services/AnalysisStore.cs](dotnet_frontend/Services/AnalysisStore.cs) — add `PersistedId` so we know the run is durable.
- [dotnet_frontend/Services/AnalysisModels.cs](dotnet_frontend/Services/AnalysisModels.cs) — add `PersistedId` to `AnalysisRun`.
- [dotnet_frontend/Services/FoundryAnalysisService.cs](dotnet_frontend/Services/FoundryAnalysisService.cs) — call `HistoryService.SaveRunAsync` at end of `RunAsync`; call `HistoryService.AppendFollowUpAsync` on every chat turn (works with Track 2).
- [dotnet_frontend/Components/Pages/Library.razor](dotnet_frontend/Components/Pages/Library.razor) — NEW.
- [dotnet_frontend/Components/Pages/Analysis.razor](dotnet_frontend/Components/Pages/Analysis.razor) — accept `?runId=` query param and hydrate from DB.
- [dotnet_frontend/Components/Layout/NavMenu.razor](dotnet_frontend/Components/Layout/NavMenu.razor) — add Library link.
- [dotnet_frontend/Program.cs](dotnet_frontend/Program.cs) — register DbContext, HistoryService, CurrentUserAccessor; run migrations on startup in non-dev.
- [dotnet_frontend/appsettings.json](dotnet_frontend/appsettings.json) + [dotnet_frontend/appsettings.Development.json](dotnet_frontend/appsettings.Development.json) — connection string slots.

### Verification

1. `azd up` provisions all resources without error. `az deployment group what-if` shows the App Service, SQL DB, Key Vault, and role assignments.
2. Open the deployed URL → Easy Auth redirects to Microsoft login → after sign-in, `/library` loads (empty).
3. Run an analysis on 10 comments → on completion, `/library` shows one row; click Open → `/analysis?runId=…` hydrates with the same themes/sentiment and the same selected comments.
4. Run a follow-up chat (Track 2), refresh the page, click Continue chat from Library → chat history is intact and you can ask one more question that appends to the same thread.
5. Sign in as a different user (incognito window) → `/library` is empty for them; pasting a deep-link `runId` belonging to the first user returns 404.
6. Soft-delete a run → disappears from `/library`; `SELECT * FROM AnalysisRunRecord WHERE Id = …` in SQL still shows it with `DeletedAt` set.
7. Restart the App Service (`az webapp restart`) → all runs and chats survive.
8. Click Download Word from a Library row (without first opening the run) → file downloads with the same content as opening + downloading from the Analysis page.

## Decisions

- Q&A: **new dedicated follow-up agent ID** (added in Settings) with a single thread per Q&A session, primed once with the full run context. Each new "Start chat" creates a fresh thread.
- Selection: **checkbox column + quick picks (Newest 25 / Oldest 25 / Next 25 unselected) + Clear**, with selections **persisted across page changes** in `AnalysisStore`.
- Export depth: **full** — every theme contains every member commenter with their original text (or attachment-extracted text).
- Non-dev UX: **hide technical bits behind a "Show technical details" toggle** on each non-Settings page; Settings stays unchanged.
- Model: all three Foundry agents (categorization, grouping, follow-up) will run on the **gpt-5.4** deployment. Deployment name is captured in `ApiSettings.ModelDeploymentName` (set via Settings page, `.env`, or `local.settings.json`) and surfaced for transparency — agents themselves are bound to the model in the Foundry portal.
- Secrets: real values live ONLY in `.env`, `local.settings.json`, and `App_Data/api-settings.json` locally — all three gitignored. In Azure, the same values move to App Service config (with the Regulations.gov key resolved from Key Vault) and Foundry auth uses Managed Identity, so no secret needs to ship in the container image.
- Hosting: **Azure App Service (Linux, .NET 9, B1→P0v3)** with WebSockets enabled for Blazor Server. The existing Python Function App stays as a parallel service and is brought into the same `azd` deployment.
- Persistence: **Azure SQL Database serverless (`GP_S_Gen5_1`, 5 GB)** with **EF Core**. Schema is small: runs, comments, follow-up threads, follow-up turns. Run auto-saves on completion; soft-delete with 30-day purge.
- Auth: **App Service Easy Auth with Entra ID** for per-user history isolation. Local dev uses a `local-dev-user` fallback so nothing breaks without `az login` interactive sign-in.
- IaC: a single `azd` deployment at repo root provisioning App Service + SQL + Key Vault + App Insights + the existing Function App via Bicep modules.

## Scope boundaries

- IN: features described above, including creating the real `.env` / `local.settings.json` / `api-settings.json` files locally, scrubbing the real Regulations.gov API key from `.env.example`, and provisioning the new Azure stack (App Service + SQL + Key Vault + App Insights) via `azd`.
- IN: Library page with per-user history, deep-link hydration of `/analysis?runId=`, soft delete with 30-day purge, and resuming follow-up chats from Library.
- OUT: in-memory `AnalysisStore` removal — it stays as the per-circuit cache for the *current* run; the database is the persistent backing store, not a replacement for the in-memory model.
- OUT: creating the new follow-up agent inside Foundry (you'll do that in the portal) and switching the existing agents to gpt-5.4 (also a portal action). Track 5 documents the steps; the agent ID + deployment name come back into the code via `.env` / Settings.
- OUT: multi-tenant isolation beyond per-user `UserId` column; per-user quotas; admin/audit views; team sharing of runs (every user sees only their own).
- OUT: migrating the existing Python Function App's logic into the Blazor app — they stay as two services in the same `azd` deployment.
- OUT: CI/CD pipeline (GitHub Actions OIDC → `azd deploy`). Track 6 step 11 mentions it as a recommendation but the plan assumes manual `azd deploy` is acceptable initially.
- OUT: pushing to the new GitHub repo — still deferred pending your A/B/C secret-scrubbing decision, though Track 5 unblocks the Regulations.gov key issue and Track 6 introduces no new secrets.

## Further considerations

1. The follow-up chat's priming message can get large (~50-100KB for 50 comments). If you regularly hit context-window limits, we may want to summarize each commenter to ~200 chars before priming. Recommend starting with full text and shrinking only if Foundry complains. gpt-5.4's larger context window should make this much more comfortable than gpt-4o was.
2. Selection persistence across fetches means if you switch from docket A to docket B without clearing, you'll send a mix of both to analysis. Recommend showing the docket-IDs-in-selection beside the count to make this obvious; or auto-clear when `DocumentId` changes. Default plan: show docket diversity, don't auto-clear.
3. Excel cell limit is 32,767 chars. Comments with very long PDF text will get truncated in Sheet 3 with `… [truncated]`. Recommend keeping the truncation; the Word doc has no such limit so the full text is preserved there.
4. gpt-5.4 has a different TPM quota than gpt-4o (verify in Foundry portal → Management center → Quota). If the per-batch token rate spikes, the existing retry-on-429 loop in `FoundryAnalysisService.SendAndAwaitAsync` will handle it transparently, but a smaller `BATCH_SIZE` (3 instead of 5) may give smoother throughput on the new deployment.
5. The `ModelDeploymentName` field is currently informational only — agents are bound to a model in Foundry, not at call time. If we later add a direct AOAI Chat Completions fallback (e.g. for the follow-up chat when no follow-up agent is configured), that field becomes load-bearing.
6. **Cost estimate** (Track 6): App Service B1 ~$13/mo + SQL serverless ~$5–15/mo (mostly idle) + Key Vault ~$0.03 per 10K ops + App Insights ~$2.30/GB ingested + Foundry per-token. Expect ~$25–40/mo at low usage, scaling with how much analysis you run. P0v3 is closer to $55/mo if you need it.
7. **Foundry thread lifetime** (Track 6): Foundry persistent threads stay around but the documented retention isn't guaranteed forever. The Library page handles missing threads gracefully by offering a re-prime with the saved run context, so we don't lose chat continuity even if Foundry rotates the underlying thread.
8. **Schema migrations on App Service startup** (Track 6 step 10) is convenient but introduces a small risk during simultaneous deploys + manual SQL changes. Acceptable for a single-instance B1 deployment; if we scale to multi-instance later, switch to the EF Migrations Bundle pattern executed once during `azd deploy`.
9. **Existing in-memory `AnalysisStore`** remains the per-circuit cache after Track 6 — the DB is durable backing, not a replacement. This keeps the Track 1–4 work fully usable even before Track 6 lands; Track 6 just adds the save/hydrate seam at the edges.
