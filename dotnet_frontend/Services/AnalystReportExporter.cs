using System.Globalization;
using System.Text;

namespace DoedRegulatoryComments.Web.Services;

public static class AnalystReportExporter
{
    public static string ContextText(AnalysisRun run, RunReviewState? reviews = null)
    {
        var coverage = AnalysisCoverage.From(run);
        var text = new StringBuilder();
        text.AppendLine($"Report basis: {(reviews?.Revisions.Count > 0 ? "current reviewed classifications" : "original AI classifications")}.");
        text.AppendLine($"Saved comment revisions: {reviews?.Revisions.Count ?? 0}");
        text.AppendLine($"Scope: {coverage.Scope}. {run.Provenance?.SelectionDescription}");
        text.AppendLine($"Available: {AnalysisCoverage.Display(coverage.Available)}; fetched: {AnalysisCoverage.Display(coverage.Fetched)}; selected: {coverage.Selected}.");
        text.AppendLine($"Categorized with usable source: {AnalysisCoverage.Display(coverage.WithUsableSource)}; unreadable: {AnalysisCoverage.Display(coverage.Unreadable)}; partial: {AnalysisCoverage.Display(coverage.Partial)}; uncaptured: {coverage.Uncaptured}.");
        text.AppendLine(AnalysisCoverage.InterpretationNotice);
        text.AppendLine("Draft response language requires human review. Reviewer labels are not authenticated identities.");
        if (run.Provenance is { } provenance)
        {
            text.AppendLine($"Pipeline: {provenance.PipelineVersion}; configured model: {provenance.Model}; batch: {provenance.BatchSize}; validation: {provenance.RunValidation}.");
            foreach (var agent in provenance.AgentVersions) text.AppendLine($"{agent.Key}: {agent.Value}");
            text.AppendLine("Configuration records do not prove a resolved model version; 'latest' is not a version pin. Model-reported confidence is not calibrated accuracy.");
        }
        else text.AppendLine("Legacy run: analysis configuration and source provenance were not recorded.");
        return text.ToString();
    }

    public static string EvidenceText(AnalysisRun run)
    {
        var text = new StringBuilder("VERIFIED SOURCE QUOTATIONS\n");
        var evidence = SourceEvidence.GetEvidence(run);
        if (evidence.Count == 0)
            text.AppendLine("No exact source quotations were verified. Do not treat unverified AI text as a citation.");
        foreach (var match in evidence)
        {
            text.AppendLine($"[{match.Passage.Id}] {match.Source.CommentId} - {match.Passage.Title} - {PageLabel(match.Passage)}");
            text.AppendLine(match.Quote);
            if (!string.IsNullOrWhiteSpace(match.Passage.Url)) text.AppendLine(match.Passage.Url);
        }
        text.AppendLine("Quotation matching verifies the captured text, not whether an interpretation logically follows from it.");
        return text.ToString();
    }

    public static string PageLabel(SourcePassage passage) =>
        passage.PageNumber is { } page ? $"page {page}" :
        passage.Kind == "inline" ? "inline comment" : "page not available";

    public static IReadOnlyList<string[]> IssueRows(IEnumerable<IssueResponse> issues)
    {
        var rows = new List<string[]>
        {
            new[] { "Rule section", "Concern", "Supporting submissions", "Requested change", "Draft response - human review required", "Review status", "Reviewer label", "Notes", "Updated UTC" },
        };
        rows.AddRange(issues.Select(issue => new[]
        {
            issue.RuleSection, issue.Concern, string.Join(", ", issue.SubmissionNumbers),
            issue.RequestedChange, issue.DraftResponse, issue.ReviewStatus, issue.Reviewer, issue.Notes,
            issue.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        }));
        return rows;
    }

    public static string IssuesCsv(IEnumerable<IssueResponse> issues)
    {
        var text = new StringBuilder();
        foreach (var row in IssueRows(issues))
            text.AppendLine(string.Join(",", row.Select(SpreadsheetSafeCsv)));
        return text.ToString();
    }

    private static string SpreadsheetSafeCsv(string value)
    {
        var leading = value.TrimStart();
        if (leading.Length > 0 && "=+-@".Contains(leading[0])) value = "'" + value;
        return CommentExporter.Esc(value);
    }
}
