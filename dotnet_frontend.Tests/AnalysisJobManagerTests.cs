using DoedRegulatoryComments.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AnalysisJobManagerTests
{
    private static AnalysisJobManager NewManager()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new AnalysisJobManager(
            scopeFactory,
            NullLogger<AnalysisJobManager>.Instance,
            new OperationalTelemetry(Options.Create(new FoundryCostOptions())));
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownId()
    {
        var manager = NewManager();
        Assert.Null(manager.Get(Guid.NewGuid()));
    }

    [Fact]
    public void GetLatestForDocument_ReturnsNull_WhenNoneOrBlank()
    {
        var manager = NewManager();
        Assert.Null(manager.GetLatestForDocument("ED-2025-0001"));
        Assert.Null(manager.GetLatestForDocument(null));
        Assert.Null(manager.GetLatestForDocument(""));
    }

    [Fact]
    public void AnalysisJob_IsActive_TracksState()
    {
        var job = new AnalysisJob { DocumentId = "ED-1" };
        Assert.True(job.IsActive);

        job.State = AnalysisJobState.Completed;
        Assert.False(job.IsActive);
    }

    [Fact]
    public void AnalysisJob_Elapsed_FreezesAfterFinish()
    {
        var job = new AnalysisJob { DocumentId = "ED-1" };
        job.FinishedAt = job.StartedAt + TimeSpan.FromSeconds(3);
        Assert.Equal(3, job.Elapsed.TotalSeconds, precision: 1);
    }

    [Fact]
    public async Task Start_CompletesAndPersistsRun()
    {
        var expectedRun = new AnalysisRun
        {
            DocumentId = "ED-1",
            TotalComments = 1,
            Succeeded = true,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        var repository = new FakeRepository();
        using var provider = BuildProvider(
            new FakeRunner((_, _, _, progress, _) =>
            {
                progress?.Report(new AnalysisProgress { Phase = "Grouping", Current = 1, Total = 1 });
                return Task.FromResult(expectedRun);
            }),
            repository);
        using var manager = NewManager(provider);

        var job = manager.Start(
            "ED-1",
            [new CommentResource { Id = "COMMENT-1" }],
            new ApiSettings());
        await job.Worker!;

        Assert.Equal(AnalysisJobState.Completed, job.State);
        Assert.Same(expectedRun, job.Run);
        Assert.Equal(repository.SavedId, job.SavedRunId);
        Assert.Same(expectedRun, repository.SavedRun);
    }

    [Fact]
    public async Task Cancel_StopsRunningJob()
    {
        using var provider = BuildProvider(
            new FakeRunner(async (_, _, _, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new AnalysisRun();
            }),
            new FakeRepository());
        using var manager = NewManager(provider);
        var job = manager.Start(
            "ED-1",
            [new CommentResource { Id = "COMMENT-1" }],
            new ApiSettings());

        manager.Cancel(job.Id);
        await job.Worker!;

        Assert.Equal(AnalysisJobState.Cancelled, job.State);
        Assert.Equal("Analysis was cancelled.", job.Error);
    }

    [Fact]
    public async Task CancelledResult_IsNotPersistedWhenRunnerReturnsAfterCancellation()
    {
        var repository = new FakeRepository();
        var runnerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = BuildProvider(
            new FakeRunner(async (_, _, _, _, _) =>
            {
                runnerStarted.SetResult();
                await allowReturn.Task;
                return new AnalysisRun { DocumentId = "ED-1", Succeeded = false };
            }),
            repository);
        using var manager = NewManager(provider);
        var job = manager.Start(
            "ED-1",
            [new CommentResource { Id = "COMMENT-1" }],
            new ApiSettings());
        await runnerStarted.Task;

        manager.Cancel(job.Id);
        allowReturn.SetResult();
        await job.Worker!;

        Assert.Equal(AnalysisJobState.Cancelled, job.State);
        Assert.Null(repository.SavedRun);
    }

    [Fact]
    public async Task CancellationDuringPersistence_MarksJobCancelled()
    {
        var repository = new FakeRepository { CancelOnSave = true };
        using var provider = BuildProvider(
            new FakeRunner((documentId, comments, settings, progress, ct) =>
                Task.FromResult(new AnalysisRun
                {
                    DocumentId = documentId,
                    TotalComments = comments.Count,
                    Succeeded = true,
                })),
            repository);
        using var manager = NewManager(provider);
        var job = manager.Start(
            "ED-1",
            [new CommentResource { Id = "COMMENT-1" }],
            new ApiSettings());

        await repository.SaveStarted.Task;
        manager.Cancel(job.Id);
        repository.AllowSaveToContinue.SetResult();
        await job.Worker!;

        Assert.Equal(AnalysisJobState.Cancelled, job.State);
        Assert.Null(job.SavedRunId);
    }

    private static ServiceProvider BuildProvider(IAnalysisRunner runner, IAnalysisRepository repository)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => runner);
        services.AddScoped(_ => repository);
        return services.BuildServiceProvider();
    }

    private static AnalysisJobManager NewManager(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AnalysisJobManager>.Instance,
            new OperationalTelemetry(Options.Create(new FoundryCostOptions())));

    private sealed class FakeRunner(
        Func<string, IReadOnlyList<CommentResource>, ApiSettings, IProgress<AnalysisProgress>?, CancellationToken, Task<AnalysisRun>> run)
        : IAnalysisRunner
    {
        public Task<AnalysisRun> RunAsync(
            string documentId,
            IReadOnlyList<CommentResource> comments,
            ApiSettings settings,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken) =>
            run(documentId, comments, settings, progress, cancellationToken);
    }

    private sealed class FakeRepository : IAnalysisRepository
    {
        public Guid SavedId { get; } = Guid.NewGuid();
        public AnalysisRun? SavedRun { get; private set; }
        public bool CancelOnSave { get; init; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSaveToContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Guid> SaveRunAsync(AnalysisRun run, CancellationToken ct = default)
        {
            SaveStarted.TrySetResult();
            if (CancelOnSave)
            {
                await AllowSaveToContinue.Task;
                ct.ThrowIfCancellationRequested();
            }
            SavedRun = run;
            return SavedId;
        }

        public Task AppendFollowUpAsync(Guid runId, FollowUpTurn turn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFollowUpThreadAsync(Guid runId, string threadId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameRunAsync(Guid runId, string? sessionName, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AnalysisRunSummary>> ListAsync(AnalysisListFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AnalysisRunSummary>>(Array.Empty<AnalysisRunSummary>());
        public Task<AnalysisRun?> LoadRunAsync(Guid id, CancellationToken ct = default) => Task.FromResult<AnalysisRun?>(null);
        public Task DeleteRunAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
