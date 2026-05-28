import requests
import json
import os
import time
from pathlib import Path
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


def download_attachment(url, output_path):
    """
    Download a file from URL to output_path
    """
    try:
        response = requests.get(url, stream=True)
        response.raise_for_status()
        
        with open(output_path, 'wb') as f:
            for chunk in response.iter_content(chunk_size=8192):
                f.write(chunk)
        
        return True
    except requests.exceptions.RequestException as e:
        print(f"  Error downloading: {e}")
        return False


def extract_text_from_pdf(pdf_path):
    """
    Extract text from PDF file using PyPDF2
    """
    try:
        import PyPDF2
        
        with open(pdf_path, 'rb') as file:
            reader = PyPDF2.PdfReader(file)
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


def extract_text_from_docx(docx_path):
    """
    Extract text from DOCX file using python-docx
    """
    try:
        from docx import Document
        
        doc = Document(docx_path)
        text = "\n\n".join([para.text for para in doc.paragraphs])
        return text.strip()
    except ImportError:
        print("  python-docx not installed. Install with: pip install python-docx")
        return None
    except Exception as e:
        print(f"  Error extracting text from DOCX: {e}")
        return None


def process_comments_with_attachments(comments_json_file, output_dir="attachments"):
    """
    Download and extract text from all attachments in the comments JSON file
    """
    # Create output directory
    Path(output_dir).mkdir(exist_ok=True)
    
    # Load comments
    print(f"Loading comments from {comments_json_file}...\n")
    with open(comments_json_file, 'r', encoding='utf-8') as f:
        comments = json.load(f)
    
    # Track results
    results = []
    comments_with_attachments = 0
    total_attachments = 0
    
    for comment in comments:
        comment_id = comment['comment_id']
        comment_num = comment['number']
        
        # Check if comment says it has attachments
        comment_text = comment.get('comment', '').lower()
        if 'attach' not in comment_text and not comment.get('has_attachments'):
            continue
        
        print(f"Processing comment {comment_num}/{len(comments)}: {comment_id}")
        
        # Fetch full details with attachments
        details = get_comment_with_attachments(comment_id)
        if not details:
            print(f"  Could not fetch details\n")
            continue
        
        # Extract attachment info
        included = details.get('included', [])
        attachments = [item for item in included if item.get('type') == 'attachments']
        
        if not attachments:
            print(f"  No attachments found\n")
            continue
        
        comments_with_attachments += 1
        comment_dir = os.path.join(output_dir, f"comment_{comment_id}")
        Path(comment_dir).mkdir(exist_ok=True)
        
        comment_result = {
            "comment_id": comment_id,
            "comment_number": comment_num,
            "organization": comment.get('organization', ''),
            "attachments": []
        }
        
        for idx, attachment in enumerate(attachments, 1):
            attrs = attachment.get('attributes', {})
            file_url = attrs.get('fileUrl')
            title = attrs.get('title', f'attachment_{idx}')
            file_format = attrs.get('format', 'unknown')
            
            if not file_url:
                continue
            
            total_attachments += 1
            
            # Sanitize filename
            safe_title = "".join(c for c in title if c.isalnum() or c in (' ', '-', '_')).strip()
            if not safe_title:
                safe_title = f"attachment_{idx}"
            
            file_ext = file_format.lower()
            if file_ext == 'msw12':
                file_ext = 'docx'
            
            filename = f"{safe_title}.{file_ext}"
            file_path = os.path.join(comment_dir, filename)
            
            print(f"  Downloading: {filename}...", end=" ", flush=True)
            
            if download_attachment(file_url, file_path):
                print("done")
                
                # Extract text based on file type
                extracted_text = None
                if file_ext == 'pdf':
                    print(f"  Extracting text from PDF...", end=" ", flush=True)
                    extracted_text = extract_text_from_pdf(file_path)
                    if extracted_text:
                        print(f"done ({len(extracted_text)} chars)")
                elif file_ext in ['docx', 'doc']:
                    print(f"  Extracting text from DOCX...", end=" ", flush=True)
                    extracted_text = extract_text_from_docx(file_path)
                    if extracted_text:
                        print(f"done ({len(extracted_text)} chars)")
                
                # Save extracted text
                if extracted_text:
                    text_path = os.path.join(comment_dir, f"{safe_title}_extracted.txt")
                    with open(text_path, 'w', encoding='utf-8') as f:
                        f.write(extracted_text)
                
                comment_result["attachments"].append({
                    "title": title,
                    "format": file_format,
                    "file_path": file_path,
                    "text_extracted": extracted_text is not None,
                    "text_length": len(extracted_text) if extracted_text else 0
                })
            else:
                print("failed")
        
        results.append(comment_result)
        print()
        
        # Be nice to the API
        time.sleep(0.5)
    
    # Save summary
    summary = {
        "total_comments": len(comments),
        "comments_with_attachments": comments_with_attachments,
        "total_attachments_downloaded": total_attachments,
        "results": results
    }
    
    summary_file = os.path.join(output_dir, "download_summary.json")
    with open(summary_file, 'w', encoding='utf-8') as f:
        json.dump(summary, f, indent=2, ensure_ascii=False)
    
    print(f"\n{'=' * 80}")
    print(f"Summary:")
    print(f"  Total comments: {len(comments)}")
    print(f"  Comments with attachments: {comments_with_attachments}")
    print(f"  Total attachments downloaded: {total_attachments}")
    print(f"  Output directory: {output_dir}")
    print(f"  Summary saved to: {summary_file}")
    print(f"{'=' * 80}")


if __name__ == "__main__":
    # Process the most recent extracted comments file
    import glob
    
    json_files = glob.glob("comments_extracted_*.json")
    if not json_files:
        print("No comments_extracted_*.json files found!")
        print("Run fetch_regulations_comments.py first.")
        exit(1)
    
    # Use the most recent file
    latest_file = max(json_files, key=os.path.getctime)
    print(f"Using comments file: {latest_file}\n")
    
    process_comments_with_attachments(latest_file)
    
    print("\n" + "=" * 80)
    print("Next steps:")
    print("1. Install text extraction libraries if needed:")
    print("   pip install PyPDF2 python-docx")
    print("2. Check the 'attachments' folder for downloaded files")
    print("3. Look for *_extracted.txt files with the text content")
    print("=" * 80)
