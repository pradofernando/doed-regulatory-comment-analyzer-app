using System.Collections.Concurrent;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>Lifecycle state of a detached analysis job.</summary>
public enum AnalysisJobState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// A single analysis run executing on the server, independent of any Blazor circuit.
/// The page reads this to render progress and pick up the result, so navigating away
/// (or a brief reconnect) doesn't kill an in-flight run.
/// </summary>
public sealed class AnalysisJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public string DocumentId { get; init; } = string.Empty;
    public int TotalComments { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public AnalysisJobState State { get; set; } = AnalysisJobState.Running;
    public AnalysisProgress? Progress { get; set; }

    /// <summary>When the current phase began — used to estimate per-phase time remaining.</summary>
    public DateTimeOffset? PhaseStartedAt { get; set; }

    public AnalysisRun? Run { get; set; }
    public Guid? SavedRunId { get; set; }
    public string? Error { get; set; }

    internal CancellationTokenSource Cts { get; } = new();
    internal Task? Worker { get; set; }

    public bool IsActive => State == AnalysisJobState.Running;

    /// <summary>Wall-clock time elapsed since the job started (frozen once it finishes).</summary>
    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

/// <summary>
/// Runs analysis jobs detached from the request/circuit so they survive page navigation.
/// A fresh DI scope is created per job to safely resolve the scoped
/// <see cref="FoundryAnalysisService"/> and <see cref="IAnalysisRepository"/>.
/// Registered as a singleton.
/// </summary>
public sealed class AnalysisJobManager : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalysisJobManager> _logger;
    private readonly OperationalTelemetry _telemetry;
    private readonly ConcurrentDictionary<Guid, AnalysisJob> _jobs = new();

    public AnalysisJobManager(
        IServiceScopeFactory scopeFactory,
        ILogger<AnalysisJobManager> logger,
        OperationalTelemetry telemetry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _telemetry = telemetry;
    }

    /// <summary>Kicks off a new background analysis job and returns immediately.</summary>
    public AnalysisJob Start(string documentId, IReadOnlyList<CommentResource> comments, ApiSettings settings)
    {
        var job = new AnalysisJob
        {
            DocumentId = documentId,
            TotalComments = comments.Count,
            Progress = new AnalysisProgress { Phase = "Starting", Message = "Connecting…", Total = comments.Count },
            PhaseStartedAt = DateTimeOffset.UtcNow,
        };
        _jobs[job.Id] = job;

        // Snapshot the inputs onto the job so the worker doesn't touch circuit-scoped state.
        job.Worker = Task.Run(() => RunAsync(job, comments, settings));
        return job;
    }

    public AnalysisJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Most recent job (running or finished) for a document, used to re-attach the page.</summary>
    public AnalysisJob? GetLatestForDocument(string? documentId)
    {
        if (string.IsNullOrEmpty(documentId)) return null;
        return _jobs.Values
            .Where(j => string.Equals(j.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.StartedAt)
            .FirstOrDefault();
    }

    public void Cancel(Guid id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            try { job.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RunAsync(AnalysisJob job, IReadOnlyList<CommentResource> comments, ApiSettings settings)
    {
        using var activity = _telemetry.StartAnalysis(job.Id, comments.Count);
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["AnalysisJobId"] = job.Id,
        });
        using var scope = _scopeFactory.CreateScope();
        var foundry = scope.ServiceProvider.GetRequiredService<IAnalysisRunner>();
        var repo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();

        var lastPhase = job.Progress?.Phase;
        var progress = new InlineProgress<AnalysisProgress>(p =>
        {
            if (!string.Equals(p.Phase, lastPhase, StringComparison.Ordinal))
            {
                if (job.PhaseStartedAt is { } previousPhaseStart)
                    _telemetry.RecordPhaseDuration(lastPhase, DateTimeOffset.UtcNow - previousPhaseStart);
                lastPhase = p.Phase;
                job.PhaseStartedAt = DateTimeOffset.UtcNow;
            }
            job.Progress = p;
        });

        try
        {
            var run = await foundry.RunAsync(job.DocumentId, comments, settings, progress, job.Cts.Token)
                .ConfigureAwait(false);
            job.Cts.Token.ThrowIfCancellationRequested();
            job.Run = run;

            try
            {
                job.SavedRunId = await repo.SaveRunAsync(run, job.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception saveEx)
            {
                _logger.LogWarning(saveEx, "Failed to persist analysis run for document {DocId}.", job.DocumentId);
            }

            if (run.Succeeded)
            {
                job.State = AnalysisJobState.Completed;
            }
            else
            {
                job.State = AnalysisJobState.Failed;
                job.Error = run.ErrorMessage;
            }
        }
        catch (OperationCanceledException)
        {
            job.State = AnalysisJobState.Cancelled;
            job.Error = "Analysis was cancelled.";
        }
        catch (Exception ex)
        {
            job.State = AnalysisJobState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Analysis job failed for document {DocId}.", job.DocumentId);
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (job.PhaseStartedAt is { } phaseStart)
                _telemetry.RecordPhaseDuration(lastPhase, job.FinishedAt.Value - phaseStart);
            _telemetry.RecordAnalysisCompleted(job.State, job.Elapsed);
            activity?.SetTag("analysis.outcome", job.State.ToString().ToLowerInvariant());
            try { job.Cts.Dispose(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        foreach (var job in _jobs.Values)
        {
            try
            {
                if (job.IsActive) job.Cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
