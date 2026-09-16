namespace DoedRegulatoryComments.Web.Services;

public sealed record AnalysisCoverage(
    int? Available,
    int? Fetched,
    int Selected,
    int Categorized,
    int? WithUsableSource,
    int? Unreadable,
    int? Partial,
    int? Complete,
    int Uncaptured,
    string Scope)
{
    public const string InterpretationNotice =
        "These are public submissions, not a representative survey. Counts and stance describe only the analyzed submissions.";

    public static AnalysisCoverage From(AnalysisRun run)
    {
        var known = run.Sources.Count > 0;
        var categorized = run.Categorizations.Select(c => c.CommentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new AnalysisCoverage(
            run.Provenance?.AvailableComments,
            run.Provenance?.FetchedComments,
            run.Provenance?.SelectedComments ?? run.TotalComments,
            run.Categorizations.Count,
            known ? run.Sources.Count(s => s.Passages.Any(p => !string.IsNullOrWhiteSpace(p.Text))
                && categorized.Contains(s.CommentId)) : null,
            known ? run.Sources.Count(s => s.ExtractionStatus == "unreadable") : null,
            known ? run.Sources.Count(s => s.ExtractionStatus == "partial") : null,
            known ? run.Sources.Count(s => s.ExtractionStatus == "complete") : null,
            Math.Max(0, run.TotalComments - run.Sources.Select(s => s.CommentId).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            run.Provenance?.Scope ?? "unknown");
    }

    public static string Display(int? value) => value?.ToString("N0") ?? "Unknown";
}
