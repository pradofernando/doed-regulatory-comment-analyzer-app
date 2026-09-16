using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoedRegulatoryComments.Web.Tests;

internal static class AnalystTestData
{
    internal static AnalysisRun Run() => new()
    {
        DocumentId = "ED-SYNTHETIC-2026",
        StartedAt = DateTimeOffset.Parse("2026-01-04T10:00:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-01-04T10:01:00Z"),
        BatchSize = 2,
        TotalComments = 2,
        Succeeded = true,
        Provenance = new AnalysisProvenance
        {
            CapturedAt = DateTimeOffset.Parse("2026-01-04T10:00:00Z"),
            AvailableComments = 10, FetchedComments = 5, SelectedComments = 2,
            Scope = "selected", SelectionDescription = "Synthetic selected submissions",
            AgentVersions = new() { ["categorization"] = "fixture-v1" },
        },
        Sources =
        [
            new CommentSourceSnapshot
            {
                CommentId = "COMMENT-1", SubmissionNumber = 1, Title = "Access proposal",
                Organization = "Example School", Commenter = "Example Reviewer",
                PostedAt = DateTimeOffset.Parse("2026-01-02T23:30:00Z"),
                ContentHash = "synthetic-content-hash",
                Passages =
                [
                    new SourcePassage { Id = "s1-p1", Text = "Keep access to services under 34 CFR 300.1.", Title = "inline", Kind = "inline" },
                    new SourcePassage
                    {
                        Id = "s1-p2", Text = "The attachment footnote describes the implementation deadline.",
                        Title = "Detailed evidence.pdf", Kind = "pdf", PageNumber = 7, Url = "https://www.regulations.gov/comment/COMMENT-1",
                    },
                ],
            },
            new CommentSourceSnapshot
            {
                CommentId = "COMMENT-2", SubmissionNumber = 2, Title = "Second access comment",
                Organization = "Other School", PostedAt = DateTimeOffset.Parse("2026-01-03T00:30:00Z"),
                Passages = [new SourcePassage { Id = "s2-p1", Text = "Reduce reporting burdens while retaining services.", Kind = "inline" }],
            },
        ],
        Categorizations =
        [
            new CategorizationResult
            {
                CommentId = "COMMENT-1", SubmissionNumber = 1, RowData = "{\"comment\":\"First original full body\"}",
                RawResponse = "first original AI output",
                Parsed = new()
                {
                    ["primary_theme"] = "Access", ["canonical_reason"] = "Access", ["stance"] = "opposing",
                    ["comment_summary"] = "Original first summary",
                    ["evidence"] = new[] { new { source_id = "s1-p1", quote = "Keep access to services" } },
                },
            },
            new CategorizationResult
            {
                CommentId = "COMMENT-2", SubmissionNumber = 2, RawResponse = "second original AI output",
                Parsed = new()
                {
                    ["primary_theme"] = "Access", ["canonical_reason"] = "Access", ["stance"] = "supportive",
                    ["comment_summary"] = "Original second summary",
                },
            },
        ],
        Grouped = new GroupedAnalysis
        {
            OverallSummary = "Original collective summary",
            OverallSentiment = "mixed",
            RawResponse = "Original collective raw output",
            ParsedSuccessfully = true,
            Patterns = ["Original pattern"],
            Recommendations = ["Original recommendation"],
            ThemeGroups =
            [
                new ThemeGroup
                {
                    GroupName = "Access", GroupDescription = "Original group description", Count = 2,
                    SubmissionNumbers = [1, 2],
                    StanceDistribution = new() { ["opposing"] = 1, ["supportive"] = 1 },
                    CommonArguments = ["Original common argument"],
                    Evidence = [new FindingEvidence { Finding = "Access concern", SourceId = "s1-p1", Quote = "Keep access to services" }],
                },
            ],
        },
    };

    internal static CommentReview Review(CategorizationResult cat) => new()
    {
        CommentId = cat.CommentId,
        Reviewer = "Human reviewer label",
        PrimaryTheme = AnalystEngine.GetField(cat, "primary_theme", "primary_topic", "topic"),
        CanonicalReason = AnalystEngine.GetField(cat, "canonical_reason", "primary_theme", "primary_topic", "topic"),
        Stance = AnalystEngine.NormalizeStance(AnalystEngine.GetField(cat, "stance", "position")),
        Summary = AnalystEngine.GetField(cat, "comment_summary", "rationale"),
        Status = "approved",
    };
}

internal sealed class AnalystSqliteStore : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    internal IDbContextFactory<AnalysisDbContext> Factory { get; }
    internal AnalysisRepository Analyses { get; }
    internal RelationalWorkspaceRepository Workspace { get; }
    internal ReviewService Reviews { get; }

    private AnalystSqliteStore(SqliteConnection connection)
    {
        _connection = connection;
        Factory = new ContextFactory(new DbContextOptionsBuilder<AnalysisDbContext>().UseSqlite(connection).Options);
        Analyses = new AnalysisRepository(Factory, NullLogger<AnalysisRepository>.Instance);
        Workspace = new RelationalWorkspaceRepository(Factory);
        Reviews = new ReviewService(Analyses, Workspace);
    }

    internal static async Task<AnalystSqliteStore> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var store = new AnalystSqliteStore(connection);
        await AnalysisDatabaseInitializer.InitializeAsync(store.Factory);
        return store;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class ContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
        public Task<AnalysisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
