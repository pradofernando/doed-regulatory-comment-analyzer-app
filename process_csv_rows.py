import csv
import asyncio
import time
import json
from datetime import datetime
from typing import Optional, Tuple, cast
from azure.identity.aio import AzureCliCredential
from semantic_kernel.agents import AzureAIAgent, AzureAIAgentThread
from semantic_kernel.contents import ChatMessageContent, FunctionCallContent, FunctionResultContent
from dotenv import load_dotenv
import os

load_dotenv()

def process_csv_rows(csv_file_path, max_rows=None):
    """
    Read CSV file and process each row as a string.
    Returns a list of all row strings for public submissions only.
    
    Args:
        csv_file_path: Path to the CSV file
        max_rows: Maximum number of public submissions to process (None = all rows)
    """
    row_strings = []
    
    with open(csv_file_path, 'r', encoding='utf-8') as file:
        csv_reader = csv.reader(file)
        
        # Read header
        header = next(csv_reader)
        print(f"CSV Header: {header}\n")
        print(f"Total columns: {len(header)}\n")
        
        # Check if we have Document Type column (from old CSV format)
        # If not, all rows are considered public submissions
        doc_type_index = -1
        try:
            doc_type_index = header.index('Document Type')
            print(f"Document Type column is at index: {doc_type_index}\n")
            has_doc_type = True
        except ValueError:
            print("Note: 'Document Type' column not found - processing all rows as comments\n")
            has_doc_type = False
        
        print("-" * 80)
        
        # Process each row
        total_rows = 0
        public_submission_count = 0
        skipped_count = 0
        
        for row in csv_reader:
            total_rows += 1
            
            # Check if this is a public submission (if we have that column)
            if has_doc_type:
                doc_type = row[doc_type_index] if len(row) > doc_type_index else ""
                
                if doc_type != "Public Submission":
                    skipped_count += 1
                    if skipped_count <= 3:
                        print(f"\nSkipping Row {total_rows} (Document Type: '{doc_type}')")
                    continue
            
            public_submission_count += 1
            
            # Convert entire row to a single string
            row_string = ','.join(row)
            row_strings.append((total_rows, row_string))  # Store row number with string
            
            # Display first few public submissions as example
            if public_submission_count <= 3:
                print(f"\nPublic Submission {public_submission_count} (CSV Row {total_rows}):")
                print(f"String: {row_string[:200]}...")  # Show first 200 chars
                print(f"Full length: {len(row_string)} characters")
            
            # Stop after reaching max_rows (if specified)
            if max_rows and public_submission_count >= max_rows:
                break
        
        print(f"\n{'-' * 80}")
        print(f"Total rows read: {total_rows}")
        print(f"Public submissions processed: {public_submission_count}")
        print(f"Non-public submissions skipped: {skipped_count}")
    
    return row_strings

async def handle_streaming_intermediate_steps(message: ChatMessageContent) -> None:
    for item in message.items or []:
        if isinstance(item, FunctionResultContent):
            print(f"Function Result:> {item.result} for function: {item.name}")
        elif isinstance(item, FunctionCallContent):
            print(f"Function Call:> {item.name} with arguments: {item.arguments}")
        else:
            print(f"{item}")


async def categorize_with_agent(row_strings, csv_file_path) -> Tuple[list, str]:
    """
    Phase 1: Categorize each comment individually.
    Returns a list of categorization results.
    """
    categorizations = []
    
    async with (
        AzureCliCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        # 1. Retrieve the agent definition based on the agent_name
        agent_definition = await client.agents.get_agent(
            agent_id=os.environ.get("CATEGORIZATION_AGENT_ID", ""),
        )

        # 2. Create a Semantic Kernel agent for the Azure AI agent
        agent = AzureAIAgent(
            client=client,
            definition=agent_definition,
        )

        # 3. Create a thread for the agent
        # If no thread is provided, a new thread will be
        # created and returned with the initial response
        thread: Optional[AzureAIAgentThread] = None

        try:
            for idx, (row_num, row_string) in enumerate(row_strings, 1):
                print(f"\n{'=' * 80}")
                print(f"PHASE 1: Processing Public Submission {idx} (CSV Row {row_num})")
                print(f"{'=' * 80}")
                print(f"# User: '{row_string}'\n")
                
                # Collect the full response
                full_response = ""
                
                # Invoke the agent for the specified thread for response
                async for response in agent.invoke_stream(
                    messages=row_string,
                    thread=thread,
                    on_intermediate_message=handle_streaming_intermediate_steps,
                ):
                    print(f"{response}", end="", flush=True)
                    full_response += str(response)
                    thread = cast(AzureAIAgentThread, response.thread)
                
                # Parse the categorization to remove markdown code blocks
                categorization_text = full_response.strip()
                if "```json" in categorization_text:
                    start = categorization_text.find("```json") + 7
                    end = categorization_text.find("```", start)
                    categorization_text = categorization_text[start:end].strip()
                elif "```" in categorization_text:
                    start = categorization_text.find("```") + 3
                    end = categorization_text.find("```", start)
                    categorization_text = categorization_text[start:end].strip()
                
                # Try to parse as JSON for cleaner storage
                try:
                    categorization_json = json.loads(categorization_text)
                except:
                    categorization_json = categorization_text
                
                # Store the categorization result
                categorizations.append({
                    "submission_number": idx,
                    "csv_row": row_num,
                    "row_data": row_string,
                    "categorization": categorization_json
                })
                
                print(f"\n\n{'=' * 80}")
                print(f"Completed Public Submission {idx} (CSV Row {row_num})")
                print(f"{'=' * 80}\n")
            
            # Save categorizations to JSON file
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            output_file = f"categorizations_{timestamp}.json"
            
            import os
            csv_filename = os.path.basename(csv_file_path)
            
            output_data = {
                "source_csv_file": csv_filename,
                "timestamp": timestamp,
                "total_comments": len(categorizations),
                "categorizations": categorizations
            }
            
            with open(output_file, 'w', encoding='utf-8') as f:
                json.dump(output_data, f, indent=2, ensure_ascii=False)
            
            print(f"\n{'=' * 80}")
            print(f"PHASE 1 COMPLETE: Saved {len(categorizations)} categorizations to {output_file}")
            print(f"{'=' * 80}\n")
            
        finally:
            # 5. Cleanup: Delete the thread and agent
            # await thread.delete() if thread else None
            # Do not clean up the agent so it can be used again
            pass
    
    return categorizations, output_file


async def group_categorizations(categorizations_file: str, batch_size: int = 5) -> None:
    """
    Phase 2: Analyze categorizations in small batches and group similar comments together.
    Maintains thread context across batches for collective analysis.
    """
    print(f"\n\n{'#' * 80}")
    print(f"PHASE 2: GROUPING AND ANALYSIS (Batch Size: {batch_size})")
    print(f"{'#' * 80}\n")
    
    # Load categorizations from file
    print(f"Loading categorizations from {categorizations_file}...\n")
    with open(categorizations_file, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    # Handle both old and new formats
    import os
    if isinstance(data, list):
        # Old format: just array of categorizations
        categorizations = data
        source_csv = "unknown"
    else:
        # New format: object with metadata
        categorizations = data.get('categorizations', [])
        source_csv_raw = data.get('source_csv_file', 'unknown')
        # Extract just filename if it's a full path
        source_csv = os.path.basename(source_csv_raw) if source_csv_raw != 'unknown' else 'unknown'
    
    total_comments = len(categorizations)
    
    async with (
        AzureCliCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        agent_definition = await client.agents.get_agent(
            agent_id=os.environ.get("GROUPING_AGENT_ID", ""),
        )
        
        agent = AzureAIAgent(
            client=client,
            definition=agent_definition,
        )
        
        thread: Optional[AzureAIAgentThread] = None
        
        try:
            # Process categorizations in batches, maintaining thread context
            final_analysis = ""
            
            for batch_num in range(0, total_comments, batch_size):
                batch = categorizations[batch_num:batch_num + batch_size]
                batch_index = batch_num // batch_size + 1
                
                print(f"\n{'*' * 80}")
                print(f"Processing Batch {batch_index} (Comments {batch_num + 1}-{min(batch_num + batch_size, total_comments)})")
                print(f"{'*' * 80}\n")
                
                # Build message for this batch
                if batch_index == 1:
                    # First batch - set context
                    message = f"I will show you categorized public comments in batches of {batch_size}. Please remember all comments as I show them to you. After all batches, I will ask for your collective analysis.\n\nBatch {batch_index}:\n\n"
                else:
                    # Subsequent batches - just show the data
                    message = f"Batch {batch_index}:\n\n"
                
                # Add the categorization data
                for cat in batch:
                    message += f"--- Submission {cat['submission_number']} (CSV Row {cat['csv_row']}) ---\n"
                    message += f"{cat['categorization']}\n\n"
                
                is_last_batch = batch_num + batch_size >= total_comments
                
                if is_last_batch:
                    # Last batch - request collective analysis with summaries
                    message += f"\nThat was the final batch. You've now seen all {total_comments} comments. Please provide your collective analysis in the JSON format specified in your instructions."
                else:
                    # Not the last batch - just acknowledge
                    message += "\nAcknowledge receipt. More batches coming..."
                
                print(f"Sending batch {batch_index} to agent...\n")
                
                batch_response = ""
                async for response in agent.invoke_stream(
                    messages=message,
                    thread=thread,
                    on_intermediate_message=handle_streaming_intermediate_steps,
                ):
                    print(f"{response}", end="", flush=True)
                    batch_response += str(response)
                    thread = cast(AzureAIAgentThread, response.thread)
                
                # Capture the final analysis from the last batch
                if is_last_batch:
                    final_analysis = batch_response
            
            # The final response from the last batch contains the complete analysis
            # No need to request it again - the agent already provided it
            
            # Save grouping results to JSON file
            output_dir = "output/3_analysis"
            os.makedirs(output_dir, exist_ok=True)
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            grouping_file = f"{output_dir}/grouped_analysis_{timestamp}.json"
            
            # Extract JSON from markdown code blocks if present
            analysis_text = final_analysis.strip()
            if "```json" in analysis_text:
                # Extract content between ```json and ```
                start = analysis_text.find("```json") + 7
                end = analysis_text.find("```", start)
                analysis_text = analysis_text[start:end].strip()
            elif "```" in analysis_text:
                # Extract content between ``` and ```
                start = analysis_text.find("```") + 3
                end = analysis_text.find("```", start)
                analysis_text = analysis_text[start:end].strip()
            
            # Try to parse the collective analysis as JSON for cleaner storage
            parsed_analysis = None
            try:
                parsed_analysis = json.loads(analysis_text)
            except Exception as e:
                # If parsing fails, keep the cleaned text (without markdown)
                print(f"\n⚠️  Warning: Could not parse JSON: {e}")
                print("Saving cleaned text instead of parsed JSON.\n")
                parsed_analysis = None
            
            grouping_results = {
                "phase": "2_grouping_analysis",
                "timestamp": timestamp,
                "source_csv_file": source_csv,
                "source_categorization_file": categorizations_file,
                "total_comments_analyzed": total_comments,
                "batch_size": batch_size,
                "total_batches": (total_comments + batch_size - 1) // batch_size,
                "collective_analysis": parsed_analysis if parsed_analysis else analysis_text
            }
            
            with open(grouping_file, 'w', encoding='utf-8') as f:
                json.dump(grouping_results, f, indent=2, ensure_ascii=False)
            
            print(f"\n\nPhase 2 complete: Analyzed {total_comments} comments collectively")
            print(f"Saved to: {grouping_file}\n")
        finally:
            pass


if __name__ == "__main__":
    # Find the most recent consolidated CSV
    import glob
    csv_files = glob.glob(r"c:\src\DoED\output\2_consolidate\comments_consolidated_*.csv")
    if not csv_files:
        # Try old location for backwards compatibility
        csv_files = glob.glob(r"c:\src\DoED\comments_consolidated_*.csv")
    if not csv_files:
        print("ERROR: No consolidated CSV files found!")
        print("Run consolidate_comments_to_csv.py first.")
        exit(1)
    csv_file = max(csv_files, key=os.path.getctime)  # Get most recent
    print(f"Using CSV file: {csv_file}\n")
    
    # Phase 1 row limit: Limits how many comments to process (useful for testing)
    # - Set to None: Process ALL comments (production use)
    # - Set to number (e.g., 5): Process only first N comments (testing/development)
    max_rows = 5
    
    # Phase 2 batch size: Controls how many categorizations are sent to the agent at once
    # - Smaller (2-5): More API calls but safer, agent processes incrementally
    # - Larger (10-20): Fewer API calls but bigger messages, might hit token limits
    # The agent maintains thread context across ALL batches to remember everything
    # and provide collective analysis at the end
    batch_size = 2

    row_strings = process_csv_rows(csv_file, max_rows=max_rows)
    
    async def main():
        # Phase 1: Categorize each comment individually
        categorizations, output_file = await categorize_with_agent(row_strings, csv_file)
        
        # Phase 2: Group similar categorizations together
        await group_categorizations(output_file, batch_size)
    
    asyncio.run(main())
