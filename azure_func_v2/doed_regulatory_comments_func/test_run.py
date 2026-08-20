"""
Local test runner - runs the full workflow without the Azure Functions host.
Set MAX_COMMENTS to a small number (e.g. 3) for a quick test.
"""

import os
import json
import asyncio
import logging
import datetime
import csv
import io

# ── Configure logging so you can see everything in the terminal ──────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)

# ── Load local.settings.json automatically ───────────────────────────────────
settings_path = os.path.join(os.path.dirname(__file__), "local.settings.json")
if os.path.exists(settings_path):
    with open(settings_path) as f:
        settings = json.load(f)
    for k, v in settings.get("Values", {}).items():
        if v:  # only set non-empty values
            os.environ.setdefault(k, v)
    logging.info(f"Loaded settings from local.settings.json")
else:
    logging.warning("local.settings.json not found — using existing env vars")

# ── Import after env vars are set ────────────────────────────────────────────
from function_app import (
    fetch_comments_from_api,
    extract_comment_text,
    consolidate_comments_to_csv,
    categorize_with_agent,
    group_categorizations,
    convert_grouped_analysis_to_csv,
    upload_to_blob,
)

async def main():
    timestamp = datetime.datetime.utcnow().strftime("%Y%m%d_%H%M%S")

    api_key              = os.environ["REGULATIONS_GOV_API_KEY"]
    document_id          = os.environ.get("DOCUMENT_ID", "ED-2025-SCC-0481-0001")
    categorization_id    = os.environ["CATEGORIZATION_AGENT_ID"]
    grouping_id          = os.environ["GROUPING_AGENT_ID"]
    batch_size           = int(os.environ.get("BATCH_SIZE", "5"))
    max_comments         = int(os.environ["MAX_COMMENTS"]) if os.environ.get("MAX_COMMENTS") else None
    storage_account      = os.environ["AZURE_STORAGE_ACCOUNT_NAME"]

    logging.info("=" * 60)
    logging.info(f"Document : {document_id}")
    logging.info(f"Max comments: {max_comments or 'ALL'}")
    logging.info(f"Batch size  : {batch_size}")
    logging.info("=" * 60)

    # ── Phase 1: Fetch ────────────────────────────────────────────────────────
    logging.info("\n>>> PHASE 1: Fetching comments from Regulations.gov...")
    comments = fetch_comments_from_api(document_id, api_key, max_comments=max_comments)

    if not comments:
        logging.warning("No comments with commentOnId filter — trying docket filter...")
        comments = fetch_comments_from_api(document_id, api_key,
                                           max_comments=max_comments,
                                           use_docket_filter=True)
    if not comments:
        logging.error("No comments found. Exiting.")
        return

    logging.info(f"Fetched {len(comments)} comment(s)")

    extracted = extract_comment_text(comments, api_key)
    with open(f"output_1_raw_{timestamp}.json", "w") as f:
        json.dump(extracted, f, indent=2)
    logging.info(f"Saved  output_1_raw_{timestamp}.json")

    # Also upload to blob
    upload_to_blob(json.dumps(extracted, indent=2),
                   f"1_fetch/comments_extracted_{timestamp}.json", storage_account)

    # ── Phase 2: Consolidate + extract PDF text ────────────────────────────────
    logging.info("\n>>> PHASE 2: Downloading attachments and extracting text...")
    csv_rows = consolidate_comments_to_csv(extracted, api_key)

    output = io.StringIO()
    fieldnames = ['comment_number', 'comment_id', 'posted_date', 'commenter_name',
                  'organization', 'title', 'has_attachments', 'attachment_titles', 'comment_text']
    writer = csv.DictWriter(output, fieldnames=fieldnames)
    writer.writeheader()
    writer.writerows(csv_rows)
    csv_content = output.getvalue()

    with open(f"output_2_consolidated_{timestamp}.csv", "w", newline='', encoding='utf-8') as f:
        f.write(csv_content)
    logging.info(f"Saved  output_2_consolidated_{timestamp}.csv  ({len(csv_rows)} rows)")

    upload_to_blob(csv_content, f"2_consolidate/comments_consolidated_{timestamp}.csv", storage_account)

    # ── Phase 3: AI Categorization ────────────────────────────────────────────
    logging.info("\n>>> PHASE 3: AI agent categorizing each comment...")
    categorizations = await categorize_with_agent(csv_rows, categorization_id)

    cat_data = {
        "timestamp": timestamp,
        "total_comments": len(categorizations),
        "categorizations": categorizations,
    }
    with open(f"output_3_categorizations_{timestamp}.json", "w") as f:
        json.dump(cat_data, f, indent=2)
    logging.info(f"Saved  output_3_categorizations_{timestamp}.json")

    upload_to_blob(json.dumps(cat_data, indent=2),
                   f"3_analysis/categorizations_{timestamp}.json", storage_account)

    # Print a preview
    logging.info("\n--- Categorization preview (first comment) ---")
    if categorizations:
        print(json.dumps(categorizations[0], indent=2))

    # ── Phase 4: AI Grouping / Summary ────────────────────────────────────────
    logging.info("\n>>> PHASE 4: AI agent grouping and summarizing...")
    grouped_analysis = await group_categorizations(categorizations, grouping_id, batch_size)

    grouped_data = {
        "phase": "grouping_analysis",
        "timestamp": timestamp,
        "total_comments_analyzed": len(categorizations),
        "collective_analysis": grouped_analysis,
    }
    with open(f"output_4_grouped_{timestamp}.json", "w") as f:
        json.dump(grouped_data, f, indent=2)
    logging.info(f"Saved  output_4_grouped_{timestamp}.json")

    grouped_csv = convert_grouped_analysis_to_csv(grouped_data)
    with open(f"output_4_grouped_{timestamp}.csv", "w", newline='', encoding='utf-8') as f:
        f.write(grouped_csv)
    logging.info(f"Saved  output_4_grouped_{timestamp}.csv  ← final human-readable report")

    upload_to_blob(json.dumps(grouped_data, indent=2),
                   f"3_analysis/grouped_analysis_{timestamp}.json", storage_account)
    upload_to_blob(grouped_csv,
                   f"3_analysis/grouped_analysis_{timestamp}.csv", storage_account)

    # Print the final summary
    logging.info("\n" + "=" * 60)
    logging.info("FINAL SUMMARY OUTPUT:")
    logging.info("=" * 60)
    print(grouped_analysis)
    logging.info("=" * 60)
    logging.info("All output files saved locally and to Azure Blob Storage.")

if __name__ == "__main__":
    asyncio.run(main())
