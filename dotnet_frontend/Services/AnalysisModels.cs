using System.Text.Json.Serialization;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// One categorization produced by Agent 1 (per-comment).
/// </summary>
public class CategorizationResult
{
    public int SubmissionNumber { get; set; }
    public string CommentId { get; set; } = string.Empty;
    public string RowData { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;

    /// <summary>Parsed JSON the agent returned (if it returned JSON).</summary>
    public Dictionary<string, object?> Parsed { get; set; } = new();

    /// <summary>Where the comment text came from: "inline", "attachment", "inline+attachment", or "none".</summary>
    public string TextSource { get; set; } = "inline";

    /// <summary>Number of attachments whose text was successfully extracted.</summary>
    public int AttachmentsExtracted { get; set; }
}

/// <summary>
/// One theme group inside the collective analysis returned by Agent 2.
/// </summary>
public class ThemeGroup
{
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = string.Empty;
    [JsonPropertyName("group_description")] public string GroupDescription { get; set; } = string.Empty;
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("submission_numbers")] public List<int> SubmissionNumbers { get; set; } = new();
    [JsonPropertyName("stance_distribution")] public Dictionary<string, int> StanceDistribution { get; set; } = new();
    [JsonPropertyName("common_arguments")] public List<string> CommonArguments { get; set; } = new();
}

/// <summary>
/// Final collective analysis from Agent 2.
/// </summary>
public class GroupedAnalysis
{
    [JsonPropertyName("overall_summary")] public string? OverallSummary { get; set; }
    [JsonPropertyName("theme_groups")] public List<ThemeGroup> ThemeGroups { get; set; } = new();
    [JsonPropertyName("patterns")] public List<string> Patterns { get; set; } = new();
    [JsonPropertyName("recommendations")] public List<string> Recommendations { get; set; } = new();
    [JsonPropertyName("overall_sentiment")] public string? OverallSentiment { get; set; }

    /// <summary>Raw text from the agent — used when JSON parsing fails.</summary>
    [JsonIgnore] public string RawResponse { get; set; } = string.Empty;

    /// <summary>True when the agent returned parseable JSON.</summary>
    [JsonIgnore] public bool ParsedSuccessfully { get; set; }
}

/// <summary>
/// Aggregate result of a single end-to-end analysis run.
/// </summary>
public class AnalysisRun
{
    public string DocumentId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int BatchSize { get; set; }
    public int TotalComments { get; set; }
    public List<CategorizationResult> Categorizations { get; set; } = new();
    public GroupedAnalysis Grouped { get; set; } = new();
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>The actual comment objects analyzed (mirrors what FoundryAnalysisService received). Used by exporters and the chat-priming step.</summary>
    [JsonIgnore]
    public IReadOnlyList<CommentResource> Comments { get; set; } = Array.Empty<CommentResource>();

    /// <summary>Pre-extracted attachment text keyed by comment ID. Populated by FoundryAnalysisService for downstream exporters.</summary>
    [JsonIgnore]
    public Dictionary<string, AttachmentExtractionResult> AttachmentText { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Foundry thread ID for the follow-up Q&amp;A agent. Created lazily when the user opens the chat panel.</summary>
    public string? FollowUpThreadId { get; set; }

    /// <summary>Full message history of the follow-up chat (user + agent turns, in order).</summary>
    public List<FollowUpTurn> FollowUpHistory { get; set; } = new();
}

/// <summary>
/// One message in the follow-up Q&amp;A chat (either user or agent).
/// </summary>
public record FollowUpTurn(string Role, string Text, DateTimeOffset At);

/// <summary>
/// Progress event surfaced to the UI while a run is in flight.
/// </summary>
public class AnalysisProgress
{
    public string Phase { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = string.Empty;
    public double Percent => Total > 0 ? Math.Clamp(100.0 * Current / Total, 0, 100) : 0;
}
