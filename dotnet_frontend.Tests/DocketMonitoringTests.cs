using DoedRegulatoryComments.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class DocketMonitoringTests
{
    [Fact]
    public async Task FirstCheckIsBaseline_ThenNewAndModifiedChangesArePersistentAndDeduplicated()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var source = new FakeSource();
        var clock = new TestClock();
        var runner = new FakeRunner();
        var service = Service(store, source, runner, clock);
        var watch = await service.SaveAsync(new DocketWatch { DocketId = "ed-test-2026", Name = "Test" }, null);
        await service.CheckAsync(watch.Watch.Id);
        Assert.Empty((await service.ListNotificationsAsync()).Items);
        source.Comments[0].Attributes.Title = "Modified";
        source.Comments.Add(TestData.Comment("B", comment: "A new comment"));
        await service.CheckAsync(watch.Watch.Id);
        await service.CheckAsync(watch.Watch.Id);
        var note = WorkspaceJson.Read<DocketNotification>(Assert.Single((await service.ListNotificationsAsync()).Items));
        Assert.Contains("1 new and 1 modified", note.Title);
        Assert.Equal(2, note.CommentIds.Count);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(2, (await service.ListAsync()).Single().Watch.PendingIds.Count);
    }

    [Fact]
    public async Task PartialAndFailedFetchesNeverAdvanceTheBaseline()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var source = new FakeSource();
        var service = Service(store, source, new FakeRunner(), new TestClock());
        var watch = await service.SaveAsync(new DocketWatch { DocketId = "ED-TEST", Name = "Test" }, null);
        await service.CheckAsync(watch.Watch.Id);
        var original = (await service.ListAsync()).Single().Watch;
        source.Comments.Add(TestData.Comment("B"));
        source.TotalOverride = 8;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(watch.Watch.Id));
        var after = (await service.ListAsync()).Single().Watch;
        Assert.Equal(original.Baseline, after.Baseline);
        Assert.Equal(original.LastCheckedAt, after.LastCheckedAt);
        Assert.Contains("Incomplete", after.LastError);
        source.Failure = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(watch.Watch.Id));
        Assert.Single((await service.ListAsync()).Single().Watch.Baseline);
    }

    [Fact]
    public async Task DeadlineRemindersAreDeduplicatedAndOrderedNewestFirst()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var clock = new TestClock();
        var service = Service(store, new FakeSource(), new FakeRunner(), clock);
        var watch = await service.SaveAsync(new DocketWatch { DocketId = "ED-TEST", Name = "Test", DeadlineAt = clock.Now.AddDays(6) }, null);
        await service.CheckAsync(watch.Watch.Id);
        await service.CheckAsync(watch.Watch.Id);
        Assert.Single((await service.ListNotificationsAsync()).Items);
        clock.Now = clock.Now.AddDays(5.5);
        await service.CheckAsync(watch.Watch.Id);
        var notes = (await service.ListNotificationsAsync()).Items.Select(WorkspaceJson.Read<DocketNotification>).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Contains("one-day", notes[0].Body);
        Assert.Contains("one-week", notes[1].Body);
    }

    [Fact]
    public async Task AutomaticAnalysisRetriesPendingChangesAfterModelFailure()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var source = new FakeSource();
        var runner = new FakeRunner { Fail = true };
        var service = Service(store, source, runner, new TestClock());
        var watch = await service.SaveAsync(new DocketWatch { DocketId = "ED-TEST", Name = "Test", AutoAnalyze = true }, null);
        await service.CheckAsync(watch.Watch.Id);
        source.Comments.Add(TestData.Comment("B"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(watch.Watch.Id));
        Assert.Single((await service.ListAsync()).Single().Watch.PendingIds);
        runner.Fail = false;
        await service.CheckAsync(watch.Watch.Id);
        var finished = (await service.ListAsync()).Single().Watch;
        Assert.Empty(finished.PendingIds);
        Assert.NotNull(finished.LastRunId);
        Assert.Equal(2, runner.Calls);
        Assert.True(runner.Input?.IsSelection);
    }

    [Fact]
    public async Task ActiveLeasePreventsDuplicateWorkers_AndWatchSavesCheckVersions()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var source = new FakeSource();
        var clock = new TestClock();
        var service = Service(store, source, new FakeRunner(), clock);
        var watch = await service.SaveAsync(new DocketWatch { DocketId = "ED-TEST", Name = "Test" }, null);
        await store.Workspace.SaveAsync(DocketMonitorService.LeaseKind, watch.Watch.Id.ToString("D"),
            WorkspaceJson.Write(new { owner = "other-worker", expiresAt = clock.Now.AddMinutes(10) }), null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(watch.Watch.Id));
        Assert.Equal(0, source.Calls);
        watch.Watch.Name = "Updated";
        await service.SaveAsync(watch.Watch, watch.Version);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => service.SaveAsync(watch.Watch, watch.Version));
    }

    [Fact]
    public void Compare_ReportsChangedSourcesScopeAndConfigurationWithoutInferringRemoval()
    {
        var before = AnalystTestData.Run();
        var after = WorkspaceJson.Clone(before);
        after.Provenance!.Model = "different-model";
        after.Sources[0].ContentHash = "changed";
        after.Categorizations.RemoveAt(1);
        var comparison = RunComparisonService.Compare(before, after);
        Assert.Contains("COMMENT-1", comparison.ChangedSources);
        Assert.Contains("COMMENT-2", comparison.OnlyBefore);
        Assert.Contains(comparison.Warnings, w => w.Contains("selected"));
        Assert.Contains(comparison.Warnings, w => w.Contains("settings differ"));
        Assert.Contains(comparison.Warnings, w => w.Contains("does not mean removal"));
        before.Provenance = null;
        Assert.Contains(RunComparisonService.Compare(before, after).Warnings, w => w.Contains("provenance is missing"));
    }

    private static DocketMonitorService Service(AnalystSqliteStore store, FakeSource source, FakeRunner runner, TestClock clock) =>
        new(store.Workspace, source, runner, store.Analyses,
            new ApiSettingsStore(new ConfigurationBuilder().Build(), new TestEnvironment()),
            Options.Create(new MonitoringOptions()), clock, NullLogger<DocketMonitorService>.Instance);

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Synthetic tests";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeSource : IDocketCommentSource
    {
        public List<CommentResource> Comments { get; } = [TestData.Comment("A", comment: "Original comment")];
        public int Calls { get; private set; }
        public bool Failure { get; set; }
        public int? TotalOverride { get; set; }
        public Task<FetchCommentsResult> FetchAsync(string docketId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new FetchCommentsResult
            {
                Success = !Failure, ErrorMessage = Failure ? "Synthetic fetch failed" : null,
                Comments = WorkspaceJson.Clone(Comments), TotalElements = TotalOverride ?? Comments.Count,
            });
        }
    }
    private sealed class FakeRunner : IAnalysisRunner
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public AnalysisInputMetadata? Input { get; private set; }
        public Task<AnalysisRun> RunAsync(string id, IReadOnlyList<CommentResource> comments, ApiSettings settings,
            IProgress<AnalysisProgress>? progress, CancellationToken ct) => RunAsync(id, comments, settings, progress, ct, null);
        public Task<AnalysisRun> RunAsync(string id, IReadOnlyList<CommentResource> comments, ApiSettings settings,
            IProgress<AnalysisProgress>? progress, CancellationToken ct, AnalysisInputMetadata? input)
        {
            Calls++; Input = input;
            if (Fail) throw new InvalidOperationException("Synthetic model failure");
            var run = AnalystTestData.Run();
            run.DocumentId = id;
            return Task.FromResult(run);
        }
    }
}
