using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class ReviewServiceTests
{
    [Fact]
    public async Task Open_SeedsDeterministicIssueIdsWithoutWritingOrInventingSections()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var id = await store.Analyses.SaveRunAsync(AnalystTestData.Run());
        var first = await store.Reviews.OpenAsync(id);
        var second = await store.Reviews.OpenAsync(id);
        Assert.Equal(WorkspaceJson.Write(first.State), WorkspaceJson.Write(second.State));
        Assert.Null(first.Version);
        Assert.Null(await store.Workspace.GetAsync("reviews", id.ToString("D")));
        var issue = Assert.Single(first.State.Issues);
        Assert.Equal("Access", issue.Concern);
        Assert.Equal(new[] { 1, 2 }, issue.SubmissionNumbers);
        Assert.Equal("", issue.RuleSection);
        Assert.Equal("", issue.RequestedChange);
        Assert.Equal(ReviewService.DraftLabel, issue.DraftResponse);
        Assert.Empty(first.State.IssueHistory);
    }

    [Fact]
    public async Task Reviews_AppendServerRevisionsAndRejectStaleSaves_WhileBaselineStaysImmutable()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var id = await store.Analyses.SaveRunAsync(AnalystTestData.Run());
        var baseline = (await store.Analyses.LoadRunAsync(id))!;
        var baselineJson = WorkspaceJson.Write(baseline);
        var review = AnalystTestData.Review(baseline.Categorizations[0]);
        review.Id = Guid.Empty;
        review.At = DateTimeOffset.Parse("2001-01-01T00:00:00Z");
        review.PrimaryTheme = "Costs";
        review.CanonicalReason = "Reporting costs";
        review.Stance = "supportive";
        review.Summary = "Verified against the saved source [s1-p1].";
        var before = DateTimeOffset.UtcNow;
        var first = await store.Reviews.SaveReviewAsync(id, review, null);
        var savedReview = Assert.Single(first.State.Revisions);
        Assert.NotEqual(Guid.Empty, savedReview.Id);
        Assert.InRange(savedReview.At, before, DateTimeOffset.UtcNow);
        Assert.Equal(Guid.Empty, review.Id);
        Assert.Equal(2, first.Effective.Grouped.ThemeGroups.Count);
        Assert.NotNull(first.Version);

        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.Reviews.SaveReviewAsync(id, review, null));
        review.Summary = "Second reviewed summary";
        review.Status = "flagged";
        var second = await store.Reviews.SaveReviewAsync(id, review, first.Version);
        Assert.Equal(2, second.State.Revisions.Count);
        Assert.Equal(savedReview.Id, second.State.Revisions[0].Id);
        Assert.Equal(savedReview.Summary, second.State.Revisions[0].Summary);
        Assert.Equal("Second reviewed summary", AnalystEngine.GetField(second.Effective.Categorizations[0], "comment_summary"));
        Assert.Equal(baselineJson, WorkspaceJson.Write(await store.Analyses.LoadRunAsync(id)));
        var reopened = await new ReviewService(store.Analyses, store.Workspace).OpenAsync(id);
        Assert.Equal(second.Version, reopened.Version);
        Assert.Equal(2, reopened.State.Revisions.Count);
        Assert.Equal(2, reopened.Original.Sources.Count);
    }

    [Fact]
    public async Task InvalidReviewValuesAndForeignSourceCitations_AreRejectedWithoutHistoryWrites()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var baseline = AnalystTestData.Run();
        var id = await store.Analyses.SaveRunAsync(baseline);
        var mutations = new Action<CommentReview>[]
        {
            review => review.CommentId = "OUTSIDE-RUN",
            review => review.Status = "publish",
            review => review.Stance = "probably",
            review => review.Reviewer = "",
            review => review.Reviewer = "multiple\nlines",
            review => review.PrimaryTheme = new string('x', 201),
            review => review.Summary = new string('x', ReviewService.MaxSummaryLength + 1),
            review => review.Notes = new string('x', ReviewService.MaxNotesLength + 1),
            review => review.Summary = "Foreign evidence [s2-p1]",
            review => review.Summary = "Unknown evidence [s99-p1]",
            review => review.Summary = "Unsupported authority 99 CFR 777.1",
        };
        foreach (var mutate in mutations)
        {
            var review = AnalystTestData.Review(baseline.Categorizations[0]);
            mutate(review);
            await Assert.ThrowsAsync<ArgumentException>(() => store.Reviews.SaveReviewAsync(id, review, null));
        }
        Assert.Null(await store.Workspace.GetAsync("reviews", id.ToString("D")));
    }

    [Fact]
    public async Task IssueRevisions_KeepBaselineAndEverySavedRevisionSeparateFromTheCurrentIssue()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var id = await store.Analyses.SaveRunAsync(AnalystTestData.Run());
        var opened = await store.Reviews.OpenAsync(id);
        var issue = WorkspaceJson.Clone(Assert.Single(opened.State.Issues));
        var seededJson = WorkspaceJson.Write(issue);
        issue.Reviewer = "Response author label";
        issue.RuleSection = "34 CFR 300.1";
        issue.RequestedChange = "Retain the referenced service provision.";
        issue.DraftResponse = "Consider the saved access concern [s1-p1] under 34 CFR 300.1.";
        var first = await store.Reviews.SaveIssueAsync(id, issue, opened.Version);
        Assert.Equal(2, first.State.IssueHistory.Count);
        Assert.Equal(seededJson, WorkspaceJson.Write(first.State.IssueHistory[0]));
        Assert.StartsWith(ReviewService.DraftLabel, first.State.Issues[0].DraftResponse);
        var firstHistoryJson = WorkspaceJson.Write(first.State.IssueHistory);
        issue.DraftResponse = "Revised draft wording without an agency conclusion.";
        issue.ReviewStatus = "in-review";
        var second = await store.Reviews.SaveIssueAsync(id, issue, first.Version);
        Assert.Equal(3, second.State.IssueHistory.Count);
        Assert.Equal(firstHistoryJson, WorkspaceJson.Write(second.State.IssueHistory.Take(2).ToList()));
        Assert.Single(second.State.Issues);
        Assert.Equal(issue.Id, second.State.Issues[0].Id);
        Assert.Equal("in-review", second.State.Issues[0].ReviewStatus);
        Assert.Equal(3, (await store.Reviews.OpenAsync(id)).State.IssueHistory.Count);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.Reviews.SaveIssueAsync(id, issue, first.Version));
        Assert.Equal("Original collective summary", (await store.Analyses.LoadRunAsync(id))!.Grouped.OverallSummary);
    }

    [Fact]
    public async Task NewIssues_AreLabeledDraftsAndRequireRealSubmissionAndSourceMembership()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var id = await store.Analyses.SaveRunAsync(AnalystTestData.Run());
        var issue = new IssueResponse
        {
            Concern = "Verify implementation scope", SubmissionNumbers = [1],
            Reviewer = "Human author", DraftResponse = "Response for review.",
        };
        foreach (var mutate in new Action<IssueResponse>[]
        {
            value => value.SubmissionNumbers = [999],
            value => value.RuleSection = "Imaginary section 9000",
            value => value.DraftResponse = "Unsupported 99 U.S.C. 1000.",
            value => value.DraftResponse = "Foreign evidence [s2-p1]",
            value => value.ReviewStatus = "published",
        })
        {
            var invalid = WorkspaceJson.Clone(issue);
            mutate(invalid);
            await Assert.ThrowsAsync<ArgumentException>(() => store.Reviews.SaveIssueAsync(id, invalid, null));
        }
        var saved = await store.Reviews.SaveIssueAsync(id, issue, null);
        Assert.Equal(2, saved.State.Issues.Count);
        Assert.Single(saved.State.IssueHistory);
        Assert.StartsWith(ReviewService.DraftLabel, saved.State.Issues.Single(i => i.Id == issue.Id).DraftResponse);
    }

    [Fact]
    public async Task ReviewPersistenceStillDetectsAConflictThatOccursAfterTheInitialRead()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var original = AnalystTestData.Run();
        var id = await store.Analyses.SaveRunAsync(original);
        var service = new ReviewService(store.Analyses, new ConcurrentCreateRepository(store.Workspace));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            service.SaveReviewAsync(id, AnalystTestData.Review(original.Categorizations[0]), null));
        Assert.Equal("Original first summary", AnalystEngine.GetField((await store.Analyses.LoadRunAsync(id))!.Categorizations[0], "comment_summary"));
    }

    private sealed class ConcurrentCreateRepository(IWorkspaceRepository inner) : IWorkspaceRepository
    {
        public Task<WorkspaceItem?> GetAsync(string kind, string id, CancellationToken ct = default) => inner.GetAsync(kind, id, ct);
        public Task<AnalysisPage<WorkspaceItem>> ListPageAsync(string kind, int take = 50, string? continuationToken = null, CancellationToken ct = default) =>
            inner.ListPageAsync(kind, take, continuationToken, ct);
        public async Task<WorkspaceItem> SaveAsync(string kind, string id, string json, string? expectedVersion, CancellationToken ct = default)
        {
            await inner.SaveAsync(kind, id, WorkspaceJson.Write(new RunReviewState()), null, ct);
            return await inner.SaveAsync(kind, id, json, expectedVersion, ct);
        }
        public Task DeleteAsync(string kind, string id, string expectedVersion, CancellationToken ct = default) =>
            inner.DeleteAsync(kind, id, expectedVersion, ct);
    }
}
