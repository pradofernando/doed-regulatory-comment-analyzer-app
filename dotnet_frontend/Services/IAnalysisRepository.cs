namespace DoedRegulatoryComments.Web.Services;

public static class AnalysisSessionNames
{
    public const int MaxLength = 160;

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > MaxLength)
            throw new ArgumentException(
                $"Session name must be {MaxLength} characters or fewer.", nameof(value));
        return normalized;
    }
}

public sealed record AnalysisRunSummary(
    Guid Id,
    string DocumentId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int TotalComments,
    int ThemeCount,
    bool Succeeded,
    string? ErrorMessage,
    string? OverallSentiment,
    string? SessionName = null);

public sealed record AnalysisListFilter(string? DocumentId = null, bool? SucceededOnly = null, int Take = 100);

public sealed record AnalysisPage<T>(IReadOnlyList<T> Items, string? ContinuationToken)
{
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
}

public interface IAnalysisRepository
{
    Task<Guid> SaveRunAsync(AnalysisRun run, CancellationToken ct = default);
    Task AppendFollowUpAsync(Guid runId, FollowUpTurn turn, CancellationToken ct = default);
    Task SetFollowUpThreadAsync(Guid runId, string threadId, CancellationToken ct = default);
    Task RenameRunAsync(Guid runId, string? sessionName, CancellationToken ct = default);
    Task<IReadOnlyList<AnalysisRunSummary>> ListAsync(AnalysisListFilter filter, CancellationToken ct = default);
    async Task<AnalysisPage<AnalysisRunSummary>> ListPageAsync(
        AnalysisListFilter filter,
        string? continuationToken = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(continuationToken))
            throw new ArgumentException("This repository does not support continuation tokens.", nameof(continuationToken));
        return new AnalysisPage<AnalysisRunSummary>(await ListAsync(filter, ct), null);
    }
    Task<AnalysisRun?> LoadRunAsync(Guid id, CancellationToken ct = default);
    Task DeleteRunAsync(Guid id, CancellationToken ct = default);
}

public static class AnalysisDocumentIds
{
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}