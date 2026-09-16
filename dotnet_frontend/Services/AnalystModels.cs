using System.Text.Json;

namespace DoedRegulatoryComments.Web.Services;

[Newtonsoft.Json.JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
public sealed class SourcePassage
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Kind { get; set; } = "inline";
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public int? PageNumber { get; set; }
    public bool Truncated { get; set; }
}

[Newtonsoft.Json.JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
public sealed class CommentSourceSnapshot
{
    public string CommentId { get; set; } = string.Empty;
    public int SubmissionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
    public string Commenter { get; set; } = string.Empty;
    public DateTimeOffset? PostedAt { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string ExtractionStatus { get; set; } = "complete";
    public List<string> Warnings { get; set; } = new();
    public List<SourcePassage> Passages { get; set; } = new();
}

[Newtonsoft.Json.JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
public sealed class AnalysisProvenance
{
    public int SchemaVersion { get; set; } = 1;
    public string PipelineVersion { get; set; } = "analyst-workspace-v1";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public int? AvailableComments { get; set; }
    public int FetchedComments { get; set; }
    public int SelectedComments { get; set; }
    public string Scope { get; set; } = "unknown";
    public string QueryScope { get; set; } = "unknown";
    public string SelectionDescription { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public Dictionary<string, string> AgentVersions { get; set; } = new();
    public int BatchSize { get; set; }
    public bool RunValidation { get; set; }
}

public sealed class AnalysisInputMetadata
{
    public int FetchedComments { get; set; }
    public int? AvailableComments { get; set; }
    public bool IsSelection { get; set; }
    public bool UseDocketFilter { get; set; } = true;
    public string Description { get; set; } = string.Empty;
}

public sealed class CommentReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CommentId { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Reviewer { get; set; } = string.Empty;
    public string PrimaryTheme { get; set; } = string.Empty;
    public string CanonicalReason { get; set; } = string.Empty;
    public string Stance { get; set; } = "neutral";
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = "unreviewed";
    public string Notes { get; set; } = string.Empty;
}

public sealed class IssueResponse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RuleSection { get; set; } = string.Empty;
    public string Concern { get; set; } = string.Empty;
    public List<int> SubmissionNumbers { get; set; } = new();
    public string RequestedChange { get; set; } = string.Empty;
    public string DraftResponse { get; set; } = string.Empty;
    public string ReviewStatus { get; set; } = "draft";
    public string Reviewer { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RunReviewState
{
    public List<CommentReview> Revisions { get; set; } = new();
    public List<IssueResponse> Issues { get; set; } = new();
    public List<IssueResponse> IssueHistory { get; set; } = new();
}

public sealed class AnalysisWorkspace
{
    public required AnalysisRun Original { get; init; }
    public required AnalysisRun Effective { get; init; }
    public required RunReviewState State { get; init; }
    public string? Version { get; init; }
}

public sealed class CommentSearchFilter
{
    public string Query { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public string Stance { get; set; } = string.Empty;
    public string ReviewStatus { get; set; } = string.Empty;
    public DateTime? PostedFrom { get; set; }
    public DateTime? PostedTo { get; set; }
}

public sealed class SavedCommentView
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public CommentSearchFilter Filter { get; set; } = new();
}

public sealed class DuplicateGroup
{
    public string Kind { get; set; } = "exact";
    public List<string> CommentIds { get; set; } = new();
    public double Similarity { get; set; }
    public string RepresentativeExcerpt { get; set; } = string.Empty;
    public string Variation { get; set; } = string.Empty;
}

public sealed record WorkspaceItem(string Id, string Kind, string Json, string Version);

public interface IWorkspaceRepository
{
    Task<WorkspaceItem?> GetAsync(string kind, string id, CancellationToken ct = default);
    Task<AnalysisPage<WorkspaceItem>> ListPageAsync(
        string kind, int take = 50, string? continuationToken = null, CancellationToken ct = default);
    Task<WorkspaceItem> SaveAsync(
        string kind, string id, string json, string? expectedVersion, CancellationToken ct = default);
    Task DeleteAsync(string kind, string id, string expectedVersion, CancellationToken ct = default);
}

public sealed class WorkspaceConflictException : InvalidOperationException
{
    public WorkspaceConflictException() : base("This item changed in another session. Reload it before saving again.") { }
}

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";
    public string ContainerName { get; set; } = "analyst-workspace";
}

public static class WorkspaceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static T Read<T>(WorkspaceItem item) =>
        JsonSerializer.Deserialize<T>(item.Json, Options)
        ?? throw new InvalidOperationException($"Workspace item '{item.Id}' contains no data.");

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(Write(value), Options)
        ?? throw new InvalidOperationException("The snapshot could not be copied.");
}
