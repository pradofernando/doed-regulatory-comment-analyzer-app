import json
import sys

def format_grouped_analysis(input_file):
    """
    Convert grouped analysis JSON with escaped strings into a clean, readable format.
    """
    # Read the input file
    with open(input_file, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    # Extract and parse the collective_analysis if it's a string
    if isinstance(data.get('collective_analysis'), str):
        analysis_text = data['collective_analysis'].strip()
        
        # Remove markdown code blocks if present
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
            data['collective_analysis'] = parsed_analysis
        except json.JSONDecodeError as e:
            print(f"Warning: Could not parse collective_analysis as JSON: {e}")
    
    # Create output filename
    output_file = input_file.replace('.json', '_formatted.json')
    
    # Write formatted output
    with open(output_file, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
    
    print(f"Formatted analysis saved to: {output_file}")
    
    # Print a summary
    if isinstance(data.get('collective_analysis'), dict):
        print("\n" + "=" * 80)
        print("SUMMARY")
        print("=" * 80)
        
        analysis = data['collective_analysis']
        
        if 'theme_groups' in analysis:
            print(f"\nTotal Theme Groups: {len(analysis['theme_groups'])}")
            for group in analysis['theme_groups']:
                print(f"\n  • {group['group_name']}")
                print(f"    Count: {group['count']} comments")
                print(f"    Submissions: {group['submission_numbers']}")
                stance_dist = group.get('stance_distribution', {})
                print(f"    Stance: {dict(stance_dist)}")
        
        if 'overall_summary' in analysis:
            print(f"\n{'-' * 80}")
            print("OVERALL SUMMARY:")
            print(f"{'-' * 80}")
            print(f"{analysis['overall_summary']}")
        
        if 'patterns' in analysis:
            print(f"\n{'-' * 80}")
            print("KEY PATTERNS:")
            print(f"{'-' * 80}")
            for i, pattern in enumerate(analysis['patterns'], 1):
                print(f"{i}. {pattern}")
        
        print("\n" + "=" * 80)

if __name__ == "__main__":
    if len(sys.argv) > 1:
        input_file = sys.argv[1]
    else:
        # Use the most recent file
        input_file = "grouped_analysis_20251118_095111.json"
    
    format_grouped_analysis(input_file)
