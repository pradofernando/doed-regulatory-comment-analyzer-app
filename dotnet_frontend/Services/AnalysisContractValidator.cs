namespace DoedRegulatoryComments.Web.Services;

internal sealed record AnalysisContractEvaluation(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

internal static class AnalysisContractValidator
{
    private static readonly HashSet<string> AllowedSentiments = new(
        ["supportive", "opposed", "neutral", "mixed"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedCommenterTypes = new(
        ["individual", "organization"],
        StringComparer.OrdinalIgnoreCase);

    public static AnalysisContractEvaluation EvaluateCategorization(
        IReadOnlyDictionary<string, object?> fields)
    {
        var errors = new List<string>();
        RequireString(fields, "primary_topic", errors);
        var sentiment = RequireString(fields, "sentiment", errors);
        if (sentiment is not null && !AllowedSentiments.Contains(sentiment))
            errors.Add("sentiment must be supportive, opposed, neutral, or mixed");
        RequireString(fields, "stance", errors);
        RequireStringList(fields, "key_concerns", errors);
        var commenterType = RequireString(fields, "commenter_type", errors);
        if (commenterType is not null && !AllowedCommenterTypes.Contains(commenterType))
            errors.Add("commenter_type must be individual or organization");
        return new AnalysisContractEvaluation(errors);
    }

    public static AnalysisContractEvaluation EvaluateGroupedAnalysis(
        GroupedAnalysis analysis,
        int totalComments)
    {
        var errors = new List<string>();
        if (!analysis.ParsedSuccessfully)
            errors.Add("response is not parseable grouped-analysis JSON");
        if (string.IsNullOrWhiteSpace(analysis.OverallSummary))
            errors.Add("overall_summary is required");
        if (string.IsNullOrWhiteSpace(analysis.OverallSentiment))
            errors.Add("overall_sentiment is required");
        if (totalComments > 0 && analysis.ThemeGroups.Count == 0)
            errors.Add("theme_groups must contain at least one group");

        var submissions = new List<int>();
        foreach (var group in analysis.ThemeGroups)
        {
            if (string.IsNullOrWhiteSpace(group.GroupName))
                errors.Add("every theme group requires group_name");
            if (string.IsNullOrWhiteSpace(group.GroupDescription))
                errors.Add($"theme group '{group.GroupName}' requires group_description");
            if (group.Count != group.SubmissionNumbers.Count)
                errors.Add($"theme group '{group.GroupName}' count does not match submission_numbers");
            if (group.StanceDistribution.Values.Any(value => value < 0))
                errors.Add($"theme group '{group.GroupName}' has a negative stance count");
            if (group.StanceDistribution.Values.Sum() != group.Count)
                errors.Add($"theme group '{group.GroupName}' stance counts do not match count");
            submissions.AddRange(group.SubmissionNumbers);
        }

        var duplicates = submissions
            .GroupBy(value => value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order()
            .ToList();
        if (duplicates.Count > 0)
            errors.Add($"submissions appear more than once: {string.Join(", ", duplicates)}");

        var expected = Enumerable.Range(1, Math.Max(0, totalComments)).ToHashSet();
        var actual = submissions.ToHashSet();
        var missing = expected.Except(actual).Order().ToList();
        if (missing.Count > 0)
            errors.Add($"submissions are missing: {string.Join(", ", missing)}");
        var unexpected = actual.Except(expected).Order().ToList();
        if (unexpected.Count > 0)
            errors.Add($"submission numbers are out of range: {string.Join(", ", unexpected)}");

        return new AnalysisContractEvaluation(errors);
    }

    private static string? RequireString(
        IReadOnlyDictionary<string, object?> fields,
        string key,
        List<string> errors)
    {
        if (!fields.TryGetValue(key, out var value)
            || value is not string text
            || string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{key} is required and must be a non-empty string");
            return null;
        }
        return text.Trim();
    }

    private static void RequireStringList(
        IReadOnlyDictionary<string, object?> fields,
        string key,
        List<string> errors)
    {
        if (!fields.TryGetValue(key, out var value)
            || value is not IEnumerable<object?> values
            || values.Any(item => item is not string text || string.IsNullOrWhiteSpace(text)))
        {
            errors.Add($"{key} is required and must be an array of non-empty strings");
        }
    }
}