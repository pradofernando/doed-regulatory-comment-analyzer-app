using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AnalysisRepositoryTests
{
    [Fact]
    public async Task Sqlite_SaveListAndLoad_PreservesRunAndOrdersNewestFirst()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        await AnalysisDatabaseInitializer.InitializeAsync(factory);

        var repository = new AnalysisRepository(factory, NullLogger<AnalysisRepository>.Instance);
        var older = CreateRun(DateTimeOffset.UtcNow.AddHours(-1), "COMMENT-OLD");
        var newer = CreateRun(DateTimeOffset.UtcNow, "COMMENT-NEW");
        await repository.SaveRunAsync(older);
        var newerId = await repository.SaveRunAsync(newer);
        await repository.RenameRunAsync(newerId, "  IDEA oversight review  ");

        var summaries = await repository.ListAsync(new AnalysisListFilter(Take: 10));
        var loaded = await repository.LoadRunAsync(newerId);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(newerId, summaries[0].Id);
        Assert.Equal("IDEA oversight review", summaries[0].SessionName);
        Assert.NotNull(loaded);
        Assert.Equal("IDEA oversight review", loaded.SessionName);
        Assert.Equal("COMMENT-NEW", loaded.Categorizations.Single().CommentId);
        Assert.Equal(new[] { 1 }, loaded.Grouped.ThemeGroups.Single().SubmissionNumbers);
        Assert.Equal("Question", loaded.FollowUpHistory.Single().Text);
    }

    [Fact]
    public async Task Initializer_AddsSessionNameToExistingSqliteSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE "Runs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Runs" PRIMARY KEY,
                    "DocumentId" TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);

        await AnalysisDatabaseInitializer.InitializeAsync(factory);
        await AnalysisDatabaseInitializer.InitializeAsync(factory);

        await using var verify = connection.CreateCommand();
        verify.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'SessionName';";
        Assert.Equal(1, Convert.ToInt32(await verify.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Sqlite_ListPage_ContinuationReturnsEachRunExactlyOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        await AnalysisDatabaseInitializer.InitializeAsync(factory);
        var repository = new AnalysisRepository(factory, NullLogger<AnalysisRepository>.Instance);
        var startedAt = DateTimeOffset.UtcNow;
        await repository.SaveRunAsync(CreateRun(startedAt.AddMinutes(-2), "COMMENT-1"));
        await repository.SaveRunAsync(CreateRun(startedAt.AddMinutes(-1), "COMMENT-2"));
        await repository.SaveRunAsync(CreateRun(startedAt, "COMMENT-3"));

        var first = await repository.ListPageAsync(new AnalysisListFilter(Take: 2));
        var second = await repository.ListPageAsync(
            new AnalysisListFilter(Take: 2),
            first.ContinuationToken);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void CosmosMapping_RoundTrip_PreservesAggregate()
    {
        var source = CreateRun(DateTimeOffset.UtcNow, "COMMENT-COSMOS");
        source.SessionName = "Cosmos review";

        var loaded = CosmosAnalysisRepository.RoundTripForTesting(source);

        Assert.Equal(source.DocumentId, loaded.DocumentId);
        Assert.Equal("Cosmos review", loaded.SessionName);
        Assert.Equal("COMMENT-COSMOS", loaded.Categorizations.Single().CommentId);
        Assert.Equal(new[] { 1 }, loaded.Grouped.ThemeGroups.Single().SubmissionNumbers);
        Assert.Equal("Question", loaded.FollowUpHistory.Single().Text);
    }

    [Fact]
    public void CosmosMapping_NormalizesDocumentIdsAndRejectsFutureSchemas()
    {
        Assert.Equal(
            "ED-TEST-0001",
            CosmosAnalysisRepository.NormalizeDocumentIdForTesting("  ed-test-0001  "));

        var source = CreateRun(DateTimeOffset.UtcNow, "COMMENT-COSMOS");
        Assert.Throws<NotSupportedException>(() =>
            CosmosAnalysisRepository.RoundTripForTesting(source, schemaVersion: 999));
    }

    [Fact]
    public void CosmosOptions_RequireEndpointOrConnectionString()
    {
        var options = new CosmosPersistenceOptions();

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Endpoint", error.Message);
    }

    [Fact]
    public async Task CosmosConcurrency_RetriesConflictsAndSucceedsWithinLimit()
    {
        var attempts = new List<int>();
        var retries = new List<int>();

        await CosmosOptimisticConcurrency.ExecuteAsync(
            attempt =>
            {
                attempts.Add(attempt);
                return Task.FromResult(attempt < 3
                    ? CosmosUpdateAttemptResult.Conflict
                    : CosmosUpdateAttemptResult.Updated);
            },
            maxAttempts: 3,
            retries.Add);

        Assert.Equal(new[] { 1, 2, 3 }, attempts);
        Assert.Equal(new[] { 1, 2 }, retries);
    }

    [Fact]
    public async Task CosmosConcurrency_ThrowsAfterConfiguredAttemptLimit()
    {
        var attempts = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CosmosOptimisticConcurrency.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromResult(CosmosUpdateAttemptResult.Conflict);
                },
                maxAttempts: 3,
                exhaustedException: () => new InvalidOperationException("exhausted")));

        Assert.Equal(3, attempts);
        Assert.Equal("exhausted", error.Message);
    }

    [Fact]
    public void CosmosContinuationToken_RoundTrip_PreservesSourceAndOpaqueToken()
    {
        const string opaqueToken = "[{\"range\":\"A:B/+\"}]";
        foreach (var source in new[] { CosmosPageSource.Aggregate, CosmosPageSource.Summary })
        {
            var page = new AnalysisPage<AnalysisRunSummary>(Array.Empty<AnalysisRunSummary>(), opaqueToken);

            var encoded = CosmosContinuationTokenCodec.EncodePage(page, source);
            var decoded = CosmosContinuationTokenCodec.Decode(encoded.ContinuationToken);

            Assert.Equal(source, decoded.Source);
            Assert.Equal(opaqueToken, decoded.Token);
        }

        var legacy = CosmosContinuationTokenCodec.Decode(opaqueToken);
        Assert.Equal(CosmosPageSource.Aggregate, legacy.Source);
        Assert.Equal(opaqueToken, legacy.Token);
    }

    [Fact]
    public void SessionName_NormalizesBlankAndRejectsOverlongValues()
    {
        Assert.Null(AnalysisSessionNames.Normalize("   "));
        Assert.Equal("Named run", AnalysisSessionNames.Normalize("  Named run  "));
        Assert.Throws<ArgumentException>(() =>
            AnalysisSessionNames.Normalize(new string('x', AnalysisSessionNames.MaxLength + 1)));
    }

    private static AnalysisRun CreateRun(DateTimeOffset startedAt, string commentId)
    {
        var run = new AnalysisRun
        {
            DocumentId = "ED-TEST-0001",
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(1),
            BatchSize = 5,
            TotalComments = 1,
            Succeeded = true,
            FollowUpThreadId = "response-1",
            Grouped = new GroupedAnalysis
            {
                OverallSummary = "Summary",
                OverallSentiment = "mixed",
                ParsedSuccessfully = true,
                Patterns = new List<string> { "Pattern" },
                Recommendations = new List<string> { "Recommendation" },
                ThemeGroups = new List<ThemeGroup>
                {
                    new()
                    {
                        GroupName = "Theme",
                        GroupDescription = "Description",
                        Count = 1,
                        SubmissionNumbers = new List<int> { 1 },
                        StanceDistribution = new Dictionary<string, int> { ["support"] = 1 },
                        CommonArguments = new List<string> { "Argument" },
                    },
                },
            },
        };
        run.Categorizations.Add(new CategorizationResult
        {
            SubmissionNumber = 1,
            CommentId = commentId,
            RawResponse = "{\"sentiment\":\"supportive\"}",
            Parsed = new Dictionary<string, object?> { ["sentiment"] = "supportive" },
            TextSource = "attachment",
            AttachmentsExtracted = 1,
        });
        run.FollowUpHistory.Add(new FollowUpTurn("user", "Question", startedAt.AddSeconds(30)));
        return run;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AnalysisDbContext>
    {
        private readonly DbContextOptions<AnalysisDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AnalysisDbContext> options) => _options = options;

        public AnalysisDbContext CreateDbContext() => new(_options);

        public Task<AnalysisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}