import azure.functions as func
import datetime
import json
import logging
import os
import re
import time
import csv
import io
import asyncio
from typing import List, Dict, Any, Optional, Tuple
import requests
import PyPDF2
from docx import Document as DocxDocument
from azure.storage.blob import BlobServiceClient
from azure.identity import DefaultAzureCredential
from azure.identity.aio import DefaultAzureCredential as AsyncDefaultAzureCredential
from semantic_kernel.agents import AgentThread, AzureAIAgent, AzureAIAgentThread
from semantic_kernel.contents import ChatMessageContent, FunctionCallContent, FunctionResultContent

app = func.FunctionApp()

# ============================================================================
# PHASE 1: FETCH COMMENTS FROM REGULATIONS.GOV API
# ============================================================================

def fetch_comments_from_api(document_id: str, api_key: str, posted_date_from: Optional[str] = None, 
                            posted_date_to: Optional[str] = None, max_comments: Optional[int] = None,
                            use_docket_filter: bool = False) -> List[Dict]:
    """Fetch comments from regulations.gov API"""
    base_url = "https://api.regulations.gov/v4/comments"
    headers = {"X-Api-Key": api_key}
    
    search_id = document_id
    filter_param = "filter[commentOnId]"
    
    if use_docket_filter:
        parts = document_id.rsplit('-', 1)
        if len(parts) == 2:
            search_id = parts[0]
            logging.info(f"Using docket ID: {search_id}")
            filter_param = "filter[docketId]"
    
    params = {
        filter_param: search_id,
        "page[size]": 250,
        "page[number]": 1,
        "sort": "-postedDate",
        "include": "attachments"
    }
    
    if posted_date_from:
        params["filter[postedDate][ge]"] = posted_date_from
    if posted_date_to:
        params["filter[postedDate][le]"] = posted_date_to
    
    all_comments = []
    page = 1
    
    while True:
        logging.info(f"Fetching page {page}...")
        params["page[number]"] = page
        
        try:
            response = requests.get(base_url, headers=headers, params=params)
            response.raise_for_status()
            
            data = response.json()
            comments = data.get("data", [])
            
            if not comments:
                break
            
            logging.info(f"Got {len(comments)} comments from page {page}")
            all_comments.extend(comments)
            
            if max_comments and len(all_comments) >= max_comments:
                all_comments = all_comments[:max_comments]
                break
            
            meta = data.get("meta", {})
            total_pages = meta.get("numberOfPages", 1)
            
            if page >= total_pages:
                break
            
            page += 1
            time.sleep(0.5)
            
        except requests.exceptions.RequestException as e:
            logging.error(f"Error fetching comments: {e}")
            break
    
    return all_comments


def extract_comment_text(comments: List[Dict], api_key: str) -> List[Dict]:
    """Extract comment text and metadata from API response"""
    extracted = []
    
    for idx, comment in enumerate(comments, 1):
        comment_id = comment.get("id")
        attributes = comment.get("attributes", {})
        comment_text = attributes.get("comment", "")
        
        attachments = []
        file_formats = attributes.get("fileFormats", [])
        if file_formats:
            for fmt in file_formats:
                attachments.append({
                    "fileUrl": fmt.get("fileUrl", ""),
                    "format": fmt.get("format", "")
                })
        
        extracted.append({
            "number": idx,
            "comment_id": comment_id,
            "posted_date": attributes.get("postedDate"),
            "title": attributes.get("title", ""),
            "comment": comment_text,
            "commenter_name": ((attributes.get("firstName") or "") + " " + (attributes.get("lastName") or "")).strip(),
            "organization": attributes.get("organization", ""),
            "has_attachments": len(attachments) > 0,
            "attachments": attachments
        })
    
    return extracted


# ============================================================================
# PHASE 2: CONSOLIDATE COMMENTS WITH ATTACHMENT TEXT
# ============================================================================

def download_file(url: str, api_key: str) -> Optional[bytes]:
    """Download a file and return its content as bytes"""
    try:
        headers = {
            "X-Api-Key": api_key,
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            "Accept": "application/pdf,application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,*/*",
            "Referer": "https://www.regulations.gov/"
        }
        response = requests.get(url, headers=headers, timeout=30, allow_redirects=True)
        response.raise_for_status()
        return response.content
    except requests.exceptions.RequestException as e:
        logging.error(f"Error downloading file: {e}")
        return None


def extract_text_from_pdf(pdf_content: bytes) -> Optional[str]:
    """Extract text from PDF bytes using PyPDF2"""
    try:
        pdf_file = io.BytesIO(pdf_content)
        reader = PyPDF2.PdfReader(pdf_file)
        text = ""
        for page in reader.pages:
            text += page.extract_text() + "\n\n"
        return text.strip()
    except Exception as e:
        logging.error(f"Error extracting text from PDF: {e}")
        return None


def extract_text_from_docx(docx_content: bytes) -> Optional[str]:
    """Extract text from DOCX bytes using python-docx"""
    try:
        docx_file = io.BytesIO(docx_content)
        doc = DocxDocument(docx_file)
        text = "\n\n".join([para.text for para in doc.paragraphs])
        return text.strip()
    except Exception as e:
        logging.error(f"Error extracting text from DOCX: {e}")
        return None


def get_comment_with_attachments(comment_id: str, api_key: str) -> Optional[Dict]:
    """Fetch full comment details including attachment URLs"""
    url = f"https://api.regulations.gov/v4/comments/{comment_id}"
    headers = {"X-Api-Key": api_key}
    params = {"include": "attachments"}
    
    try:
        response = requests.get(url, headers=headers, params=params)
        response.raise_for_status()
        return response.json()
    except requests.exceptions.RequestException as e:
        logging.error(f"Error fetching comment {comment_id}: {e}")
        return None


def consolidate_comments_to_csv(comments: List[Dict], api_key: str) -> List[Dict]:
    """Process comments and extract text from attachments"""
    csv_rows = []
    
    logging.info(f"Processing {len(comments)} comments...")
    
    for idx, comment in enumerate(comments, 1):
        comment_id = comment['comment_id']
        logging.info(f"[{idx}/{len(comments)}] Processing {comment_id}...")
        
        inline_text = comment.get('comment', '').strip()
        
        needs_attachments = (
            'attach' in inline_text.lower() or 
            'see attach' in inline_text.lower() or
            inline_text == "" or
            len(inline_text) < 100 or
            comment.get('has_attachments', False)
        )
        
        combined_text = inline_text if inline_text and 'attach' not in inline_text.lower() else ""
        attachment_info = []
        attachment_count = 0
        
        if needs_attachments:
            details = get_comment_with_attachments(comment_id, api_key)
            
            if details:
                included = details.get('included', [])
                attachments = [item for item in included if item.get('type') == 'attachments']
                
                if attachments:
                    logging.info(f"Found {len(attachments)} attachment(s)")
                    
                    for att_idx, attachment in enumerate(attachments, 1):
                        attrs = attachment.get('attributes', {})
                        title = attrs.get('title', f'attachment_{att_idx}')
                        
                        file_url = None
                        file_format = None
                        file_formats = attrs.get('fileFormats', [])
                        
                        if file_formats and len(file_formats) > 0:
                            first_format = file_formats[0]
                            file_url = first_format.get('fileUrl')
                            file_format = first_format.get('format', 'pdf')
                        
                        if not file_url:
                            attachment_info.append(f"[{title} - could not access]")
                            continue
                        
                        file_content = download_file(file_url, api_key)
                        
                        if file_content:
                            extracted_text = None
                            
                            if file_format == 'pdf':
                                extracted_text = extract_text_from_pdf(file_content)
                            elif file_format in ['docx', 'doc', 'msw12']:
                                extracted_text = extract_text_from_docx(file_content)
                            
                            if extracted_text:
                                attachment_count += 1
                                attachment_info.append(f"[{title}]")
                                combined_text += f"\n\n--- Attachment: {title} ---\n\n{extracted_text}"
                        
                        time.sleep(0.3)
        
        if not combined_text or combined_text.strip() == "":
            if attachment_info:
                combined_text = f"[Comment has {len(attachment_info)} attachment(s) but text extraction failed or files not accessible: {'; '.join(attachment_info)}]"
            else:
                combined_text = "[No text available]"
        
        csv_rows.append({
            'comment_number': comment['number'],
            'comment_id': comment_id,
            'posted_date': comment.get('posted_date', ''),
            'commenter_name': comment.get('commenter_name', ''),
            'organization': comment.get('organization', ''),
            'title': comment.get('title', ''),
            'has_attachments': attachment_count > 0,
            'attachment_titles': '; '.join(attachment_info),
            'comment_text': combined_text
        })
        
        time.sleep(0.5)
    
    return csv_rows


# ============================================================================
# PHASE 3 & 4: AI AGENT ANALYSIS
# ============================================================================

async def handle_streaming_intermediate_steps(message: ChatMessageContent) -> None:
    """Handle streaming intermediate steps from AI agent"""
    for item in message.items or []:
        if isinstance(item, FunctionResultContent):
            logging.info(f"Function Result: {item.result} for function: {item.name}")
        elif isinstance(item, FunctionCallContent):
            logging.info(f"Function Call: {item.name} with arguments: {item.arguments}")


async def categorize_with_agent(csv_rows: List[Dict], agent_id: str) -> List[Dict]:
    """Phase 3: Categorize each comment individually using AI agent"""
    categorizations = []
    
    async with (
        AsyncDefaultAzureCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        agent_definition = await client.agents.get_agent(agent_id=agent_id)
        safe_name = re.sub(r'[^0-9A-Za-z_]', '_', agent_definition.name or 'categorization_agent')
        agent = AzureAIAgent(client=client, definition=agent_definition, name=safe_name)
        thread: Optional[AgentThread] = None
        
        try:
            for idx, row in enumerate(csv_rows, 1):
                logging.info(f"Processing comment {idx}/{len(csv_rows)}")
                
                # Create row string for agent
                row_string = ','.join([str(v) for v in row.values()])
                
                full_response = ""
                async for response in agent.invoke_stream(
                    messages=row_string,
                    thread=thread,
                    on_intermediate_message=handle_streaming_intermediate_steps,
                ):
                    full_response += str(response)
                    thread = response.thread
                
                # Parse the categorization
                categorization_text = full_response.strip()
                if "```json" in categorization_text:
                    start = categorization_text.find("```json") + 7
                    end = categorization_text.find("```", start)
                    categorization_text = categorization_text[start:end].strip()
                elif "```" in categorization_text:
                    start = categorization_text.find("```") + 3
                    end = categorization_text.find("```", start)
                    categorization_text = categorization_text[start:end].strip()
                
                try:
                    categorization_json = json.loads(categorization_text)
                except:
                    categorization_json = categorization_text
                
                categorizations.append({
                    "submission_number": idx,
                    "csv_row": idx,
                    "row_data": row_string,
                    "categorization": categorization_json
                })
        
        finally:
            pass
    
    return categorizations


async def group_categorizations(categorizations: List[Dict], agent_id: str, batch_size: int = 5) -> Any:
    """Phase 4: Analyze categorizations in batches and group similar comments"""
    logging.info(f"Grouping {len(categorizations)} categorizations with batch size {batch_size}")
    
    total_comments = len(categorizations)
    
    async with (
        AsyncDefaultAzureCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        agent_definition = await client.agents.get_agent(agent_id=agent_id)
        safe_name = re.sub(r'[^0-9A-Za-z_]', '_', agent_definition.name or 'grouping_agent')
        agent = AzureAIAgent(client=client, definition=agent_definition, name=safe_name)
        thread: Optional[AgentThread] = None
        
        try:
            final_analysis = ""
            
            for batch_num in range(0, total_comments, batch_size):
                batch = categorizations[batch_num:batch_num + batch_size]
                batch_index = batch_num // batch_size + 1
                
                logging.info(f"Processing batch {batch_index} (Comments {batch_num + 1}-{min(batch_num + batch_size, total_comments)})")
                
                if batch_index == 1:
                    message = f"I will show you categorized public comments in batches of {batch_size}. Please remember all comments as I show them to you. After all batches, I will ask for your collective analysis.\n\nBatch {batch_index}:\n\n"
                else:
                    message = f"Batch {batch_index}:\n\n"
                
                for cat in batch:
                    message += f"--- Submission {cat['submission_number']} (CSV Row {cat['csv_row']}) ---\n"
                    message += f"{cat['categorization']}\n\n"
                
                is_last_batch = batch_num + batch_size >= total_comments
                
                if is_last_batch:
                    message += f"\nThat was the final batch. You've now seen all {total_comments} comments. Please provide your collective analysis in the JSON format specified in your instructions."
                else:
                    message += "\nAcknowledge receipt. More batches coming..."
                
                batch_response = ""
                async for response in agent.invoke_stream(
                    messages=message,
                    thread=thread,
                    on_intermediate_message=handle_streaming_intermediate_steps,
                ):
                    batch_response += str(response)
                    thread = response.thread
                
                if is_last_batch:
                    final_analysis = batch_response
        
        finally:
            pass
    
    # Parse the analysis
    analysis_text = final_analysis.strip()
    if "```json" in analysis_text:
        start = analysis_text.find("```json") + 7
        end = analysis_text.find("```", start)
        analysis_text = analysis_text[start:end].strip()
    elif "```" in analysis_text:
        start = analysis_text.find("```") + 3
        end = analysis_text.find("```", start)
        analysis_text = analysis_text[start:end].strip()
    
    try:
        parsed_analysis = json.loads(analysis_text)
    except Exception as e:
        logging.warning(f"Could not parse JSON: {e}")
        parsed_analysis = None
    
    return parsed_analysis if parsed_analysis else analysis_text


# ============================================================================
# ANALYSIS FORMATTING HELPERS
# ============================================================================

def convert_grouped_analysis_to_csv(grouped_data: Dict) -> str:
    """Convert grouped analysis JSON to user-friendly CSV format"""
    output = io.StringIO()
    
    # Extract the collective analysis
    analysis = grouped_data.get('collective_analysis', {})
    
    # If analysis is a string (unparsed), try to parse it
    if isinstance(analysis, str):
        try:
            analysis = json.loads(analysis)
        except:
            # If can't parse, create simple summary CSV
            writer = csv.writer(output)
            writer.writerow(['Analysis Summary'])
            writer.writerow([analysis])
            return output.getvalue()
    
    writer = csv.writer(output)
    
    # Header information
    writer.writerow(['Regulatory Comments Analysis Report'])
    writer.writerow(['Generated:', grouped_data.get('timestamp', '')])
    writer.writerow(['Total Comments Analyzed:', grouped_data.get('total_comments_analyzed', 0)])
    writer.writerow(['Source File:', grouped_data.get('source_csv_file', '')])
    writer.writerow([])  # Blank row
    
    # Overall Summary
    if 'overall_summary' in analysis:
        writer.writerow(['OVERALL SUMMARY'])
        writer.writerow([analysis['overall_summary']])
        writer.writerow([])  # Blank row
    
    # Theme Groups
    if 'theme_groups' in analysis:
        writer.writerow(['THEME GROUPS'])
        writer.writerow([])  # Blank row
        writer.writerow(['Group Name', 'Description', 'Comment Count', 'Comment IDs', 'Stance', 'Key Arguments'])
        
        for group in analysis['theme_groups']:
            group_name = group.get('group_name', '')
            description = group.get('group_description', '')
            count = group.get('count', 0)
            submissions = ', '.join(map(str, group.get('submission_numbers', [])))
            
            # Format stance distribution
            stance_dist = group.get('stance_distribution', {})
            stance_str = ', '.join([f"{k}: {v}" for k, v in stance_dist.items()]) if stance_dist else ''
            
            # Format key arguments
            arguments = group.get('common_arguments', [])
            arguments_str = ' | '.join(arguments) if arguments else ''
            
            writer.writerow([group_name, description, count, submissions, stance_str, arguments_str])
        
        writer.writerow([])  # Blank row
    
    # Key Patterns
    if 'patterns' in analysis:
        writer.writerow(['KEY PATTERNS IDENTIFIED'])
        writer.writerow([])  # Blank row
        for i, pattern in enumerate(analysis['patterns'], 1):
            writer.writerow([f"{i}.", pattern])
        writer.writerow([])  # Blank row
    
    # Recommendations (if present)
    if 'recommendations' in analysis:
        writer.writerow(['RECOMMENDATIONS'])
        writer.writerow([])  # Blank row
        for i, rec in enumerate(analysis['recommendations'], 1):
            writer.writerow([f"{i}.", rec])
        writer.writerow([])  # Blank row
    
    # Overall Sentiment (if present)
    if 'overall_sentiment' in analysis:
        writer.writerow(['OVERALL SENTIMENT'])
        writer.writerow([analysis['overall_sentiment']])
        writer.writerow([])  # Blank row
    
    return output.getvalue()


def generate_run_summary(
    document_id: str,
    timestamp: str,
    comments_fetched: int,
    comments_processed: int,
    batch_size: int,
    categorizations: List[Dict],
    grouped_analysis,
    csv_rows: List[Dict],
) -> str:
    """Generate a human-readable run summary covering both agents' output."""
    run_dt = datetime.datetime.utcnow().strftime("%B %d, %Y at %H:%M UTC")
    sep = "=" * 60
    thin = "-" * 60

    lines = [
        sep,
        "REGULATORY COMMENTS PROCESSING - RUN SUMMARY",
        sep,
        "",
        f"Run completed:        {run_dt}",
        f"Document:             {document_id}",
        f"Comments fetched:     {comments_fetched}",
        f"Comments processed:   {comments_processed}",
        f"Batch size:           {batch_size}",
    ]

    # ------------------------------------------------------------------
    # AGENT 1 — Individual Comment Categorizations
    # ------------------------------------------------------------------
    lines += [
        "",
        sep,
        "AGENT 1: INDIVIDUAL COMMENT CATEGORIZATIONS",
        sep,
        "Each comment was analyzed for topic, sentiment, and key concerns.",
        "",
    ]

    # Tally counters
    topic_counts: Dict[str, int] = {}
    sentiment_counts: Dict[str, int] = {}
    type_counts: Dict[str, int] = {}

    for cat_entry in categorizations:
        cat = cat_entry.get("categorization", {})
        # Handle both parsed-dict and raw-string categorizations
        if isinstance(cat, str):
            try:
                cat = json.loads(cat)
            except Exception:
                cat = {}
        if not isinstance(cat, dict):
            cat = {}

        sub_num = cat_entry.get("submission_number", "?")

        # Try common field names the categorization agent may return
        commenter = cat.get("commenter_name") or cat.get("commenter") or ""
        commenter_type = cat.get("commenter_type") or cat.get("type") or ""
        topic = cat.get("primary_topic") or cat.get("topic") or cat.get("category") or ""
        sentiment = cat.get("sentiment") or cat.get("overall_sentiment") or ""
        key_concerns = cat.get("key_concerns") or cat.get("concerns") or cat.get("key_arguments") or []

        # Fallback commenter name from csv_rows if agent didn't return one
        if not commenter and sub_num != "?" and isinstance(sub_num, int) and sub_num <= len(csv_rows):
            row = csv_rows[sub_num - 1]
            commenter = row.get("commenter_name", "")

        # Build display name
        display_name = commenter
        if commenter_type:
            display_name = f"{commenter} ({commenter_type})" if commenter else f"({commenter_type})"

        # Format concerns
        if isinstance(key_concerns, list):
            concerns_str = "; ".join(str(c) for c in key_concerns)
        else:
            concerns_str = str(key_concerns)

        lines.append(f"Comment #{sub_num}")
        if display_name:
            lines.append(f"  Commenter:     {display_name}")
        if topic:
            lines.append(f"  Topic:         {topic}")
        if sentiment:
            lines.append(f"  Sentiment:     {sentiment}")
        if concerns_str:
            lines.append(f"  Key concerns:  {concerns_str}")
        lines.append("")

        # Tallies
        if topic:
            topic_counts[topic] = topic_counts.get(topic, 0) + 1
        if sentiment:
            sentiment_counts[sentiment] = sentiment_counts.get(sentiment, 0) + 1
        if commenter_type:
            type_counts[commenter_type] = type_counts.get(commenter_type, 0) + 1

    # Categorization breakdown
    lines.append(thin)
    lines.append("Categorization breakdown:")

    if topic_counts:
        sorted_topics = sorted(topic_counts.items(), key=lambda x: x[1], reverse=True)
        topics_str = ", ".join(f"{t} ({c})" for t, c in sorted_topics)
        lines.append(f"  Topics:      {topics_str}")

    if sentiment_counts:
        sentiment_str = " | ".join(f"{s}: {c}" for s, c in sentiment_counts.items())
        lines.append(f"  Sentiment:   {sentiment_str}")

    if type_counts:
        type_str = " | ".join(f"{t}s: {c}" for t, c in type_counts.items())
        lines.append(f"  Commenters:  {type_str}")

    lines.append(thin)

    # ------------------------------------------------------------------
    # AGENT 2 — Grouped Analysis & Themes
    # ------------------------------------------------------------------
    lines += [
        "",
        sep,
        "AGENT 2: GROUPED ANALYSIS & THEMES",
        sep,
        "Comments were analyzed in batches and grouped by theme.",
        "",
    ]

    analysis = grouped_analysis
    if isinstance(analysis, str):
        try:
            analysis = json.loads(analysis)
        except Exception:
            pass

    if isinstance(analysis, dict):
        # Overall summary
        if "overall_summary" in analysis:
            lines += ["OVERALL SUMMARY:", analysis["overall_summary"], ""]

        # Theme groups
        if "theme_groups" in analysis:
            lines.append("THEME GROUPS:")
            lines.append(thin)
            for i, group in enumerate(analysis["theme_groups"], 1):
                name = group.get("group_name", f"Group {i}")
                count = group.get("count", 0)
                subs = group.get("submission_numbers", [])
                subs_str = ", ".join(f"#{s}" for s in subs[:8])
                if len(subs) > 8:
                    subs_str += ", ..."

                lines.append(f"{i}. {name}  ({count} comments: {subs_str})")

                # Stance distribution
                stance = group.get("stance_distribution", {})
                if stance:
                    stance_str = " | ".join(f"{k}: {v}" for k, v in stance.items())
                    lines.append(f"   Stance:  {stance_str}")

                desc = group.get("group_description", "")
                if desc:
                    lines.append(f"   Description: {desc}")

                args = group.get("common_arguments", [])
                if args:
                    lines.append("   Key arguments:")
                    for arg in args:
                        lines.append(f"     - {arg}")

                lines.append("")

            lines.append(thin)

        # Key patterns
        if "patterns" in analysis:
            lines.append("")
            lines.append("KEY PATTERNS:")
            for i, pattern in enumerate(analysis["patterns"], 1):
                lines.append(f"  {i}. {pattern}")

        # Overall sentiment
        if "overall_sentiment" in analysis:
            lines += ["", "OVERALL SENTIMENT:", f"  {analysis['overall_sentiment']}"]

        # Recommendations
        if "recommendations" in analysis:
            lines += ["", "RECOMMENDATIONS:"]
            for i, rec in enumerate(analysis["recommendations"], 1):
                lines.append(f"  {i}. {rec}")
    else:
        # Couldn't parse — include raw text
        lines.append(str(analysis))

    # ------------------------------------------------------------------
    # File index
    # ------------------------------------------------------------------
    lines += [
        "",
        sep,
        f"All outputs stored in: regulatory-comments/{timestamp}",
        f"  - 1_fetch/comments_raw_{timestamp}.json",
        f"  - 1_fetch/comments_extracted_{timestamp}.json",
        f"  - 2_consolidate/comments_consolidated_{timestamp}.csv",
        f"  - 3_analysis/categorizations_{timestamp}.json",
        f"  - 3_analysis/grouped_analysis_{timestamp}.json",
        f"  - 3_analysis/grouped_analysis_{timestamp}.csv",
        f"  - 4_summary/run_summary_{timestamp}.txt",
        sep,
    ]

    return "\n".join(lines)


# ============================================================================
# AZURE STORAGE HELPERS
# ============================================================================

def upload_to_blob(content: str, blob_name: str, storage_account_name: str, container_name: str = "regulatory-comments") -> str:
    """Upload content to Azure Blob Storage using managed identity"""
    try:
        # Use DefaultAzureCredential for authentication
        # Locally: Uses az login credentials
        # In Azure: Uses managed identity
        credential = DefaultAzureCredential()
        
        blob_service_client = BlobServiceClient(
            account_url=f"https://{storage_account_name}.blob.core.windows.net",
            credential=credential
        )
        container_client = blob_service_client.get_container_client(container_name)
        
        # Create container if it doesn't exist
        try:
            container_client.create_container()
        except:
            pass  # Container already exists
        
        blob_client = container_client.get_blob_client(blob_name)
        blob_client.upload_blob(content, overwrite=True)
        
        return f"https://{storage_account_name}.blob.core.windows.net/{container_name}/{blob_name}"
    except Exception as e:
        logging.error(f"Error uploading to blob: {e}")
        raise


# ============================================================================
# MAIN TIMER FUNCTION
# ============================================================================

@app.schedule(schedule="0 0 8 * * *", arg_name="myTimer", run_on_startup=True,
              use_monitor=False) 
def regulatory_comments_daily(myTimer: func.TimerRequest) -> None:
    """
    Azure Function that runs daily at 3AM EST (8AM UTC) to fetch and analyze regulatory comments.
    
    Complete workflow:
    1. Fetch comments from Regulations.gov API
    2. Extract text from attachments
    3. Consolidate into CSV format
    4. Categorize with AI agent
    5. Group and analyze with AI agent
    6. Save all outputs to Azure Blob Storage
    """
    if myTimer.past_due:
        logging.info('The timer is past due!')
    
    logging.info('Starting regulatory comments processing workflow...')
    timestamp = datetime.datetime.utcnow().strftime("%Y%m%d_%H%M%S")
    
    # Get configuration from environment variables
    api_key = os.environ.get("REGULATIONS_GOV_API_KEY")
    document_id = os.environ.get("DOCUMENT_ID", "ED-2025-SCC-0481-0001")
    # Agent IDs MUST be set via app settings / local.settings.json — no real defaults in source.
    categorization_agent_id = os.environ.get("CATEGORIZATION_AGENT_ID", "")
    grouping_agent_id = os.environ.get("GROUPING_AGENT_ID", "")
    if not categorization_agent_id or not grouping_agent_id:
        logging.error("CATEGORIZATION_AGENT_ID and GROUPING_AGENT_ID must be set in app settings.")
        return
    batch_size = int(os.environ.get("BATCH_SIZE", "5"))
    max_comments_str = os.environ.get("MAX_COMMENTS")
    max_comments = int(max_comments_str) if max_comments_str else None
    storage_account_name = os.environ.get("AZURE_STORAGE_ACCOUNT_NAME")
    
    if not api_key:
        logging.error("REGULATIONS_GOV_API_KEY not found in environment variables")
        return
    
    if not storage_account_name:
        logging.error("AZURE_STORAGE_ACCOUNT_NAME not found in environment variables")
        return
    
    try:
        # Phase 1: Fetch comments
        if max_comments:
            logging.info(f"Phase 1: Fetching up to {max_comments} comments for document {document_id}")
        else:
            logging.info(f"Phase 1: Fetching all comments for document {document_id}")
        
        comments = fetch_comments_from_api(document_id, api_key, max_comments=max_comments)
        
        if not comments:
            logging.warning("No comments found. Trying with docket filter...")
            comments = fetch_comments_from_api(document_id, api_key, max_comments=max_comments, use_docket_filter=True)
        
        if not comments:
            logging.error("No comments found with either method")
            return
        
        logging.info(f"Fetched {len(comments)} comments")
        
        # Save raw comments
        raw_comments_json = json.dumps(comments, indent=2)
        upload_to_blob(raw_comments_json, f"1_fetch/comments_raw_{timestamp}.json", storage_account_name)
        
        # Extract comment text
        extracted_comments = extract_comment_text(comments, api_key)
        extracted_json = json.dumps(extracted_comments, indent=2)
        upload_to_blob(extracted_json, f"1_fetch/comments_extracted_{timestamp}.json", storage_account_name)
        
        # Phase 2: Consolidate with attachments
        logging.info("Phase 2: Consolidating comments with attachment text")
        csv_rows = consolidate_comments_to_csv(extracted_comments, api_key)
        
        # Convert CSV rows to CSV format
        output = io.StringIO()
        fieldnames = ['comment_number', 'comment_id', 'posted_date', 'commenter_name', 
                     'organization', 'title', 'has_attachments', 'attachment_titles', 'comment_text']
        writer = csv.DictWriter(output, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(csv_rows)
        csv_content = output.getvalue()
        
        upload_to_blob(csv_content, f"2_consolidate/comments_consolidated_{timestamp}.csv", storage_account_name)
        logging.info(f"Consolidated {len(csv_rows)} comments")
        
        # Phase 3: Categorize with AI agent
        logging.info("Phase 3: Categorizing comments with AI agent")
        
        async def run_categorization():
            categorizations = await categorize_with_agent(csv_rows, categorization_agent_id)
            return categorizations
        
        categorizations = asyncio.run(run_categorization())
        
        categorizations_data = {
            "source_csv_file": f"comments_consolidated_{timestamp}.csv",
            "timestamp": timestamp,
            "total_comments": len(categorizations),
            "categorizations": categorizations
        }
        categorizations_json = json.dumps(categorizations_data, indent=2)
        upload_to_blob(categorizations_json, f"3_analysis/categorizations_{timestamp}.json", storage_account_name)
        
        # Phase 4: Group and analyze
        logging.info("Phase 4: Grouping and analyzing with AI agent")
        
        async def run_grouping():
            analysis = await group_categorizations(categorizations, grouping_agent_id, batch_size)
            return analysis
        
        grouped_analysis = asyncio.run(run_grouping())
        
        grouped_data = {
            "phase": "2_grouping_analysis",
            "timestamp": timestamp,
            "source_csv_file": f"comments_consolidated_{timestamp}.csv",
            "source_categorization_file": f"categorizations_{timestamp}.json",
            "total_comments_analyzed": len(categorizations),
            "batch_size": batch_size,
            "collective_analysis": grouped_analysis
        }
        
        # Save JSON version (for technical users/processing)
        grouped_json = json.dumps(grouped_data, indent=2)
        upload_to_blob(grouped_json, f"3_analysis/grouped_analysis_{timestamp}.json", storage_account_name)
        
        # Save CSV version (for non-technical end users)
        grouped_csv = convert_grouped_analysis_to_csv(grouped_data)
        upload_to_blob(grouped_csv, f"3_analysis/grouped_analysis_{timestamp}.csv", storage_account_name)
        logging.info(f"Saved analysis in both JSON and CSV formats")
        
        # Phase 5: Generate and upload human-readable run summary
        logging.info("Phase 5: Generating human-readable run summary")
        run_summary = generate_run_summary(
            document_id=document_id,
            timestamp=timestamp,
            comments_fetched=len(comments),
            comments_processed=len(csv_rows),
            batch_size=batch_size,
            categorizations=categorizations,
            grouped_analysis=grouped_analysis,
            csv_rows=csv_rows,
        )
        upload_to_blob(run_summary, f"4_summary/run_summary_{timestamp}.txt", storage_account_name)
        logging.info(f"Uploaded run summary to 4_summary/run_summary_{timestamp}.txt")

        logging.info(f"Workflow completed successfully! Processed {len(csv_rows)} comments")
        logging.info(f"All outputs saved to Azure Blob Storage with timestamp {timestamp}")

    except Exception as e:
        logging.error(f"Error in workflow: {e}", exc_info=True)
        raise