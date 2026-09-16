# Analyst guide

## What this app is for

Public comments can contain thousands of pages of submissions and attachments.
This app helps an analyst organize that material, check what the AI actually
used, make corrections, and prepare a draft working response.

It does not decide policy, establish legal authority, or turn public comments
into a representative opinion poll.

You do not need to understand Azure to use the interface. An administrator
configures the data source and AI connection first. The everyday workflow takes
place in your browser.

## A few terms

| Term | Plain-language meaning |
| --- | --- |
| Docket | The collection of material associated with a rulemaking or other regulatory action. |
| Document | An individual published item within a docket. A document filter and a docket filter can cover different submissions. |
| Submission / comment | One public response. It can contain inline text, attachments, or both. |
| Analysis run | A saved result for a particular set of submissions and configuration at a particular time. |
| Theme | A label describing what a comment discusses. |
| Primary policy reason | The main substantive reason behind a commenter's position. It is more specific than general positive or negative sentiment. |
| Stance | Whether the commenter supports or opposes the proposed change, is neutral, has mixed positions, or makes a procedural comment. |
| Source snapshot | The text captured when an analysis ran. It is retained so later evidence links do not silently fetch a changed document. |
| Agent | A model given a specialized set of instructions. This app has four roles, not seven new agents for its seven workspace features. |

## A complete first workflow

1. Open **Comments** and enter a document or docket ID.
2. Check whether **Treat ID as docket** matches the scope you intend.
3. Fetch the comments. Optionally filter or select a subset.
4. Choose **Analyze all fetched** or **Analyze selected**.
5. On **Analysis**, review the coverage panel before reading conclusions.
6. Open a finding or submission to inspect its saved evidence.
7. Under **Review and search**, correct classifications and flag anything
   questionable.
8. Use **Issue-response matrix** to prepare draft working material.
9. Download the report or the original-and-reviewed JSON package.
10. Reopen the result in **Library**, compare it with a later run, or create a
    docket watchlist.

The following sections explain each feature and its limits.

## 1. Evidence-backed findings

### What you see

Theme arguments on the analysis page can be opened to check their evidence.
The evidence page places the current interpretation beside the saved source.
It can show:

- The comment ID and submission number.
- The current theme, primary reason, stance, and summary.
- The original AI classification for comparison.
- The exact captured text and a highlighted supporting quotation.
- The attachment title, source link, and page number when available.
- Extraction warnings and a fingerprint of the captured text.

Source IDs such as `s12-p2` identify passages **within a run**. The full evidence
link includes the run ID, so a citation cannot accidentally refer to the same
passage label in a different analysis.

### What "verified" means here

The application checks that the quoted characters actually occur in the
identified saved passage. For a group finding, it also checks the associated
submission membership and finding linkage.

This is a quotation check, not a proof that an interpretation follows logically
from the quotation. Analysts still need to evaluate context and reasoning.

If an agent invents a source ID, supplies a quotation that cannot be found, or
does not provide evidence for a finding, the app does not manufacture a citation.
It shows that verified evidence is unavailable.

### Attachments and page numbers

PDF and OCR page numbers come from the extractor's actual page results. These
are physical page positions, which may differ from page numbers printed in the
document's footer. Word files do not have stable pagination across different
layouts, so their page number can be unavailable.

Large sources are bounded. Truncated text, failed extraction, and unavailable
pages are disclosed instead of presented as complete evidence. Search and
citations cover the captured text, not material that was never captured.

### Chat citations

The follow-up agent receives current classifications and a bounded selection
of relevant saved passages. It can return a marker such as
`[source:s12-p2]`. The interface turns known source markers into links and
leaves unknown markers unverified.

Submission references such as `#12` can open the submission's saved sources.
They are less specific than a passage citation.

Earlier messages remain part of chat history and may predate human
corrections. New questions use the current analysis context. The agent must
acknowledge when the available context is insufficient.

### How it is implemented

Both the local .NET analysis path and the Function analysis path capture
source snapshots. The model does not create those snapshots. The application
creates them from source data, validates returned citations, and persists them
with the run.

The central .NET contracts are in
[AnalystModels.cs](../dotnet_frontend/Services/AnalystModels.cs).
[SourceEvidence.cs](../dotnet_frontend/Services/SourceEvidence.cs) handles
capture and evidence checks.
[Evidence.razor](../dotnet_frontend/Components/Pages/Evidence.razor) displays
the side-by-side view, and
[CitedText.razor](../dotnet_frontend/Components/CitedText.razor) renders
citations without treating model output as HTML.

## 2. Human review and correction

Open **Review and search**, find a submission, and select **Review**.

You can edit:

- Theme and primary policy reason.
- Stance relative to the proposed change.
- The short summary.
- Review status: unreviewed, approved, or flagged.
- Reviewer label and notes explaining the correction.

Saving adds a revision instead of replacing the original AI record.
**Revision history** shows the saved values, notes, and timestamps.

Current classifications, grouping counts, and report summaries use the effective
reviewed values. This does not retrain a model or rewrite the original evidence.
Derived reviewed summaries are application-generated working summaries, not a
new independent AI assessment.

### Working with other people

Workspace data is shared by this app. A reviewer label is descriptive text, not
proof of identity. There is no new authentication or authorization system in
this feature.

If another browser saves a newer version while you are editing, your save is
rejected as a conflict. Reload the latest state and reapply your intended
changes. The application does not silently choose the last writer.

### Exports

- Regular report downloads use the current reviewed classifications.
- Word and Excel include coverage, provenance, evidence, and review information.
- The **original + reviewed package** includes the original run, effective run,
  and saved review state in JSON.
- Raw AI responses are labeled as original material, not substituted for the
  current reviewed values.

### How it is implemented

[ReviewService.cs](../dotnet_frontend/Services/ReviewService.cs) validates and
saves revisions.
[AnalystEngine.cs](../dotnet_frontend/Services/AnalystEngine.cs) derives the
effective analysis without mutating the original.
[WorkspaceRepository.cs](../dotnet_frontend/Services/WorkspaceRepository.cs)
stores versioned records and checks for concurrent changes.

## 3. Docket watchlists and notifications

Open **Docket watchlists** to follow a docket and give it a recognizable name.
A watch is a monitoring configuration, not a new analysis by itself.

### The first check

The first successful check establishes a baseline of known submissions.
Existing comments are not all reported as newly arrived.

Subsequent checks compare IDs and available modification metadata/body
fingerprints with that baseline. Notifications identify newly observed or
modified submissions and provide links for investigation.

A metadata change is not necessarily a change in policy position. Changes in
attachment contents cannot be inferred solely from unchanged comment metadata.
Use captured-source comparisons between analyses to investigate source changes
in more detail.

### Deadlines

A watch can include a manually entered deadline. Verify it against the official
notice; an analyst-entered date is not automatically an authoritative deadline.
The monitor creates reminders as the deadline approaches and avoids repeatedly
creating the same reminder.

### Where notifications go

Open **Notifications** to read the persistent in-app inbox and mark entries
read. The inbox is shared by users of the app. Email and Teams delivery are not
part of this implementation.

The background monitor needs the web app to be running. Automatic monitoring
is disabled by default locally and enabled through the deployment
configuration. Manual checks remain available.

### Automatic analysis and costs

Watching a docket does not automatically authorize unlimited model calls.
Automatic analysis of new or changed submissions is an explicit per-watch
opt-in. It uses the existing analysis runner and saves results to the Library.

A change-only analysis is a selected subset. Comparing it with a previous full
run must not be interpreted as a change in the entire docket's stance.
Request and processing limits still apply, and failed analysis must not be
mistaken for a successfully completed digest.

### How it is implemented

[DocketMonitorService.cs](../dotnet_frontend/Services/DocketMonitorService.cs)
coordinates checks and background processing.
Watch configuration, baseline state, notifications, and concurrency leases
use the same durable workspace repository as the other features.

The monitor uses versioned leases to prevent two web instances from processing
the same watch concurrently. Failed retrieval does not advance a successful
baseline.

The existing Function daily-analysis trigger remains separate: it runs the
configured analysis job when explicitly enabled with `-EnableScheduledAnalysis`.
It is disabled by default to avoid unrequested AI usage. Watchlists add a
user-managed monitoring workflow.

## 4. Form-letter and near-duplicate detection

Open **Similar submissions** in the analyst workspace.

The app distinguishes:

- Total submission volume, which is never reduced by this feature.
- Distinct recorded primary policy reasons.
- Detected groups of identical or similar captured text.

Exact matching normalizes text for comparison. Near matching compares
overlapping sequences of words, using an explicit high similarity threshold.
It is a reading aid, not an AI determination of motive.

Open the individual sources to inspect wording differences and substantive
variations. A template may include a meaningful addition or a changed position.

**Do not interpret a similar-text group as proof of coordination, a count of
unique people, or a reason to discard submissions.** All submissions remain in
the analysis and its totals.

Comments with unavailable sources cannot be reliably compared. A failure to
detect a group is not proof that every submission is unique.

The implementation lives in
[AnalystEngine.cs](../dotnet_frontend/Services/AnalystEngine.cs) and runs without
an additional model call.

## 5. Regulatory issue-and-response matrix

The matrix is a working document with these columns:

**Rule section -> concern -> supporting submissions -> requested change ->
draft response -> review status**

Start from the generated working rows, or choose **Add issue**. Open supporting
submissions to check the source. Enter a rule section only when you can verify
it, edit the requested change and draft response, and record your review status.

Unknown rule sections are left unspecified rather than invented.

All proposed response language is draft working material requiring human
review. An analyst's approval status is not an official agency approval or a
legal determination.

Changes are saved with issue revision history. The matrix can be exported as
CSV and is also included in Word/Excel report downloads.

The UI is in
[AnalystWorkbench.razor](../dotnet_frontend/Components/AnalystWorkbench.razor);
validation and persistence are handled by
[ReviewService.cs](../dotnet_frontend/Services/ReviewService.cs).

## 6. Deeper search and saved views

The **Comments** page can filter metadata and any body text already available
from the fetch. After analysis, **Review and search** can search the captured
body and attachment text as well as the analysis.

Combine:

- Text search.
- Organization.
- Theme or primary reason.
- Stance.
- Review status.
- Posted-date range, interpreted as UTC calendar dates.

Choose **Save filters as a new view** to keep a reusable named filter.
For example, save a view for a particular organization and flagged comments.
The view saves the filter definition, not a frozen result list. Reusing it on a
different run can return a different set of submissions.

Saved views are shared. Deleting a view removes the saved filter, not comments
or analysis runs.

Missing, unreadable, or truncated source text cannot be searched beyond what
was captured. The interface calls out that limitation.

## 7. Coverage and quality

Read **Coverage and quality** before using a report.

| Value | Meaning |
| --- | --- |
| Available | The total returned by the API for the relevant query, when known. |
| Fetched | How many submissions were fetched for the selection workflow. |
| Selected | How many were included in this analysis request. |
| Categorized with usable text | Submissions with a categorization and captured usable source text. |
| Unreadable | Submissions whose capture did not produce usable text. |
| Partial | Sources affected by truncation, incomplete extraction, or other recorded limitations. |
| Uncaptured | Submissions with no saved source snapshot, including legacy runs. |

An unknown value is shown as **Unknown**, not zero.

The panel also shows the capture time, pipeline version, configured model,
agent-version settings, batch size, validation setting, and extraction warnings.

Configured values are not a live inventory of the model service. An agent
version set to `latest` is not a reproducible version pin. Model-reported
confidence is not a calibrated measure of factual accuracy.

Most importantly, stance counts describe the analyzed submissions, not the
general population.

## Comparing two analyses

Open **Compare analyses**, or choose **Compare** next to a Library run.

Choose a before and an after run. Inspect:

- Submissions present in only one run.
- Changed source fingerprints where both runs have snapshots.
- Theme and stance counts with their denominators.
- Differences in scope, configuration, pipeline, and available provenance.

The comparison warns when the runs are not directly comparable. Differences
can arise from selection, extraction, changed instructions, model settings,
or human corrections, not just changes in incoming comments.

A theme-label difference is a candidate for investigation, not automatic proof
that a new substantive issue emerged. A comment absent from a selected subset
has not necessarily been removed from the docket.

[RunComparisonService.cs](../dotnet_frontend/Services/RunComparisonService.cs)
implements the comparison rules.

## Storage and backward compatibility

Original analysis runs remain in the existing selected provider:

- **SQLite:** a local database file, useful for development or a single app
  instance.
- **Azure SQL:** a managed relational database.
- **Cosmos DB:** a managed document database.

New reviews, saved views, watches, and notifications use versioned workspace
records. Relational initialization adds the required schema to existing
databases. Cosmos uses a separate `analyst-workspace` container with `/id` as
its partition key.

Large AI and source payloads can be compressed into private Blob Storage.
Blob Storage is simply file storage; the database retains the reference.
The new source-bearing payload format remains compatible with old
categorization-only payloads.

Older runs can still be reopened. They do not automatically gain source
passages or provenance that were never saved. Run a new analysis when those
features are needed; never treat an old missing snapshot as an empty comment.

Database upgrades require the configured identity to have the necessary
schema-change permissions. Existing external Cosmos deployments also need the
workspace container and appropriate data access. Do not expose storage
credentials to the browser.

## Deployment and agent instructions

### What Azure components do

| Component | Job |
| --- | --- |
| App Service | Runs the browser-facing web app and its optional docket monitor. |
| Azure Functions | Runs queued and scheduled analysis work independently of a browser session. |
| Microsoft Foundry | Hosts the models and four specialized prompt-agent definitions. |
| Cosmos DB / Azure SQL | Stores runs and workspace state, depending on configuration. |
| Blob Storage | Stores large compressed payloads. |
| Document Intelligence | Reads text from scanned documents when configured. |
| Azure AI Search | Optionally provides a configured methodology knowledge base. |

### Four roles

1. **Categorization:** reads an individual comment, identifies its
   proposal-relative stance and policy reasons, and cites supplied passages.
2. **Grouping:** organizes categorized comments by substantive reason and stance
   while preserving distinct concerns.
3. **Validation:** checks membership, stance consistency, and exact coverage;
   makes minimal corrections. It is not a substitute for human review.
4. **Follow-up:** answers questions from the supplied current analysis and source
   passages, without inventing missing evidence.

The full instructions are in
[AGENT_PROMPTS.md](../azure_func_v2/AGENT_PROMPTS.md).

Bicep describes infrastructure. The deployment scripts read the prompt
definitions, publish agent versions, and pass their returned names and versions
to the app. Identical deployed definitions can be reused; changes are versioned.
The agent-creation step must account for tool configuration as well as prompt
text and model configuration.

Methodology search requires a real configured project connection and index.
Telling an agent to search does not grant search access. Without a configured
retrieval tool, the deployment uses a no-retrieval mode: agents must not claim
to have searched or invent knowledge-base results.

Source documents and comments are treated as data, not instructions. Retrieved
methodology may guide style and terminology, but does not establish what a
commenter said.

The personal deployment uses GPT-5.5 version `2026-04-24` with Global Standard.
The model name, version, and SKU are explicit deployment parameters. A missing
or mismatched model is an error, not permission to switch to an older model.
Optional Search infrastructure and embeddings are not provisioned unless
explicitly requested.

### New application settings

| Setting | Purpose |
| --- | --- |
| `Workspace:ContainerName` / `Workspace__ContainerName` | Names the Cosmos workspace container; default `analyst-workspace`. |
| `Monitoring:Enabled` / `Monitoring__Enabled` | Enables automatic web-app docket checks; defaults to false locally. |
| `Monitoring:PollIntervalMinutes` / `Monitoring__PollIntervalMinutes` | Controls the periodic check interval; default 60 minutes. |

The double-underscore spelling is used for environment variables. Existing
model, API, storage, and extraction settings remain relevant.

The Bicep templates and root deployment workflow include the workspace
configuration. Preparation and compilation do not create Azure resources;
deployment must be run separately with the intended subscription, permissions,
and cost approval.

For the personal subscription checked during the 2026-09-16 readiness pass,
inherited policies disable public network access to Storage, Key Vault, and
Cosmos DB. The current templates need approved private connectivity before
deployment. A successful template preview does not prove those connections work.
The web app's client-IP restriction protects its incoming browser endpoint but
does not provide a private route to its backend services.

See [the main README](../README.md) and
[deployment runbook](../dotnet_frontend/DEPLOYMENT.md) for setup.

## Verification and limits

The repository includes deterministic tests for source matching, page
provenance, persistence, review history, concurrency, filtering, duplicates,
monitoring, comparisons, exports, and citation rendering.

These tests use synthetic data or mocked services. They are not proof of live
Azure connectivity, production quota availability, or a model's factual
accuracy. After deployment, verify a small permitted docket end to end:
capture a source, inspect a citation, save a correction, reopen the run, export
it, check a watch, and read its notification.

No end-user authentication is added here. Do not confuse shared review history
or version checks with access control. Apply the organization's existing
deployment access restrictions before exposing the app.
