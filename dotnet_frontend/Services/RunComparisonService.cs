namespace DoedRegulatoryComments.Web.Services;

public sealed record CountChange(string Label, int Before, int After);
public sealed record RunComparison(
    IReadOnlyList<string> OnlyBefore, IReadOnlyList<string> OnlyAfter, IReadOnlyList<string> ChangedSources,
    IReadOnlyList<CountChange> Themes, IReadOnlyList<CountChange> Stances, IReadOnlyList<string> Warnings);

public static class RunComparisonService
{
    public static RunComparison Compare(AnalysisRun before, AnalysisRun after)
    {
        var warnings = new List<string>();
        if (AnalysisDocumentIds.Normalize(before.DocumentId) != AnalysisDocumentIds.Normalize(after.DocumentId))
            warnings.Add("Document/docket identifiers differ. These runs may cover different regulatory material.");
        var beforeIds = before.Categorizations.Select(c => c.CommentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterIds = after.Categorizations.Select(c => c.CommentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldSources = before.Sources.ToDictionary(s => s.CommentId, StringComparer.OrdinalIgnoreCase);
        var newSources = after.Sources.ToDictionary(s => s.CommentId, StringComparer.OrdinalIgnoreCase);
        if (before.Sources.Count < beforeIds.Count || after.Sources.Count < afterIds.Count)
            warnings.Add("Some source snapshots are missing. Their text changes cannot be determined.");
        if (before.Sources.Concat(after.Sources).Any(s => s.ExtractionStatus != "complete"))
            warnings.Add("At least one source was partial or unreadable; source and stance comparisons are incomplete.");
        if (!beforeIds.SetEquals(afterIds))
            warnings.Add("The runs contain different submission sets. Compare counts with their denominators; absence from a selected run does not mean removal from the docket.");
        if (before.Provenance is not { } oldConfig || after.Provenance is not { } newConfig)
            warnings.Add("Configuration provenance is missing; equivalent analysis settings cannot be established.");
        else
        {
            if (oldConfig.Scope != "full" || newConfig.Scope != "full")
                warnings.Add("One or both runs are selected, limited, or unknown in scope. Do not infer a whole-docket opinion shift.");
            if (oldConfig.QueryScope != newConfig.QueryScope || oldConfig.QueryScope == "unknown")
                warnings.Add("Query scope differs or is unknown (document versus docket).");
            if (oldConfig.Model != newConfig.Model || oldConfig.BatchSize != newConfig.BatchSize ||
                oldConfig.RunValidation != newConfig.RunValidation || oldConfig.PipelineVersion != newConfig.PipelineVersion ||
                !oldConfig.AgentVersions.OrderBy(p => p.Key).SequenceEqual(newConfig.AgentVersions.OrderBy(p => p.Key)))
                warnings.Add("Model, agent versions, pipeline, batch size, or validation settings differ.");
            if (oldConfig.AgentVersions.Count == 0 || newConfig.AgentVersions.Count == 0 ||
                oldConfig.AgentVersions.Values.Concat(newConfig.AgentVersions.Values).Any(v => v.Contains("latest", StringComparison.OrdinalIgnoreCase)))
                warnings.Add("Agent versions are missing or use 'latest'; an exact model/prompt version was not established.");
        }
        var changed = beforeIds.Intersect(afterIds, StringComparer.OrdinalIgnoreCase)
            .Where(id => oldSources.TryGetValue(id, out var oldSource) && newSources.TryGetValue(id, out var newSource)
                && !string.IsNullOrEmpty(oldSource.ContentHash) && !string.IsNullOrEmpty(newSource.ContentHash)
                && oldSource.ContentHash != newSource.ContentHash).Order(StringComparer.Ordinal).ToList();
        return new(
            beforeIds.Except(afterIds, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
            afterIds.Except(beforeIds, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(), changed,
            Counts(ThemeCounts(before), ThemeCounts(after)), Counts(StanceCounts(before), StanceCounts(after)), warnings);
    }

    private static Dictionary<string, int> ThemeCounts(AnalysisRun run) => run.Grouped.ThemeGroups
        .GroupBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(t => t.Count), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> StanceCounts(AnalysisRun run)
    {
        var unreadable = run.Sources.Where(s => s.ExtractionStatus == "unreadable").Select(s => s.CommentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return run.Categorizations.GroupBy(c => unreadable.Contains(c.CommentId) ? "unreadable - no stance inferred" :
            AnalystEngine.NormalizeStance(AnalystEngine.GetField(c, "stance", "position")) is { Length: > 0 } stance ? stance : "unrecorded",
            StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CountChange> Counts(Dictionary<string, int> before, Dictionary<string, int> after) =>
        before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(key => new CountChange(key, before.GetValueOrDefault(key), after.GetValueOrDefault(key))).ToList();
}
