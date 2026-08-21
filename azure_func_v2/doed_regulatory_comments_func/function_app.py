import azure.functions as func
import datetime
import json
import logging
import os
import random
import time
import csv
import io
import asyncio
import uuid
from typing import List, Dict, Any, Optional, Tuple
import requests
from agent_framework import AgentSession
from agent_framework.foundry import FoundryAgent, FoundryAgentOptions
from azure.ai.documentintelligence import DocumentIntelligenceClient
from azure.core.credentials import AzureKeyCredential
from azure.storage.blob import BlobServiceClient
from azure.identity import DefaultAzureCredential
from analysis_requests import AnalysisRequestValidationError, create_analysis_request
from cosmos_runs import (
    CosmosRunStore,
    build_analysis_document,
    build_failed_analysis_document,
    build_job_document,
    derive_overall_sentiment,
    utc_now,
)

app = func.FunctionApp()

_document_intelligence_client: Optional[DocumentIntelligenceClient] = None
_blob_service_client: Optional[BlobServiceClient] = None
_cosmos_run_store: Optional[CosmosRunStore] = None
_cosmos_run_store_initialized = False
_FOUNDRY_AGENT_TIMEOUT_SECONDS = int(os.environ.get("FOUNDRY_AGENT_TIMEOUT_SECONDS", "180"))
_FOUNDRY_THROTTLE_MAX_ATTEMPTS = max(1, int(os.environ.get("FOUNDRY_THROTTLE_MAX_ATTEMPTS", "5")))
_FOUNDRY_THROTTLE_BASE_DELAY_SECONDS = max(
    0.1,
    float(os.environ.get("FOUNDRY_THROTTLE_BASE_DELAY_SECONDS", "60")),
)
_FOUNDRY_THROTTLE_MAX_DELAY_SECONDS = max(
    _FOUNDRY_THROTTLE_BASE_DELAY_SECONDS,
    float(os.environ.get("FOUNDRY_THROTTLE_MAX_DELAY_SECONDS", "120")),
)
_FOUNDRY_CALL_SEMAPHORE = asyncio.Semaphore(
    max(1, int(os.environ.get("FOUNDRY_MAX_CONCURRENT_CALLS", "1")))
)


def get_document_intelligence_client() -> DocumentIntelligenceClient:
    """Create and cache an Azure Document Intelligence client."""
    global _document_intelligence_client

    if _document_intelligence_client is not None:
        return _document_intelligence_client

    endpoint = (
        os.environ.get("DOCUMENTINTELLIGENCE_ENDPOINT")
        or os.environ.get("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT")
    )
    if not endpoint:
        raise ValueError(
            "DOCUMENTINTELLIGENCE_ENDPOINT or AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT must be configured"
        )

    api_key = (
        os.environ.get("DOCUMENTINTELLIGENCE_API_KEY")
        or os.environ.get("AZURE_DOCUMENT_INTELLIGENCE_API_KEY")
    )

    if api_key:
        credential = AzureKeyCredential(api_key)
    else:
        credential = DefaultAzureCredential()

    _document_intelligence_client = DocumentIntelligenceClient(
        endpoint=endpoint,
        credential=credential,
    )
    return _document_intelligence_client

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


def extract_text_with_document_intelligence(file_content: bytes, content_type: str, file_label: str) -> Optional[str]:
    """Extract text from supported documents using Azure Document Intelligence."""
    try:
        client = get_document_intelligence_client()
        poller = client.begin_analyze_document(
            "prebuilt-read",
            body=io.BytesIO(file_content),
            content_type=content_type,
        )
        result = poller.result()
        extracted_text = (result.content or "").strip()
        return extracted_text or None
    except Exception as e:
        logging.error(f"Error extracting text from {file_label} with Azure Document Intelligence: {e}")
        return None


def extract_text_from_pdf(pdf_content: bytes) -> Optional[str]:
    """Extract text from PDF bytes using Azure Document Intelligence."""
    return extract_text_with_document_intelligence(pdf_content, "application/pdf", "PDF")


def extract_text_from_docx(docx_content: bytes) -> Optional[str]:
    """Extract text from DOCX bytes using Azure Document Intelligence."""
    return extract_text_with_document_intelligence(
        docx_content,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "DOCX",
    )


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
                            elif file_format in ['docx', 'msw12']:
                                extracted_text = extract_text_from_docx(file_content)
                            elif file_format == 'doc':
                                logging.warning(
                                    "Skipping legacy .doc attachment '%s'; Azure Document Intelligence read model supports DOCX, not binary .doc",
                                    title,
                                )
                            
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

def create_foundry_agent(
    agent_name: str,
    agent_version: Optional[str] = None,
    agent_model: Optional[str] = None,
) -> FoundryAgent:
    """Create a Foundry agent using the Microsoft Agent Framework."""
    project_endpoint = (
        os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
        or os.environ.get("AZURE_AI_AGENT_ENDPOINT")
        or os.environ.get("AZURE_AI_PROJECT_ENDPOINT")
    )
    if not project_endpoint:
        raise ValueError("FOUNDRY_PROJECT_ENDPOINT or AZURE_AI_AGENT_ENDPOINT must be configured")

    default_options = None
    if agent_model:
        default_options = FoundryAgentOptions(model=agent_model)

    return FoundryAgent(
        project_endpoint=project_endpoint,
        agent_name=agent_name,
        agent_version=agent_version,
        credential=DefaultAzureCredential(),
        allow_preview=True,
        default_options=default_options,
    )


def extract_text_from_foundry_response(final_response: Any) -> str:
    """Extract text content from a final Agent Framework response."""
    parts: List[str] = []

    for message in getattr(final_response, "messages", []) or []:
        for content in getattr(message, "contents", []) or []:
            text = getattr(content, "text", None)
            if text:
                parts.append(text)

    return "".join(parts).strip()


async def _run_foundry_agent_once(agent: FoundryAgent, session: AgentSession, message: str) -> str:
    """Run one Foundry agent attempt with a timeout."""
    async def _run() -> str:
        stream = agent.run(message, session=session, stream=True)
        streamed_parts: List[str] = []
        async for chunk in stream:
            if chunk.text:
                streamed_parts.append(chunk.text)
        final_response = await stream.get_final_response()
        streamed_text = "".join(streamed_parts).strip()
        if streamed_text:
            return streamed_text
        return extract_text_from_foundry_response(final_response)

    try:
        return await asyncio.wait_for(_run(), timeout=_FOUNDRY_AGENT_TIMEOUT_SECONDS)
    except asyncio.TimeoutError:
        logging.error(
            "Foundry agent call timed out after %s seconds",
            _FOUNDRY_AGENT_TIMEOUT_SECONDS,
        )
        raise


def _exception_chain(error: Exception):
    current: Optional[BaseException] = error
    seen = set()
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        yield current
        current = current.__cause__ or current.__context__


def _is_foundry_throttling_error(error: Exception) -> bool:
    throttling_markers = (
        "rate limit",
        "too many requests",
        "tokens per minute",
        "requests per minute",
        "tpm",
        "rpm",
    )
    for candidate in _exception_chain(error):
        response = getattr(candidate, "response", None)
        status_code = getattr(candidate, "status_code", None) or getattr(response, "status_code", None)
        if status_code == 429:
            return True
        error_code = str(getattr(candidate, "code", "") or "").lower()
        if error_code in {"429", "rate_limit_exceeded", "too_many_requests"}:
            return True
        message = str(candidate).lower()
        if any(marker in message for marker in throttling_markers):
            return True
    return False


def _retry_after_seconds(error: Exception) -> Optional[float]:
    for candidate in _exception_chain(error):
        response = getattr(candidate, "response", None)
        headers = getattr(response, "headers", None) or getattr(candidate, "headers", None)
        if not headers:
            continue
        normalized_headers = {str(name).lower(): value for name, value in headers.items()}

        for header_name in ("retry-after-ms", "x-ms-retry-after-ms"):
            try:
                milliseconds = float(normalized_headers.get(header_name))
                if milliseconds > 0:
                    return milliseconds / 1000
            except (TypeError, ValueError):
                pass

        try:
            seconds = float(normalized_headers.get("retry-after"))
            if seconds > 0:
                return seconds
        except (TypeError, ValueError):
            pass
    return None


def _foundry_throttle_delay(error: Exception, attempt: int) -> float:
    retry_after = _retry_after_seconds(error)
    if retry_after is not None:
        return min(retry_after, _FOUNDRY_THROTTLE_MAX_DELAY_SECONDS)

    exponential_delay = min(
        _FOUNDRY_THROTTLE_BASE_DELAY_SECONDS * (2 ** max(0, attempt - 1)),
        _FOUNDRY_THROTTLE_MAX_DELAY_SECONDS,
    )
    jittered_delay = exponential_delay * random.uniform(1.0, 1.25)
    return min(
        max(_FOUNDRY_THROTTLE_BASE_DELAY_SECONDS, jittered_delay),
        _FOUNDRY_THROTTLE_MAX_DELAY_SECONDS,
    )


async def run_foundry_agent(agent: FoundryAgent, session: AgentSession, message: str) -> str:
    """Run a Foundry agent, backing off when the service throttles TPM or RPM."""
    async with _FOUNDRY_CALL_SEMAPHORE:
        for attempt in range(1, _FOUNDRY_THROTTLE_MAX_ATTEMPTS + 1):
            try:
                return await _run_foundry_agent_once(agent, session, message)
            except asyncio.CancelledError:
                raise
            except Exception as error:
                if not _is_foundry_throttling_error(error) or attempt >= _FOUNDRY_THROTTLE_MAX_ATTEMPTS:
                    raise

                delay = _foundry_throttle_delay(error, attempt)
                logging.warning(
                    "Foundry throttled the request (attempt %s/%s); retrying in %.1f seconds.",
                    attempt,
                    _FOUNDRY_THROTTLE_MAX_ATTEMPTS,
                    delay,
                )
                await asyncio.sleep(delay)

    raise RuntimeError("Foundry retry loop ended without a result")

def validate_and_normalize_grouped_analysis(analysis: Dict[str, Any], expected_total_comments: int) -> Dict[str, Any]:
    """Validate grouped analysis structure without changing agent-produced semantics."""
    if not isinstance(analysis, dict):
        raise ValueError("Grouped analysis must be a JSON object")

    categories = analysis.get("categories")
    if not isinstance(categories, list) or not categories:
        raise ValueError("Grouped analysis must include a non-empty 'categories' array")

    all_submission_numbers: List[int] = []
    all_csv_rows: List[int] = []

    for index, category in enumerate(categories, start=1):
        if not isinstance(category, dict):
            raise ValueError(f"Category {index} must be a JSON object")

        submission_numbers = category.get("submission_numbers") or []
        csv_rows = category.get("csv_rows") or []

        if not isinstance(submission_numbers, list) or not isinstance(csv_rows, list):
            raise ValueError(f"Category {index} must include list values for 'submission_numbers' and 'csv_rows'")

        if len(submission_numbers) != len(csv_rows):
            raise ValueError(
                f"Category {index} has mismatched submission_numbers ({len(submission_numbers)}) "
                f"and csv_rows ({len(csv_rows)})"
            )

        expected_count = len(submission_numbers)
        actual_count = category.get("comment_count")
        if actual_count != expected_count:
            logging.warning(
                "Category %s had comment_count=%s but %s listed submissions; normalizing count",
                index,
                actual_count,
                expected_count,
            )
            category["comment_count"] = expected_count

        all_submission_numbers.extend(submission_numbers)
        all_csv_rows.extend(csv_rows)

    if len(all_submission_numbers) != len(set(all_submission_numbers)):
        raise ValueError("Grouped analysis contains duplicate submission_numbers across categories")

    if len(all_csv_rows) != len(set(all_csv_rows)):
        raise ValueError("Grouped analysis contains duplicate csv_rows across categories")

    if sorted(all_submission_numbers) != list(range(1, expected_total_comments + 1)):
        raise ValueError(
            "Grouped analysis submission_numbers do not cover the expected set of comments "
            f"1..{expected_total_comments}"
        )

    total_from_categories = sum(category["comment_count"] for category in categories)
    if total_from_categories != expected_total_comments:
        raise ValueError(
            f"Grouped analysis category counts sum to {total_from_categories}, expected {expected_total_comments}"
        )

    if analysis.get("total_comments") != expected_total_comments:
        logging.warning(
            "Grouped analysis total_comments=%s but expected %s; normalizing total_comments",
            analysis.get("total_comments"),
            expected_total_comments,
        )
        analysis["total_comments"] = expected_total_comments

    if analysis.get("total_categories") != len(categories):
        logging.warning(
            "Grouped analysis total_categories=%s but found %s categories; normalizing total_categories",
            analysis.get("total_categories"),
            len(categories),
        )
        analysis["total_categories"] = len(categories)

    derived_sentiment = derive_overall_sentiment(analysis)
    if derived_sentiment is not None:
        analysis["overall_sentiment"] = derived_sentiment

    return analysis


def extract_json_payload(response_text: str) -> str:
    """Extract JSON content from a model response that may include markdown fences."""
    cleaned_text = response_text.strip()
    if "```json" in cleaned_text:
        start = cleaned_text.find("```json") + 7
        end = cleaned_text.find("```", start)
        cleaned_text = cleaned_text[start:end].strip()
    elif "```" in cleaned_text:
        start = cleaned_text.find("```") + 3
        end = cleaned_text.find("```", start)
        cleaned_text = cleaned_text[start:end].strip()

    return cleaned_text


def is_non_substantive_comment_text(comment_text: str) -> bool:
    """Return True when a comment has no evaluable policy content."""
    normalized_text = (comment_text or "").strip().lower()

    if not normalized_text:
        return True

    non_substantive_markers = [
        "[no text available]",
        "[comment has",
    ]

    return any(normalized_text.startswith(marker) for marker in non_substantive_markers)


def is_refusal_or_error_response(response_text: str) -> bool:
    """Return True for obvious refusal/error strings that should not flow into grouped analysis."""
    normalized_text = (response_text or "").strip().lower()

    refusal_markers = [
        "i'm sorry, but i cannot assist with that request.",
        "sorry, i can't assist with that.",
        "sorry, i cannot assist with that.",
        "i cannot assist with that request.",
        "i can't assist with that request.",
    ]

    return normalized_text in refusal_markers


def build_non_substantive_categorization(reason: str) -> Dict[str, Any]:
    """Create a stable categorization object for non-substantive or unusable inputs."""
    return {
        "proposed_change_summary": "No substantive comment content was available to evaluate against the proposal.",
        "stance": "procedural",
        "stance_confidence": 1.0,
        "stance_reasoning_check": "The submission does not contain evaluable policy content that can be classified as supporting or opposing the proposal.",
        "stance_breakdown": {
            "supporting_aspects": [],
            "opposing_aspects": [],
        },
        "canonical_reason": "No substantive input",
        "rationale": reason,
        "reason_span": "No primary policy reason could be identified because the submission did not contain usable substantive content.",
        "key_phrases": [],
        "primary_theme": "Non-substantive submission handling",
        "secondary_themes": [],
        "comment_summary": "No substantive comment content available for analysis.",
        "search_queries_used": [],
        "doed_framework_applied": "Non-substantive comment handling",
        "search_confidence": 1.0,
    }


def summarize_grouped_analysis_structure_issues(analysis: Dict[str, Any], expected_total_comments: int) -> str:
    """Summarize duplicate/missing coverage problems to help the agent repair invalid grouped JSON."""
    if not isinstance(analysis, dict):
        return "- Analysis is not a JSON object."

    categories = analysis.get("categories")
    if not isinstance(categories, list):
        return "- Analysis does not contain a valid 'categories' array."

    expected_ids = set(range(1, expected_total_comments + 1))
    submission_counts: Dict[int, int] = {}
    csv_row_counts: Dict[int, int] = {}

    for category in categories:
        if not isinstance(category, dict):
            continue

        for submission_number in category.get("submission_numbers") or []:
            if isinstance(submission_number, int):
                submission_counts[submission_number] = submission_counts.get(submission_number, 0) + 1

        for csv_row in category.get("csv_rows") or []:
            if isinstance(csv_row, int):
                csv_row_counts[csv_row] = csv_row_counts.get(csv_row, 0) + 1

    duplicate_submissions = sorted(number for number, count in submission_counts.items() if count > 1)
    missing_submissions = sorted(expected_ids - set(submission_counts.keys()))
    duplicate_csv_rows = sorted(number for number, count in csv_row_counts.items() if count > 1)
    missing_csv_rows = sorted(expected_ids - set(csv_row_counts.keys()))

    issue_lines = []
    if duplicate_submissions:
        issue_lines.append(f"- Duplicate submission_numbers: {duplicate_submissions}")
    if missing_submissions:
        issue_lines.append(f"- Missing submission_numbers: {missing_submissions}")
    if duplicate_csv_rows:
        issue_lines.append(f"- Duplicate csv_rows: {duplicate_csv_rows}")
    if missing_csv_rows:
        issue_lines.append(f"- Missing csv_rows: {missing_csv_rows}")

    if not issue_lines:
        issue_lines.append("- No duplicate or missing coverage details could be derived; repair all structural issues from the validation error.")

    return "\n".join(issue_lines)


async def repair_grouped_analysis(
    agent: FoundryAgent,
    session: AgentSession,
    invalid_analysis: Dict[str, Any],
    validation_error: str,
    total_comments: int,
) -> Optional[Dict[str, Any]]:
    """Ask the grouping agent to repair structural inconsistencies without changing the analysis more than necessary."""
    issue_details = summarize_grouped_analysis_structure_issues(invalid_analysis, total_comments)
    repair_prompt = (
        "Your previous grouped-analysis JSON was structurally invalid. "
        "Repair ONLY the structural issues while preserving the original substantive grouping intent as much as possible.\n\n"
        f"Validation error: {validation_error}\n\n"
        f"Derived structural issue details:\n{issue_details}\n\n"
        f"Required constraints:\n"
        f"- Every submission_number from 1 to {total_comments} must appear exactly once across all categories.\n"
        f"- Every csv_row from 1 to {total_comments} must appear exactly once across all categories.\n"
        f"- For each category, comment_count must equal len(submission_numbers) and len(csv_rows).\n"
        f"- The sum of category comment_count values must equal total_comments ({total_comments}).\n"
        f"- Return JSON only, in the exact schema from your instructions.\n\n"
        "Previous invalid JSON:\n"
        f"{json.dumps(invalid_analysis, ensure_ascii=False, indent=2)}"
    )

    repair_response = await run_foundry_agent(agent, session, repair_prompt)

    repair_text = extract_json_payload(repair_response)

    try:
        repaired_analysis = json.loads(repair_text)
    except Exception as e:
        logging.warning(f"Could not parse repaired grouped analysis JSON: {e}")
        return None

    return repaired_analysis


async def validate_grouped_analysis_with_agent(
    categorizations: List[Dict],
    grouped_analysis: Any,
    agent_name: str,
    agent_version: Optional[str] = None,
    agent_model: Optional[str] = None,
) -> Any:
    """Ask a validator agent to review grouped analysis and make minimal corrections when needed."""
    async with create_foundry_agent(agent_name, agent_version, agent_model) as agent:
        session = AgentSession()

        validation_prompt = (
            "Review the grouped analysis against the categorization inputs. "
            "Return JSON only in the validator schema from your instructions. "
            "If the grouped analysis is already materially coherent, return status 'pass'. "
            "If it over-merges distinct primary reasons or misaligns canonical reasons, return status 'corrected' with the smallest necessary fixes.\n\n"
            "Categorization inputs:\n"
            f"{json.dumps(categorizations, ensure_ascii=False, indent=2)}\n\n"
            "Current grouped analysis:\n"
            f"{json.dumps(grouped_analysis, ensure_ascii=False, indent=2) if isinstance(grouped_analysis, dict) else str(grouped_analysis)}"
        )

        validation_response = await run_foundry_agent(agent, session, validation_prompt)
        validation_text = extract_json_payload(validation_response)

        try:
            parsed_validation = json.loads(validation_text)
        except Exception as e:
            logging.warning(f"Could not parse validator output JSON: {e}")
            return grouped_analysis

        status = parsed_validation.get("status")
        validated_analysis = parsed_validation.get("collective_analysis")

        if status not in {"pass", "corrected"} or not isinstance(validated_analysis, dict):
            logging.warning("Validator output missing expected status or collective_analysis; preserving grouped analysis")
            return grouped_analysis

        logging.info("Validator agent returned status=%s", status)
        return validated_analysis


async def categorize_with_agent(
    csv_rows: List[Dict],
    agent_name: str,
    agent_version: Optional[str] = None,
    agent_model: Optional[str] = None,
) -> List[Dict]:
    """Phase 3: Categorize each comment individually using AI agent"""
    categorizations = []
    async with create_foundry_agent(agent_name, agent_version, agent_model) as agent:
        for idx, row in enumerate(csv_rows, 1):
            logging.info(f"Processing comment {idx}/{len(csv_rows)}")

            row_string = ','.join([str(v) for v in row.values()])
            comment_text = str(row.get("comment_text", "") or "")

            if is_non_substantive_comment_text(comment_text):
                logging.info("Skipping categorization agent for non-substantive comment %s", idx)
                categorizations.append({
                    "submission_number": idx,
                    "csv_row": idx,
                    "row_data": row_string,
                    "comment_id": row.get("comment_id", ""),
                    "text_source": "inline+attachment" if row.get("has_attachments") else "inline",
                    "attachments_extracted": 1 if row.get("has_attachments") else 0,
                    "categorization": build_non_substantive_categorization(
                        "The submission did not include usable comment text or extractable attachment content."
                    )
                })
                continue

            # Use a fresh session per comment to avoid context accumulation across calls
            full_response = await run_foundry_agent(agent, AgentSession(), row_string)

            categorization_text = extract_json_payload(full_response)

            if is_refusal_or_error_response(categorization_text):
                logging.warning("Categorization agent returned refusal/error text for comment %s; normalizing to non-substantive output", idx)
                categorization_json = build_non_substantive_categorization(
                    "The categorization agent returned an unusable refusal/error string instead of analyzable structured output."
                )
            else:
                try:
                    categorization_json = json.loads(categorization_text)
                except Exception:
                    categorization_json = categorization_text

            categorizations.append({
                "submission_number": idx,
                "csv_row": idx,
                "row_data": row_string,
                "comment_id": row.get("comment_id", ""),
                "text_source": "inline+attachment" if row.get("has_attachments") else "inline",
                "attachments_extracted": 1 if row.get("has_attachments") else 0,
                "categorization": categorization_json
            })
    
    return categorizations


async def group_categorizations(
    categorizations: List[Dict],
    agent_name: str,
    agent_version: Optional[str] = None,
    agent_model: Optional[str] = None,
    batch_size: int = 5,
) -> Dict:
    """Phase 4: Analyze categorizations in batches and group similar comments"""
    logging.info(f"Grouping {len(categorizations)} categorizations with batch size {batch_size}")
    
    total_comments = len(categorizations)
    async with create_foundry_agent(agent_name, agent_version, agent_model) as agent:
        session = AgentSession()
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

            batch_response = await run_foundry_agent(agent, session, message)

            if is_last_batch:
                final_analysis = batch_response

        analysis_text = extract_json_payload(final_analysis)

        try:
            parsed_analysis = json.loads(analysis_text)
        except Exception as e:
            logging.warning(f"Could not parse JSON: {e}")
            parsed_analysis = None

        if parsed_analysis:
            try:
                parsed_analysis = validate_and_normalize_grouped_analysis(parsed_analysis, total_comments)
            except ValueError as validation_error:
                current_analysis = parsed_analysis

                for attempt in range(1, 4):
                    logging.warning(
                        "Grouped analysis validation failed on attempt %s; requesting repaired JSON: %s",
                        attempt,
                        validation_error,
                    )
                    repaired_analysis = await repair_grouped_analysis(
                        agent,
                        session,
                        current_analysis,
                        str(validation_error),
                        total_comments,
                    )

                    if repaired_analysis is None:
                        raise ValueError("Could not parse repaired grouped analysis JSON")

                    try:
                        parsed_analysis = validate_and_normalize_grouped_analysis(repaired_analysis, total_comments)
                        break
                    except ValueError as next_validation_error:
                        current_analysis = repaired_analysis
                        validation_error = next_validation_error
                else:
                    raise validation_error

        return parsed_analysis if parsed_analysis else analysis_text


async def run_analysis_workflow(
    csv_rows: List[Dict],
    timestamp: str,
    storage_account_name: str,
    categorization_agent_name: str,
    categorization_agent_version: Optional[str],
    categorization_agent_model: Optional[str],
    grouping_agent_name: str,
    grouping_agent_version: Optional[str],
    grouping_agent_model: Optional[str],
    validation_agent_name: Optional[str],
    validation_agent_version: Optional[str],
    validation_agent_model: Optional[str],
    batch_size: int,
) -> Tuple[List[Dict], Any]:
    """Run all async agent analysis phases inside a single event loop."""
    logging.info("Phase 3: Categorizing comments with AI agent")
    categorizations = await categorize_with_agent(
        csv_rows,
        categorization_agent_name,
        categorization_agent_version,
        categorization_agent_model,
    )

    categorizations_data = {
        "source_csv_file": f"comments_consolidated_{timestamp}.csv",
        "timestamp": timestamp,
        "total_comments": len(categorizations),
        "categorizations": categorizations
    }
    categorizations_json = json.dumps(categorizations_data, indent=2)
    upload_to_blob(categorizations_json, f"3_analysis/categorizations_{timestamp}.json", storage_account_name)

    logging.info("Phase 4: Grouping and analyzing with AI agent")
    grouped_analysis = await group_categorizations(
        categorizations,
        grouping_agent_name,
        grouping_agent_version,
        grouping_agent_model,
        batch_size,
    )

    if validation_agent_name:
        logging.info("Phase 5: Validating grouped analysis with validator agent")
        grouped_analysis = await validate_grouped_analysis_with_agent(
            categorizations,
            grouped_analysis,
            validation_agent_name,
            validation_agent_version,
            validation_agent_model,
        )

        if isinstance(grouped_analysis, dict):
            grouped_analysis = validate_and_normalize_grouped_analysis(grouped_analysis, len(categorizations))
    else:
        logging.info("Phase 5: Skipping validator agent step because VALIDATION_AGENT_NAME is not configured")

    return categorizations, grouped_analysis


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


# ============================================================================
# AZURE STORAGE HELPERS
# ============================================================================

def upload_to_blob(content: str, blob_name: str, storage_account_name: str, container_name: str = "regulatory-comments") -> str:
    """Upload content to Azure Blob Storage using managed identity"""
    try:
        global _blob_service_client
        if _blob_service_client is None:
            _blob_service_client = BlobServiceClient(
                account_url=f"https://{storage_account_name}.blob.core.windows.net",
                credential=DefaultAzureCredential(),
            )
        container_client = _blob_service_client.get_container_client(container_name)
        
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
# FUNCTION TRIGGERS AND SHARED WORKER
# ============================================================================

_ANALYSIS_QUEUE_NAME = "analysis-requests"


def _optional_int_setting(name: str) -> Optional[int]:
    value = os.environ.get(name)
    return int(value) if value else None


def _create_request(payload: Dict[str, Any], trigger_source: str) -> Dict[str, Any]:
    default_models = {
        "categorization": os.environ.get("CATEGORIZATION_AGENT_MODEL"),
        "grouping": os.environ.get("GROUPING_AGENT_MODEL"),
        "validation": os.environ.get("VALIDATION_AGENT_MODEL"),
    }
    configured_models = {
        model.strip()
        for model in os.environ.get("ALLOWED_MODEL_DEPLOYMENTS", "").split(",")
        if model.strip()
    }
    configured_models.update(model for model in default_models.values() if model)

    return create_analysis_request(
        payload,
        trigger_source=trigger_source,
        default_document_id=os.environ.get("DOCUMENT_ID", "ED-2025-SCC-0481-0001"),
        default_batch_size=int(os.environ.get("BATCH_SIZE", "5")),
        default_max_comments=_optional_int_setting("MAX_COMMENTS"),
        default_models=default_models,
        allowed_models=configured_models,
    )


def _get_cosmos_run_store() -> Optional[CosmosRunStore]:
    global _cosmos_run_store, _cosmos_run_store_initialized
    if not _cosmos_run_store_initialized:
        _cosmos_run_store = CosmosRunStore.from_environment()
        _cosmos_run_store_initialized = True
    return _cosmos_run_store


@app.route(route="analysis-runs", methods=["POST"], auth_level=func.AuthLevel.FUNCTION)
@app.queue_output(
    arg_name="request_message",
    queue_name=_ANALYSIS_QUEUE_NAME,
    connection="AzureWebJobsStorage",
)
def submit_analysis_run(req: func.HttpRequest, request_message: func.Out[str]) -> func.HttpResponse:
    try:
        payload = req.get_json() if req.get_body() else {}
        if not isinstance(payload, dict):
            raise AnalysisRequestValidationError("The request body must be a JSON object.")
        request = _create_request(payload, "manual")
    except (AnalysisRequestValidationError, ValueError) as error:
        return func.HttpResponse(
            json.dumps({"error": str(error)}),
            status_code=400,
            mimetype="application/json",
        )

    run_store = _get_cosmos_run_store()
    if run_store is None:
        return func.HttpResponse(
            json.dumps({"error": "Manual analysis is unavailable because COSMOS_ENDPOINT is not configured."}),
            status_code=503,
            mimetype="application/json",
        )

    run_store.save_job(build_job_document(request, "queued"))
    request_message.set(json.dumps(request))
    return func.HttpResponse(
        json.dumps({
            "runId": request["runId"],
            "status": "queued",
            "effectiveSettings": {
                "documentId": request["documentId"],
                "commentIds": request["commentIds"],
                "maxComments": request["maxComments"],
                "batchSize": request["batchSize"],
                "models": request["models"],
                "runValidation": request["runValidation"],
            },
        }),
        status_code=202,
        mimetype="application/json",
    )


@app.route(route="analysis-runs/{run_id}", methods=["GET"], auth_level=func.AuthLevel.FUNCTION)
def get_analysis_run_status(req: func.HttpRequest) -> func.HttpResponse:
    run_id = req.route_params.get("run_id", "").strip()
    run_store = _get_cosmos_run_store()
    if run_store is None:
        return func.HttpResponse(
            json.dumps({"error": "COSMOS_ENDPOINT is not configured."}),
            status_code=503,
            mimetype="application/json",
        )

    document = run_store.get(run_id)
    if document is None:
        return func.HttpResponse(
            json.dumps({"error": "Analysis run not found."}),
            status_code=404,
            mimetype="application/json",
        )

    status = document.get("status")
    if not status and document.get("type") == "analysisRun":
        status = "succeeded" if document.get("succeeded") else "failed"
    response = {
        "runId": document["id"],
        "status": status,
        "documentId": document.get("documentId"),
        "startedAt": document.get("startedAt"),
        "completedAt": document.get("completedAt"),
        "totalComments": document.get("totalComments"),
        "succeeded": document.get("succeeded"),
        "errorMessage": document.get("errorMessage"),
    }
    return func.HttpResponse(json.dumps(response), mimetype="application/json")


def _get_followup_agent_settings(payload: Dict[str, Any]) -> Tuple[str, Optional[str], Optional[str]]:
    agent_name = (
        str(payload.get("agentName") or "").strip()
        or os.environ.get("FOLLOWUP_AGENT_NAME")
        or os.environ.get("FOLLOWUP_AGENT_ID")
        or "RegulatoryCommentFollowUpAgent"
    )
    agent_version = (
        str(payload.get("agentVersion") or "").strip()
        or os.environ.get("FOLLOWUP_AGENT_VERSION")
        or None
    )
    agent_model = (
        str(payload.get("agentModel") or "").strip()
        or os.environ.get("FOLLOWUP_AGENT_MODEL")
        or os.environ.get("CATEGORIZATION_AGENT_MODEL")
        or None
    )
    if not agent_name:
        raise ValueError("FOLLOWUP_AGENT_NAME is not configured.")
    return agent_name, agent_version, agent_model


def _followup_payload(req: func.HttpRequest) -> Dict[str, Any]:
    payload = req.get_json() if req.get_body() else {}
    if not isinstance(payload, dict):
        raise ValueError("The request body must be a JSON object.")
    return payload


def _format_followup_prompt(payload: Dict[str, Any]) -> str:
    analysis_context = str(payload.get("analysisContext") or "").strip()
    question = str(payload.get("question") or "").strip()
    if not analysis_context:
        raise ValueError("analysisContext is required.")
    if not question:
        raise ValueError("question is required.")

    history = payload.get("history") or []
    prompt_lines = [
        "You are a follow-up Q&A assistant for a public-comments analysis.",
        "Use only the analysis context below and the prior chat turns provided here.",
        "If the analysis does not contain enough information to answer, say so clearly.",
        "",
        "=== ANALYSIS CONTEXT ===",
        analysis_context,
        "",
        "=== PRIOR CHAT TURNS ===",
    ]

    if isinstance(history, list) and history:
        for turn in history[-20:]:
            if not isinstance(turn, dict):
                continue
            role = str(turn.get("role") or "").strip() or "user"
            text = str(turn.get("text") or "").strip()
            if text:
                prompt_lines.append(f"{role}: {text}")
    else:
        prompt_lines.append("(none)")

    prompt_lines.extend([
        "",
        "=== CURRENT QUESTION ===",
        question,
        "",
        "Answer in plain language and cite relevant theme groups or submission numbers when useful.",
    ])
    return "\n".join(prompt_lines)


@app.route(route="followup/start", methods=["POST"], auth_level=func.AuthLevel.FUNCTION)
def start_followup(req: func.HttpRequest) -> func.HttpResponse:
    try:
        payload = _followup_payload(req)
        _get_followup_agent_settings(payload)
        if not str(payload.get("analysisContext") or "").strip():
            raise ValueError("analysisContext is required.")
    except ValueError as error:
        return func.HttpResponse(
            json.dumps({"error": str(error)}),
            status_code=400,
            mimetype="application/json",
        )

    return func.HttpResponse(
        json.dumps({"conversationId": f"function-followup-{uuid.uuid4()}"}),
        mimetype="application/json",
    )


@app.route(route="followup/ask", methods=["POST"], auth_level=func.AuthLevel.FUNCTION)
async def ask_followup(req: func.HttpRequest) -> func.HttpResponse:
    try:
        payload = _followup_payload(req)
        agent_name, agent_version, agent_model = _get_followup_agent_settings(payload)
        prompt = _format_followup_prompt(payload)
        async with create_foundry_agent(agent_name, agent_version, agent_model) as agent:
            answer = await run_foundry_agent(agent, AgentSession(), prompt)
    except ValueError as error:
        return func.HttpResponse(
            json.dumps({"error": str(error)}),
            status_code=400,
            mimetype="application/json",
        )
    except Exception as error:
        logging.exception("Follow-up Q&A agent call failed.")
        return func.HttpResponse(
            json.dumps({"error": str(error)}),
            status_code=502,
            mimetype="application/json",
        )

    return func.HttpResponse(
        json.dumps({
            "conversationId": str(payload.get("conversationId") or f"function-followup-{uuid.uuid4()}"),
            "answer": answer,
        }),
        mimetype="application/json",
    )


@app.schedule(
    schedule="0 0 8 * * *",
    arg_name="myTimer",
    run_on_startup=False,
    use_monitor=False,
)
@app.queue_output(
    arg_name="request_message",
    queue_name=_ANALYSIS_QUEUE_NAME,
    connection="AzureWebJobsStorage",
)
def regulatory_comments_daily(myTimer: func.TimerRequest, request_message: func.Out[str]) -> None:
    if myTimer.past_due:
        logging.info("The timer is past due.")

    request = _create_request({}, "scheduled")
    run_store = _get_cosmos_run_store()
    if run_store is not None:
        run_store.save_job(build_job_document(request, "queued"))
    request_message.set(json.dumps(request))
    logging.info("Queued scheduled analysis run %s.", request["runId"])


@app.queue_trigger(
    arg_name="request_message",
    queue_name=_ANALYSIS_QUEUE_NAME,
    connection="AzureWebJobsStorage",
)
async def process_analysis_run(request_message: func.QueueMessage) -> None:
    request = json.loads(request_message.get_body().decode("utf-8"))
    started_at = utc_now()
    run_store = _get_cosmos_run_store()
    if run_store is not None and not run_store.try_start(request["runId"], started_at):
        logging.info(
            "Skipping duplicate delivery for analysis run %s because it is already running or complete.",
            request["runId"],
        )
        return
    logging.info(
        "Starting %s analysis run %s for document %s.",
        request["triggerSource"],
        request["runId"],
        request["documentId"],
    )
    try:
        result = await execute_analysis_request(request)
        if run_store is not None:
            run_store.save_analysis(build_analysis_document(
                request,
                result,
                started_at=started_at,
                completed_at=utc_now(),
            ))
    except Exception as error:
        if run_store is not None:
            run_store.save_analysis(build_failed_analysis_document(
                request,
                started_at=started_at,
                completed_at=utc_now(),
                error_message=str(error),
            ))
        raise


async def execute_analysis_request(request: Dict[str, Any]) -> Dict[str, Any]:
    timestamp = (
        datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
        + f"_{request['runId'][:8]}"
    )

    api_key = os.environ.get("REGULATIONS_GOV_API_KEY")
    document_id = request["documentId"]
    foundry_project_endpoint = (
        os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
        or os.environ.get("AZURE_AI_AGENT_ENDPOINT")
        or os.environ.get("AZURE_AI_PROJECT_ENDPOINT")
    )
    categorization_agent_name = os.environ.get("CATEGORIZATION_AGENT_NAME") or os.environ.get("CATEGORIZATION_AGENT_ID")
    categorization_agent_version = os.environ.get("CATEGORIZATION_AGENT_VERSION")
    categorization_agent_model = request["models"]["categorization"]
    grouping_agent_name = os.environ.get("GROUPING_AGENT_NAME") or os.environ.get("GROUPING_AGENT_ID")
    grouping_agent_version = os.environ.get("GROUPING_AGENT_VERSION")
    grouping_agent_model = request["models"]["grouping"]
    validation_agent_name = (
        os.environ.get("VALIDATION_AGENT_NAME") or os.environ.get("VALIDATION_AGENT_ID")
        if request["runValidation"]
        else None
    )
    validation_agent_version = os.environ.get("VALIDATION_AGENT_VERSION")
    validation_agent_model = request["models"]["validation"]
    batch_size = request["batchSize"]
    max_comments = request["maxComments"]
    requested_comment_ids = request.get("commentIds", [])
    storage_account_name = os.environ.get("AZURE_STORAGE_ACCOUNT_NAME")
    document_intelligence_endpoint = (
        os.environ.get("DOCUMENTINTELLIGENCE_ENDPOINT")
        or os.environ.get("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT")
    )
    
    if not api_key:
        raise ValueError("REGULATIONS_GOV_API_KEY not found in environment variables")
    
    if not storage_account_name:
        raise ValueError("AZURE_STORAGE_ACCOUNT_NAME not found in environment variables")

    if not document_intelligence_endpoint:
        raise ValueError(
            "DOCUMENTINTELLIGENCE_ENDPOINT or AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT not found in environment variables"
        )

    if not foundry_project_endpoint:
        raise ValueError("FOUNDRY_PROJECT_ENDPOINT not found in environment variables")

    if not categorization_agent_name:
        raise ValueError("CATEGORIZATION_AGENT_NAME not found in environment variables")

    if not grouping_agent_name:
        raise ValueError("GROUPING_AGENT_NAME not found in environment variables")
    
    try:
        # Phase 1: Fetch comments
        if max_comments:
            logging.info(f"Phase 1: Fetching up to {max_comments} comments for document {document_id}")
        else:
            logging.info(f"Phase 1: Fetching all comments for document {document_id}")
        
        fetch_limit = None if requested_comment_ids else max_comments
        comments = fetch_comments_from_api(document_id, api_key, max_comments=fetch_limit)
        
        if not comments:
            logging.warning("No comments found. Trying with docket filter...")
            comments = fetch_comments_from_api(
                document_id,
                api_key,
                max_comments=fetch_limit,
                use_docket_filter=True,
            )
        
        if not comments:
            raise ValueError(f"No comments found for document {document_id} with either filter.")

        if requested_comment_ids:
            requested_ids = set(requested_comment_ids)
            comments = [comment for comment in comments if comment.get("id") in requested_ids]
            missing_ids = requested_ids - {comment.get("id") for comment in comments}
            if missing_ids:
                raise ValueError(
                    f"Could not find {len(missing_ids)} requested comment(s) for document {document_id}."
                )
        
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
        
        categorizations, grouped_analysis = await run_analysis_workflow(
            csv_rows,
            timestamp,
            storage_account_name,
            categorization_agent_name,
            categorization_agent_version,
            categorization_agent_model,
            grouping_agent_name,
            grouping_agent_version,
            grouping_agent_model,
            validation_agent_name,
            validation_agent_version,
            validation_agent_model,
            batch_size,
        )
        
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
        
        logging.info(f"Workflow completed successfully! Processed {len(csv_rows)} comments")
        logging.info(f"All outputs saved to Azure Blob Storage with timestamp {timestamp}")
        return {
            "runId": request["runId"],
            "documentId": document_id,
            "timestamp": timestamp,
            "totalComments": len(categorizations),
            "categorizations": categorizations,
            "groupedAnalysis": grouped_analysis,
        }
        
    except Exception as e:
        logging.error(f"Error in workflow: {e}", exc_info=True)
        raise