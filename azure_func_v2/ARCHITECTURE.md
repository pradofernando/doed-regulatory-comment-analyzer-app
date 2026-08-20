# Azure Function Architecture Overview

## System Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         AZURE FUNCTION                               │
│                   (Runs Daily at 3AM EST)                           │
└─────────────────────────────────────────────────────────────────────┘
                                 │
                                 │ Timer Trigger
                                 │ (CRON: 0 0 8 * * *)
                                 ▼
┌─────────────────────────────────────────────────────────────────────┐
│                          WORKFLOW PHASES                             │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 1: FETCH COMMENTS                                            │
│  ─────────────────────────────────────────────────────────────────  │
│  ┌──────────────┐          ┌─────────────────┐                     │
│  │ Azure        │  API Key │ Regulations.gov │                     │
│  │ Function     │──────────>│ API             │                     │
│  │              │<──────────│                 │                     │
│  └──────────────┘  Comments└─────────────────┘                     │
│         │                                                            │
│         │ Saves: comments_raw_*.json                               │
│         │        comments_extracted_*.json                         │
│         ▼                                                            │
│  ┌──────────────┐                                                   │
│  │ Azure Blob   │                                                   │
│  │ Storage      │                                                   │
│  │ /1_fetch/    │                                                   │
│  └──────────────┘                                                   │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 2: EXTRACT & CONSOLIDATE                                     │
│  ─────────────────────────────────────────────────────────────────  │
│  ┌──────────────┐                                                   │
│  │ Download     │  PDF/DOCX                                         │
│  │ Attachments  │────┐                                              │
│  └──────────────┘    │                                              │
│         │             ▼                                              │
│         │      ┌──────────────┐                                     │
│         │      │ Extract Text │                                     │
│         │      │ - PyPDF2     │                                     │
│         │      │ - python-docx│                                     │
│         │      └──────────────┘                                     │
│         │             │                                              │
│         │             ▼                                              │
│         │      ┌──────────────┐                                     │
│         └─────>│ Consolidate  │                                     │
│                │ Inline Text  │                                     │
│                │ + Attachments│                                     │
│                └──────────────┘                                     │
│                       │                                              │
│                       │ Saves: comments_consolidated_*.csv          │
│                       ▼                                              │
│                ┌──────────────┐                                     │
│                │ Azure Blob   │                                     │
│                │ Storage      │                                     │
│                │ /2_consolidate│                                    │
│                └──────────────┘                                     │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 3: AI CATEGORIZATION                                         │
│  ─────────────────────────────────────────────────────────────────  │
│  ┌──────────────┐                                                   │
│  │ CSV Rows     │                                                   │
│  │ (Comments)   │                                                   │
│  └──────────────┘                                                   │
│         │                                                            │
│         │ For each comment                                          │
│         ▼                                                            │
│  ┌──────────────────┐          ┌─────────────────┐                │
│  │ Azure AI Agent   │ Managed  │ Azure OpenAI    │                │
│  │ Categorization   │ Identity │ via APIM        │                │
│  │ (Agent 1)        │─────────>│                 │                │
│  └──────────────────┘          └─────────────────┘                │
│         │                              │                             │
│         │<─────────────────────────────┘                            │
│         │ Categorization JSON                                       │
│         │                                                            │
│         │ Saves: categorizations_*.json                            │
│         ▼                                                            │
│  ┌──────────────┐                                                   │
│  │ Azure Blob   │                                                   │
│  │ Storage      │                                                   │
│  │ /3_analysis/ │                                                   │
│  └──────────────┘                                                   │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  PHASE 4: GROUP & ANALYZE                                           │
│  ─────────────────────────────────────────────────────────────────  │
│  ┌──────────────┐                                                   │
│  │Categorizations│                                                  │
│  │    (JSON)     │                                                  │
│  └──────────────┘                                                   │
│         │                                                            │
│         │ Split into batches (size: 5)                             │
│         ▼                                                            │
│  ┌──────────────────┐          ┌─────────────────┐                │
│  │ Azure AI Agent   │ Managed  │ Azure OpenAI    │                │
│  │ Grouping         │ Identity │ via APIM        │                │
│  │ (Agent 2)        │─────────>│                 │                │
│  └──────────────────┘          └─────────────────┘                │
│         │                              │                             │
│         │<─────────────────────────────┘                            │
│         │ Collective Analysis JSON                                  │
│         │ - Theme groups                                            │
│         │ - Patterns                                                │
│         │ - Sentiment                                               │
│         │                                                            │
│         │ Saves: grouped_analysis_*.json                           │
│         ▼                                                            │
│  ┌──────────────┐                                                   │
│  │ Azure Blob   │                                                   │
│  │ Storage      │                                                   │
│  │ /3_analysis/ │                                                   │
│  └──────────────┘                                                   │
└─────────────────────────────────────────────────────────────────────┘

                                 ✅
                         WORKFLOW COMPLETE!
```

## Data Flow

```
Regulations.gov API
         │
         │ (Phase 1: Fetch)
         ▼
   Raw Comments JSON ──────────────┐
         │                          │
         │ (Extract metadata)       │
         ▼                          │
Extracted Comments JSON ────────────┤
         │                          │
         │ (Phase 2: Consolidate)   │
         ▼                          ▼
Download Attachments          Azure Blob Storage
         │                    /1_fetch/
         │ (Extract text)           │
         ▼                          │
  PDF/DOCX Text                     │
         │                          │
         │ (Combine)                │
         ▼                          │
Consolidated CSV ────────────────────┼────┐
         │                          │    │
         │ (Phase 3: Categorize)    │    │
         ▼                          ▼    │
   AI Agent (Agent 1)         Azure Blob Storage
         │                    /2_consolidate/
         │ (Process each)           │    │
         ▼                          │    │
Categorizations JSON ─────────────────────┼────┐
         │                          │    │    │
         │ (Phase 4: Group)         │    │    │
         ▼                          │    │    │
   AI Agent (Agent 2)               │    │    │
         │                          │    │    │
         │ (Analyze patterns)       │    │    │
         ▼                          ▼    ▼    ▼
Grouped Analysis JSON ──────> Azure Blob Storage
                              /3_analysis/
```

## Azure Resources

```
┌─────────────────────────────────────────────┐
│          RESOURCE GROUP                      │
│      rg-regulatory-comments                 │
│                                             │
│  ┌─────────────────────────────────────┐  │
│  │   FUNCTION APP                      │  │
│  │   func-regulatory-comments          │  │
│  │   - Runtime: Python 3.9              │  │
│  │   - Plan: Consumption                │  │
│  │   - OS: Linux                        │  │
│  └─────────────────────────────────────┘  │
│                   │                        │
│                   ├───────────────────┐    │
│                   │                   │    │
│  ┌────────────────▼──────┐  ┌────────▼────┐│
│  │ STORAGE ACCOUNT       │  │ STORAGE      ││
│  │ (Function internal)   │  │ ACCOUNT      ││
│  │ storegulatorycomments │  │ (Outputs)    ││
│  │ - Logs                │  │ storeregulatory│
│  │ - Function files      │  │ - Comments   ││
│  └───────────────────────┘  │ - CSV data   ││
│                              │ - Analysis   ││
│                              │              ││
│                              │ Container:   ││
│                              │ regulatory-  ││
│                              │ comments/    ││
│                              └──────────────┘│
│                                             │
│  ┌─────────────────────────────────────┐  │
│  │   APPLICATION INSIGHTS (Optional)   │  │
│  │   func-regulatory-comments-insights │  │
│  │   - Logs                             │  │
│  │   - Metrics                          │  │
│  │   - Alerts                           │  │
│  └─────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
         │                            │
         │ (Calls via Managed Identity)
         ▼                            ▼
┌──────────────────┐    ┌────────────────────────┐
│ Azure OpenAI     │    │ Regulations.gov API    │
│ (via APIM)       │    │ (External)             │
│ - Agent 1        │    │ - Comments data        │
│ - Agent 2        │    │ - Attachments          │
└──────────────────┘    └────────────────────────┘
```

## Timing & Schedule

```
Daily Schedule (EST)

00:00 (Midnight) ────────────────────────────
        │
02:00   │
        │
03:00   ├─► ⏰ FUNCTION TRIGGERS
        │    │
        │    ├─► Phase 1: Fetch Comments (2-3 min)
        │    │
        │    ├─► Phase 2: Extract & Consolidate (5-10 min)
        │    │
        │    ├─► Phase 3: AI Categorize (3-5 min)
        │    │
        │    └─► Phase 4: AI Group & Analyze (2-3 min)
        │         │
03:15   │         └─► ✅ COMPLETE
        │
04:00   │
        │
        ▼
```

**Total Processing Time**: ~10-20 minutes
**Daily Run Time**: 3:00 AM EST (8:00 AM UTC)

## File Outputs

```
Azure Blob Storage: regulatory-comments/

├── 1_fetch/
│   ├── comments_raw_20260129_080000.json         (Full API response)
│   └── comments_extracted_20260129_080000.json   (Simplified comments)
│
├── 2_consolidate/
│   └── comments_consolidated_20260129_080000.csv (CSV with full text)
│
└── 3_analysis/
    ├── categorizations_20260129_080000.json      (Individual analysis)
    └── grouped_analysis_20260129_080000.json     (Collective analysis)
```

## Environment Variables Flow

```
Azure Portal / local.settings.json
         │
         ├─► REGULATIONS_GOV_API_KEY ────► Phase 1 & 2
         │
         ├─► DOCUMENT_ID ────────────────► Phase 1
         │
         ├─► CATEGORIZATION_AGENT_ID ────► Phase 3
         │
         ├─► GROUPING_AGENT_ID ──────────► Phase 4
         │
         ├─► BATCH_SIZE ─────────────────► Phase 4
         │
         └─► AZURE_STORAGE_CONNECTION ───► All Phases (Output)
             STRING
```

## Cost Breakdown (Monthly Estimate)

```
┌──────────────────────────────────────────────┐
│ Service                    Estimated Cost    │
├──────────────────────────────────────────────┤
│ Azure Functions            $0.50 - $1.00     │
│ (Consumption Plan)                           │
│   - Executions: 30/month                     │
│   - Duration: ~15 min each                   │
│                                              │
│ Azure Storage              $1.00 - $5.00     │
│   - Blob storage (10 GB)                     │
│   - Transactions                             │
│                                              │
│ Azure OpenAI               $5.00 - $20.00    │
│   - AI Agent calls                           │
│   - Token usage (varies)                     │
│                                              │
│ Application Insights       $0.00 - $2.00     │
│   - Log Analytics                            │
│   - Telemetry                                │
│                                              │
├──────────────────────────────────────────────┤
│ TOTAL                      $10 - $30/month   │
└──────────────────────────────────────────────┘
```

**Note**: Costs vary based on:
- Number of comments processed
- Attachment sizes and quantity
- AI agent token usage
- Blob storage retention policy

## Security & Authentication

```
┌──────────────────────────────────────────────────┐
│             AUTHENTICATION FLOW                  │
└──────────────────────────────────────────────────┘

Function App
    │
    ├─► Regulations.gov API
    │   └─► Authentication: API Key (in env var)
    │
    ├─► Azure Storage
    │   └─► Authentication: Connection String
    │
    └─► Azure OpenAI (via APIM)
        └─► Authentication: Managed Identity
            (No keys needed!)

Managed Identity Benefits:
✅ No API keys to manage
✅ Automatic credential rotation
✅ Azure AD authentication
✅ Role-based access control
```

## Monitoring & Observability

```
┌──────────────────────────────────────────────────┐
│            MONITORING OPTIONS                     │
└──────────────────────────────────────────────────┘

1. Azure Portal
   └─► Function App → Monitor tab
       - Execution history
       - Success/failure rates
       - Duration metrics

2. Application Insights
   └─► Advanced telemetry
       - Detailed logs
       - Performance metrics
       - Dependency tracking
       - Custom alerts

3. Azure CLI / PowerShell
   └─► Real-time log streaming
       az webapp log tail --name func-regulatory-comments

4. VS Code Extension
   └─► Azure Functions extension
       - Live logs
       - Remote debugging
```

## Success Criteria

```
✅ Function deploys successfully
✅ First run completes without errors
✅ All 4 phases execute in sequence
✅ Output files appear in blob storage
✅ Daily schedule triggers at 3AM EST
✅ Costs remain within budget
✅ Monitoring and alerts configured
```

---

**For detailed setup instructions, see:**
- [QUICKSTART.md](QUICKSTART.md) - 5-minute setup
- [DEPLOYMENT.md](DEPLOYMENT.md) - Step-by-step guide
- [README.md](README.md) - Complete documentation
- [SUMMARY.md](SUMMARY.md) - Overview and features
