import requests
import json
import csv
import os
import time
from pathlib import Path
from datetime import datetime
from dotenv import load_dotenv

load_dotenv()

API_KEY = os.getenv("REGULATIONS_GOV_API_KEY")


def get_comment_with_attachments(comment_id):
    """
    Fetch full comment details including attachment URLs
    """
    url = f"https://api.regulations.gov/v4/comments/{comment_id}"
    headers = {"X-Api-Key": API_KEY}
    params = {"include": "attachments"}
    
    try:
        response = requests.get(url, headers=headers, params=params)
        response.raise_for_status()
        return response.json()
    except requests.exceptions.RequestException as e:
        print(f"Error fetching comment {comment_id}: {e}")
        return None


def download_file(url):
    """
    Download a file and return its content as bytes
    """
    try:
        # Try with browser-like headers to avoid blocking
        headers = {
            "X-Api-Key": API_KEY,
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Accept": "application/pdf,application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,*/*",
            "Referer": "https://www.regulations.gov/"
        }
        response = requests.get(url, headers=headers, timeout=30, allow_redirects=True)
        response.raise_for_status()
        return response.content
    except requests.exceptions.RequestException as e:
        print(f"  Error downloading: {e}")
        return None


def extract_text_from_pdf(pdf_content):
    """
    Extract text from PDF bytes using PyPDF2
    """
    try:
        import PyPDF2
        import io
        
        pdf_file = io.BytesIO(pdf_content)
        reader = PyPDF2.PdfReader(pdf_file)
        text = ""
        for page in reader.pages:
            text += page.extract_text() + "\n\n"
        
        return text.strip()
    except ImportError:
        print("  PyPDF2 not installed. Install with: pip install PyPDF2")
        return None
    except Exception as e:
        print(f"  Error extracting text from PDF: {e}")
        return None


def extract_text_from_docx(docx_content):
    """
    Extract text from DOCX bytes using python-docx
    """
    try:
        from docx import Document
        import io
        
        docx_file = io.BytesIO(docx_content)
        doc = Document(docx_file)
        text = "\n\n".join([para.text for para in doc.paragraphs])
        return text.strip()
    except ImportError:
        print("  python-docx not installed. Install with: pip install python-docx")
        return None
    except Exception as e:
        print(f"  Error extracting text from DOCX: {e}")
        return None


def process_comments_to_csv(comments_json_file, output_csv=None):
    """
    Process all comments and create a CSV with consolidated text from inline + attachments
    """
    # Create output directory
    output_dir = "output/2_consolidate"
    os.makedirs(output_dir, exist_ok=True)
    
    if not output_csv:
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        output_csv = f"{output_dir}/comments_consolidated_{timestamp}.csv"
    
    # Load comments
    print(f"Loading comments from {comments_json_file}...\n")
    with open(comments_json_file, 'r', encoding='utf-8') as f:
        comments = json.load(f)
    
    # Prepare CSV
    csv_rows = []
    
    print(f"Processing {len(comments)} comments...\n")
    
    for idx, comment in enumerate(comments, 1):
        comment_id = comment['comment_id']
        comment_num = comment['number']
        
        print(f"[{idx}/{len(comments)}] Processing {comment_id}...", end=" ", flush=True)
        
        # Start with inline comment text
        inline_text = comment.get('comment', '').strip()
        
        # Check if we need to fetch attachments - be more aggressive
        needs_attachments = (
            'attach' in inline_text.lower() or 
            'see attach' in inline_text.lower() or
            inline_text == "" or
            len(inline_text) < 100 or  # Very short comments likely reference attachments
            comment.get('has_attachments', False)
        )
        
        combined_text = inline_text if inline_text and 'attach' not in inline_text.lower() else ""
        attachment_info = []
        attachment_count = 0
        
        if needs_attachments:
            print("fetching details...", end=" ", flush=True)
            # Fetch full details with attachments
            details = get_comment_with_attachments(comment_id)
            
            if details:
                included = details.get('included', [])
                attachments = [item for item in included if item.get('type') == 'attachments']
                
                # Debug: Save first attachment response to see structure
                if attachments and idx == 42:  # Save details from first comment with attachment
                    with open('debug_attachment_response.json', 'w') as f:
                        json.dump(details, f, indent=2)
                
                if attachments:
                    print(f"{len(attachments)} attachment(s)...", end=" ", flush=True)
                    
                    for att_idx, attachment in enumerate(attachments, 1):
                        attrs = attachment.get('attributes', {})
                        title = attrs.get('title', f'attachment_{att_idx}')
                        attachment_id = attachment.get('id')
                        
                        # Get fileUrl from the fileFormats array (correct structure)
                        file_url = None
                        file_format = None
                        file_formats = attrs.get('fileFormats', [])
                        
                        if file_formats and len(file_formats) > 0:
                            # Use the first file format available
                            first_format = file_formats[0]
                            file_url = first_format.get('fileUrl')
                            file_format = first_format.get('format', 'pdf')
                        
                        if not file_url:
                            print(f"[no accessible URL for {title}]", end=" ", flush=True)
                            # Note the attachment exists but couldn't be downloaded
                            attachment_info.append(f"[{title} - could not access]")
                            continue
                        
                        print(f"[{file_format}]", end=" ", flush=True)
                        
                        # Download from the direct URL
                        file_content = download_file(file_url)
                        
                        # Process the downloaded content if we have it
                        if file_content:
                            extracted_text = None
                            
                            if file_format == 'pdf':
                                extracted_text = extract_text_from_pdf(file_content)
                            elif file_format in ['docx', 'doc', 'msw12']:
                                extracted_text = extract_text_from_docx(file_content)
                            else:
                                print(f"[unsupported format: {file_format}]", end=" ", flush=True)
                            
                            if extracted_text:
                                attachment_count += 1
                                attachment_info.append(f"[{title}]")
                                combined_text += f"\n\n--- Attachment: {title} ---\n\n{extracted_text}"
                                print(f"[{len(extracted_text)} chars]", end=" ", flush=True)
                            else:
                                print(f"[failed to extract]", end=" ", flush=True)
                        else:
                            print(f"[download failed]", end=" ", flush=True)
                        
                        time.sleep(0.3)  # Rate limiting
                else:
                    print("no attachments found", end=" ", flush=True)
            else:
                print("failed to fetch details", end=" ", flush=True)
        
        # If we still have no text, note that
        if not combined_text or combined_text.strip() == "":
            if attachment_info:
                combined_text = f"[Comment has {len(attachment_info)} attachment(s) but text extraction failed or files not accessible: {'; '.join(attachment_info)}]"
            else:
                combined_text = "[No text available]"
        
        print("done")
        
        # Add to CSV rows
        csv_rows.append({
            'comment_number': comment_num,
            'comment_id': comment_id,
            'posted_date': comment.get('posted_date', ''),
            'commenter_name': comment.get('commenter_name', ''),
            'organization': comment.get('organization', ''),
            'title': comment.get('title', ''),
            'has_attachments': attachment_count > 0,
            'attachment_titles': '; '.join(attachment_info),
            'comment_text': combined_text
        })
        
        time.sleep(0.5)  # Be nice to the API
    
    # Write to CSV
    print(f"\nWriting to CSV: {output_csv}...")
    
    with open(output_csv, 'w', newline='', encoding='utf-8') as csvfile:
        fieldnames = [
            'comment_number', 
            'comment_id', 
            'posted_date', 
            'commenter_name', 
            'organization', 
            'title',
            'has_attachments',
            'attachment_titles',
            'comment_text'
        ]
        
        writer = csv.DictWriter(csvfile, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(csv_rows)
    
    print(f"\n{'=' * 80}")
    print(f"Success!")
    print(f"  Total comments processed: {len(csv_rows)}")
    print(f"  Comments with attachments: {sum(1 for r in csv_rows if r['has_attachments'])}")
    print(f"  Output file: {output_csv}")
    print(f"{'=' * 80}")
    
    return output_csv


if __name__ == "__main__":
    import glob
    
    # Find the most recent extracted comments file
    json_files = glob.glob("output/1_fetch/comments_extracted_*.json")
    if not json_files:
        # Try old location for backwards compatibility
        json_files = glob.glob("comments_extracted_*.json")
    if not json_files:
        print("No comments_extracted_*.json files found!")
        print("Run fetch_regulations_comments.py first.")
        exit(1)
    
    latest_file = max(json_files, key=os.path.getctime)
    print(f"Using comments file: {latest_file}\n")
    print("=" * 80)
    
    process_comments_to_csv(latest_file)
    
    print("\nThe CSV file contains:")
    print("- comment_number: Sequential number")
    print("- comment_id: Official comment ID")
    print("- posted_date: When comment was posted")
    print("- commenter_name: Name of commenter")
    print("- organization: Organization if provided")
    print("- title: Comment title")
    print("- has_attachments: Whether attachments were processed")
    print("- attachment_titles: Names of attachments")
    print("- comment_text: Full text (inline + extracted from attachments)")
    print("\nYou can now use this CSV with your existing process_csv_rows.py script!")
