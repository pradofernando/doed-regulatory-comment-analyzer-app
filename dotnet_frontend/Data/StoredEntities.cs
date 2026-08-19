using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DoedRegulatoryComments.Web.Data;

public class StoredAnalysisRun
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string DocumentId { get; set; } = string.Empty;
    [MaxLength(160)] public string? SessionName { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public int BatchSize { get; set; }
    public int TotalComments { get; set; }
    public bool Succeeded { get; set; }

    public string? ErrorMessage { get; set; }

    public string? OverallSummary { get; set; }
    public string? OverallSentiment { get; set; }

    /// <summary>JSON array of strings.</summary>
    public string PatternsJson { get; set; } = "[]";

    /// <summary>JSON array of strings.</summary>
    public string RecommendationsJson { get; set; } = "[]";

    public string? FollowUpThreadId { get; set; }

    public List<StoredCategorization> Categorizations { get; set; } = new();
    public List<StoredThemeGroup> ThemeGroups { get; set; } = new();
    public List<StoredFollowUpTurn> FollowUpHistory { get; set; } = new();
}

public class StoredCategorization
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }
    [ForeignKey(nameof(RunId))] public StoredAnalysisRun? Run { get; set; }

    public int SubmissionNumber { get; set; }

    [MaxLength(128)] public string CommentId { get; set; } = string.Empty;

    /// <summary>The raw text the agent returned (may be JSON or prose).</summary>
    public string RawResponse { get; set; } = string.Empty;

    /// <summary>JSON serialization of the parsed dictionary (may be "{}" if unparsed).</summary>
    public string ParsedJson { get; set; } = "{}";

    [MaxLength(64)] public string TextSource { get; set; } = "inline";
    public int AttachmentsExtracted { get; set; }
}

public class StoredThemeGroup
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }
    [ForeignKey(nameof(RunId))] public StoredAnalysisRun? Run { get; set; }

    public int Position { get; set; }

    public string GroupName { get; set; } = string.Empty;
    public string GroupDescription { get; set; } = string.Empty;
    public int Count { get; set; }

    /// <summary>JSON array of ints.</summary>
    public string SubmissionNumbersJson { get; set; } = "[]";

    /// <summary>JSON object: stance -> count.</summary>
    public string StanceDistributionJson { get; set; } = "{}";

    /// <summary>JSON array of strings.</summary>
    public string CommonArgumentsJson { get; set; } = "[]";
}

public class StoredFollowUpTurn
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }
    [ForeignKey(nameof(RunId))] public StoredAnalysisRun? Run { get; set; }

    public int Position { get; set; }

    [MaxLength(16)] public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
