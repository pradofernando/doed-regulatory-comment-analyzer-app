import requests
import json
import time
from datetime import datetime
import os
from dotenv import load_dotenv

load_dotenv()

# Get API key from environment variable
API_KEY = os.getenv("REGULATIONS_GOV_API_KEY")

if not API_KEY:
    print("ERROR: Please set REGULATIONS_GOV_API_KEY in your .env file")
    print("Get a free API key at: https://open.gsa.gov/api/regulationsgov/#getting-started")
    exit(1)

def verify_document_exists(document_id):
    """
    Verify that a document exists in the API
    
    Args:
        document_id: Document ID to check
    
    Returns:
        Document data if exists, None otherwise
    """
    url = f"https://api.regulations.gov/v4/documents/{document_id}"
    headers = {"X-Api-Key": API_KEY}
    
    try:
        print(f"Verifying document exists: {document_id}...")
        response = requests.get(url, headers=headers)
        response.raise_for_status()
        
        data = response.json()
        doc_data = data.get("data", {})
        attributes = doc_data.get("attributes", {})
        
        print(f"✓ Document found!")
        print(f"  Title: {attributes.get('title', 'N/A')}")
        print(f"  Docket ID: {attributes.get('docketId', 'N/A')}")
        print(f"  Posted Date: {attributes.get('postedDate', 'N/A')}")
        print(f"  Comment Start Date: {attributes.get('commentStartDate', 'N/A')}")
        print(f"  Comment End Date: {attributes.get('commentEndDate', 'N/A')}")
        print()
        
        return data
    except requests.exceptions.HTTPError as e:
        if e.response.status_code == 404:
            print(f"✗ Document not found: {document_id}")
            print("  Make sure the document ID is correct.")
        else:
            print(f"✗ Error verifying document: {e}")
        return None
    except requests.exceptions.RequestException as e:
        print(f"✗ Error connecting to API: {e}")
        return None


def fetch_comments(document_id, posted_date_from=None, posted_date_to=None, max_comments=None, use_docket_filter=False):
    """
    Fetch comments from regulations.gov API
    
    Args:
        document_id: Document ID (e.g., "ED-2025-SCC-0481-0001")
        posted_date_from: Start date in YYYY-MM-DD format
        posted_date_to: End date in YYYY-MM-DD format
        max_comments: Maximum number of comments to fetch (None = all)
        use_docket_filter: If True, extract docket ID and search by that instead
    
    Returns:
        List of comment objects
    """
    
    base_url = "https://api.regulations.gov/v4/comments"
    headers = {"X-Api-Key": API_KEY}
    
    # Extract docket ID if requested (format: ED-2025-SCC-0481)
    search_id = document_id
    filter_param = "filter[commentOnId]"
    
    if use_docket_filter:
        # Remove the last part after the last dash to get docket ID
        parts = document_id.rsplit('-', 1)
        if len(parts) == 2:
            search_id = parts[0]
            print(f"Using docket ID: {search_id}")
            filter_param = "filter[docketId]"  # Use docketId filter instead of searchTerm
    
    # Build filter parameters
    params = {
        filter_param: search_id,
        "page[size]": 250,  # Max allowed per page
        "page[number]": 1,
        "sort": "-postedDate",  # Sort by newest first
        "include": "attachments"  # Include attachment info
    }
    
    if posted_date_from:
        params["filter[postedDate][ge]"] = posted_date_from
    
    if posted_date_to:
        params["filter[postedDate][le]"] = posted_date_to
    
    print(f"API URL: {base_url}")
    print(f"Parameters: {params}\n")
    
    all_comments = []
    page = 1
    
    while True:
        print(f"Fetching page {page}...", end=" ", flush=True)
        params["page[number]"] = page
        
        try:
            response = requests.get(base_url, headers=headers, params=params)
            response.raise_for_status()
            
            data = response.json()
            comments = data.get("data", [])
            
            if not comments:
                print("No more comments.")
                
                # Show error details if available
                errors = data.get("errors", [])
                if errors:
                    print(f"API Errors: {errors}")
                
                break
            
            print(f"Got {len(comments)} comments.")
            all_comments.extend(comments)
            
            # Check if we've reached the max
            if max_comments and len(all_comments) >= max_comments:
                all_comments = all_comments[:max_comments]
                print(f"Reached maximum of {max_comments} comments.")
                break
            
            # Check if there are more pages
            meta = data.get("meta", {})
            total_elements = meta.get("totalElements", 0)
            total_pages = meta.get("numberOfPages", 1)
            
            print(f"  Total comments available: {total_elements}, Page {page} of {total_pages}")
            
            if page >= total_pages:
                print("Reached last page.")
                break
            
            page += 1
            
            # Be nice to the API - add a small delay between requests
            time.sleep(0.5)
            
        except requests.exceptions.RequestException as e:
            print(f"\nError fetching comments: {e}")
            break
    
    return all_comments


def get_comment_details(comment_id):
    """
    Fetch detailed information for a specific comment
    
    Args:
        comment_id: Comment ID from the API
    
    Returns:
        Detailed comment object
    """
    url = f"https://api.regulations.gov/v4/comments/{comment_id}"
    headers = {"X-Api-Key": API_KEY}
    
    try:
        response = requests.get(url, headers=headers)
        response.raise_for_status()
        return response.json()
    except requests.exceptions.RequestException as e:
        print(f"Error fetching comment {comment_id}: {e}")
        return None


def save_comments_to_json(comments, filename=None):
    """Save comments to a JSON file"""
    # Create output directory
    output_dir = "output/1_fetch"
    os.makedirs(output_dir, exist_ok=True)
    
    if not filename:
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        filename = f"{output_dir}/regulations_comments_{timestamp}.json"
    
    with open(filename, 'w', encoding='utf-8') as f:
        json.dump(comments, f, indent=2, ensure_ascii=False)
    
    print(f"\nSaved {len(comments)} comments to {filename}")
    return filename


def extract_comment_text(comments, fetch_details=False):
    """
    Extract just the comment text and basic metadata
    Returns a simplified list suitable for processing
    
    Args:
        comments: List of comment objects from API
        fetch_details: If True, fetch full details for each comment (slower but gets attachments info)
    """
    extracted = []
    
    for idx, comment in enumerate(comments, 1):
        comment_id = comment.get("id")
        attributes = comment.get("attributes", {})
        
        comment_text = attributes.get("comment", "")
        
        # If no comment text and fetch_details is True, try to get full details
        if not comment_text and fetch_details:
            print(f"  Fetching details for comment {idx}/{len(comments)}: {comment_id}...", end=" ", flush=True)
            details = get_comment_details(comment_id)
            if details:
                detail_attrs = details.get("data", {}).get("attributes", {})
                comment_text = detail_attrs.get("comment", "")
                # Update attributes with detailed info
                attributes = detail_attrs
                print("done")
            else:
                print("failed")
            time.sleep(0.3)  # Rate limit protection
        
        # Check for attachments
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


if __name__ == "__main__":
    # Configuration
    DOCUMENT_ID = "ED-2025-SCC-0481-0001"
    POSTED_DATE_FROM = None  # Set to None to get all dates, or use "YYYY-MM-DD" format
    POSTED_DATE_TO = None
    MAX_COMMENTS = None  # Set to None for all comments, or a number to limit
    
    print(f"Fetching comments for document: {DOCUMENT_ID}")
    if POSTED_DATE_FROM or POSTED_DATE_TO:
        print(f"Date range: {POSTED_DATE_FROM or 'any'} to {POSTED_DATE_TO or 'any'}")
    else:
        print("Date range: All dates")
    print("-" * 80)
    
    # First, verify the document exists
    doc_data = verify_document_exists(DOCUMENT_ID)
    if not doc_data:
        print("\nCannot proceed without valid document. Exiting.")
        exit(1)
    
    print("-" * 80)
    
    # Fetch all comments - try with document ID first
    comments = fetch_comments(
        document_id=DOCUMENT_ID,
        posted_date_from=POSTED_DATE_FROM,
        posted_date_to=POSTED_DATE_TO,
        max_comments=MAX_COMMENTS,
        use_docket_filter=False
    )
    
    # If no comments found, try searching by docket instead
    if not comments:
        print("\nNo comments found with document ID. Trying with docket search...\n")
        comments = fetch_comments(
            document_id=DOCUMENT_ID,
            posted_date_from=POSTED_DATE_FROM,
            posted_date_to=POSTED_DATE_TO,
            max_comments=MAX_COMMENTS,
            use_docket_filter=True
        )
    
    print(f"\n{'=' * 80}")
    print(f"Total comments fetched: {len(comments)}")
    print(f"{'=' * 80}")
    
    if comments:
        # Save full API response
        full_file = save_comments_to_json(comments)
        
        # Extract and save simplified version (with details fetch for attachments)
        print("\nExtracting comment details (this may take a moment)...")
        extracted = extract_comment_text(comments, fetch_details=True)
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        output_dir = "output/1_fetch"
        simple_file = f"{output_dir}/comments_extracted_{timestamp}.json"
        save_comments_to_json(extracted, simple_file)
        
        # Count comments with/without text
        with_text = sum(1 for c in extracted if c['comment'])
        with_attachments = sum(1 for c in extracted if c['has_attachments'])
        
        print(f"\nComment statistics:")
        print(f"  Comments with inline text: {with_text}")
        print(f"  Comments with attachments: {with_attachments}")
        print(f"  Comments with neither: {len(extracted) - with_text - with_attachments}")
        
        # Display sample
        print(f"\nSample comment:")
        print("-" * 80)
        if extracted:
            sample = extracted[0]
            print(f"ID: {sample['comment_id']}")
            print(f"Posted: {sample['posted_date']}")
            print(f"Commenter: {sample['commenter_name']}")
            print(f"Has attachments: {sample['has_attachments']}")
            if sample['comment']:
                print(f"Comment: {sample['comment'][:200]}...")
            if sample['attachments']:
                print(f"Attachments: {len(sample['attachments'])} file(s)")
                for att in sample['attachments'][:2]:
                    print(f"  - {att['format']}: {att['fileUrl']}")
