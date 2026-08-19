using System.Text.Json;
using System.Globalization;
using System.Text;
using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Persists <see cref="AnalysisRun"/> instances and follow-up Q&amp;A turns to the relational store
/// and rehydrates them back into the in-memory model used by the UI and exporters.
/// </summary>
/// <remarks>
/// Uses an <see cref="IDbContextFactory{TContext}"/> so background analysis tasks can create
/// their own DbContext per call (Blazor circuits are scoped and a long-running task may outlive
/// a single scope's DbContext lifetime).
/// </remarks>
public sealed class AnalysisRepository : IAnalysisRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly IDbContextFactory<AnalysisDbContext> _factory;
    private readonly ILogger<AnalysisRepository> _logger;

    public AnalysisRepository(IDbContextFactory<AnalysisDbContext> factory, ILogger<AnalysisRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<Guid> SaveRunAsync(AnalysisRun run, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var stored = new StoredAnalysisRun
        {
            Id = Guid.NewGuid(),
            DocumentId = run.DocumentId,
            SessionName = AnalysisSessionNames.Normalize(run.SessionName),
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            BatchSize = run.BatchSize,
            TotalComments = run.TotalComments,
            Succeeded = run.Succeeded,
            ErrorMessage = run.ErrorMessage,
            OverallSummary = run.Grouped.OverallSummary,
            OverallSentiment = run.Grouped.OverallSentiment,
            PatternsJson = JsonSerializer.Serialize(run.Grouped.Patterns, JsonOpts),
            RecommendationsJson = JsonSerializer.Serialize(run.Grouped.Recommendations, JsonOpts),
            FollowUpThreadId = run.FollowUpThreadId,
        };

        foreach (var c in run.Categorizations)
        {
            stored.Categorizations.Add(new StoredCategorization
            {
                RunId = stored.Id,
                SubmissionNumber = c.SubmissionNumber,
                CommentId = c.CommentId,
                RawResponse = c.RawResponse,
                ParsedJson = JsonSerializer.Serialize(c.Parsed, JsonOpts),
                TextSource = c.TextSource,
                AttachmentsExtracted = c.AttachmentsExtracted,
            });
        }

        for (var i = 0; i < run.Grouped.ThemeGroups.Count; i++)
        {
            var g = run.Grouped.ThemeGroups[i];
            stored.ThemeGroups.Add(new StoredThemeGroup
            {
                RunId = stored.Id,
                Position = i,
                GroupName = g.GroupName,
                GroupDescription = g.GroupDescription,
                Count = g.Count,
                SubmissionNumbersJson = JsonSerializer.Serialize(g.SubmissionNumbers, JsonOpts),
                StanceDistributionJson = JsonSerializer.Serialize(g.StanceDistribution, JsonOpts),
                CommonArgumentsJson = JsonSerializer.Serialize(g.CommonArguments, JsonOpts),
            });
        }

        for (var i = 0; i < run.FollowUpHistory.Count; i++)
        {
            var t = run.FollowUpHistory[i];
            stored.FollowUpHistory.Add(new StoredFollowUpTurn
            {
                RunId = stored.Id,
                Position = i,
                Role = t.Role,
                Text = t.Text,
                At = t.At,
            });
        }

        db.Runs.Add(stored);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Saved analysis run {RunId} for document {DocumentId} ({Count} comments, {Themes} themes).",
            stored.Id, stored.DocumentId, stored.TotalComments, stored.ThemeGroups.Count);

        return stored.Id;
    }

    /// <summary>Append a single follow-up turn (user or agent) to an existing run.</summary>
    public async Task AppendFollowUpAsync(Guid runId, FollowUpTurn turn, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var nextPos = await db.FollowUpTurns
            .Where(t => t.RunId == runId)
            .Select(t => (int?)t.Position)
            .MaxAsync(ct).ConfigureAwait(false);

        db.FollowUpTurns.Add(new StoredFollowUpTurn
        {
            RunId = runId,
            Position = (nextPos ?? -1) + 1,
            Role = turn.Role,
            Text = turn.Text,
            At = turn.At,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Update the follow-up thread ID once Foundry creates it.</summary>
    public async Task SetFollowUpThreadAsync(Guid runId, string threadId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run is null) return;
        run.FollowUpThreadId = threadId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RenameRunAsync(Guid runId, string? sessionName, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run is null) return;
        run.SessionName = AnalysisSessionNames.Normalize(sessionName);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AnalysisRunSummary>> ListAsync(
        AnalysisListFilter filter,
        CancellationToken ct = default) =>
        (await ListPageAsync(filter, cancellationToken: ct).ConfigureAwait(false)).Items;

    public async Task<AnalysisPage<AnalysisRunSummary>> ListPageAsync(
        AnalysisListFilter filter,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var take = Math.Clamp(filter.Take, 1, 500);
        var offset = DecodeOffset(continuationToken);

        var q = db.Runs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.DocumentId))
            q = q.Where(r => r.DocumentId.Contains(filter.DocumentId));
        if (filter.SucceededOnly is true)
            q = q.Where(r => r.Succeeded);

        var projected = q.Select(r => new AnalysisRunSummary(
                r.Id,
                r.DocumentId,
                r.StartedAt,
                r.CompletedAt,
                r.TotalComments,
                r.ThemeGroups.Count,
                r.Succeeded,
                r.ErrorMessage,
                r.OverallSentiment,
                r.SessionName));

        if (db.Database.IsSqlite())
        {
            var rows = await projected.ToListAsync(cancellationToken).ConfigureAwait(false);
            var page = rows
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
                .Skip(offset)
                .Take(take + 1)
                .ToList();
            return ToPage(page, take, offset);
        }

        var databasePage = await projected
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Skip(offset)
            .Take(take + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToPage(databasePage, take, offset);
    }

    public async Task<AnalysisRun?> LoadRunAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var stored = await db.Runs
            .AsNoTracking()
            .Include(r => r.Categorizations)
            .Include(r => r.ThemeGroups)
            .Include(r => r.FollowUpHistory)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (stored is null) return null;

        var run = new AnalysisRun
        {
            SessionName = stored.SessionName,
            DocumentId = stored.DocumentId,
            StartedAt = stored.StartedAt,
            CompletedAt = stored.CompletedAt,
            BatchSize = stored.BatchSize,
            TotalComments = stored.TotalComments,
            Succeeded = stored.Succeeded,
            ErrorMessage = stored.ErrorMessage,
            FollowUpThreadId = stored.FollowUpThreadId,
        };

        run.Grouped = new GroupedAnalysis
        {
            OverallSummary = stored.OverallSummary,
            OverallSentiment = stored.OverallSentiment,
            Patterns = SafeDeserialize<List<string>>(stored.PatternsJson) ?? new(),
            Recommendations = SafeDeserialize<List<string>>(stored.RecommendationsJson) ?? new(),
            ParsedSuccessfully = stored.ThemeGroups.Count > 0,
        };

        foreach (var g in stored.ThemeGroups.OrderBy(g => g.Position))
        {
            run.Grouped.ThemeGroups.Add(new ThemeGroup
            {
                GroupName = g.GroupName,
                GroupDescription = g.GroupDescription,
                Count = g.Count,
                SubmissionNumbers = SafeDeserialize<List<int>>(g.SubmissionNumbersJson) ?? new(),
                StanceDistribution = SafeDeserialize<Dictionary<string, int>>(g.StanceDistributionJson) ?? new(),
                CommonArguments = SafeDeserialize<List<string>>(g.CommonArgumentsJson) ?? new(),
            });
        }

        foreach (var c in stored.Categorizations.OrderBy(c => c.SubmissionNumber))
        {
            run.Categorizations.Add(new CategorizationResult
            {
                SubmissionNumber = c.SubmissionNumber,
                CommentId = c.CommentId,
                RawResponse = c.RawResponse,
                Parsed = SafeDeserialize<Dictionary<string, object?>>(c.ParsedJson) ?? new(),
                TextSource = c.TextSource,
                AttachmentsExtracted = c.AttachmentsExtracted,
            });
        }

        foreach (var t in stored.FollowUpHistory.OrderBy(t => t.Position))
        {
            run.FollowUpHistory.Add(new FollowUpTurn(t.Role, t.Text, t.At));
        }

        return run;
    }

    public async Task DeleteRunAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (run is null) return;
        db.Runs.Remove(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static T? SafeDeserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return default; }
    }

    private static AnalysisPage<AnalysisRunSummary> ToPage(
        List<AnalysisRunSummary> rows,
        int take,
        int offset)
    {
        var hasMore = rows.Count > take;
        var items = rows.Take(take).ToList();
        return new AnalysisPage<AnalysisRunSummary>(
            items,
            hasMore ? EncodeOffset(offset + items.Count) : null);
    }

    private static string EncodeOffset(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeOffset(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
        }
        throw new ArgumentException("The continuation token is invalid.", nameof(token));
    }
}
