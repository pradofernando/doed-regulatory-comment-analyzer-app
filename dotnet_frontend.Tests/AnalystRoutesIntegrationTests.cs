using System.Net;
using DoedRegulatoryComments.Web.Services;
using DoedRegulatoryComments.Web.Data;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class AnalystRoutesIntegrationTests : IClassFixture<AnalystWebApplicationFactory>
{
    private readonly AnalystWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public AnalystRoutesIntegrationTests(AnalystWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Theory]
    [InlineData("/analysis")]
    [InlineData("/library")]
    [InlineData("/watchlists")]
    [InlineData("/notifications")]
    [InlineData("/compare")]
    public async Task AnalystRoutes_RenderWithoutCloudAccess(string path)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BrowserTabIcon_IsApplicationSpecificSvg()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var html = await client.GetStringAsync("/");
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(html);
        var link = document.DocumentNode.SelectSingleNode("//link[@rel='icon']");
        Assert.NotNull(link);
        Assert.Equal("image/svg+xml", HtmlAgilityPack.HtmlEntity.DeEntitize(link.GetAttributeValue("type", "")));
        using var response = await client.GetAsync(HtmlAgilityPack.HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")));
        response.EnsureSuccessStatusCode();
        var svg = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("svg", svg.Root?.Name.LocalName);
        Assert.Contains("DoED Regulatory Comment Analyzer", svg.ToString());
    }

    [Theory]
    [InlineData("watchlists")]
    [InlineData("notifications")]
    [InlineData("compare")]
    public async Task NewSidebarItems_HaveDecorativeSvgIcons(string route)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(await client.GetStringAsync("/"));
        var icon = document.DocumentNode.SelectSingleNode($"//a[@href='{route}']/span[@aria-hidden='true']/svg");
        Assert.NotNull(icon);
        Assert.Equal("0 0 24 24", icon.GetAttributeValue("viewBox", ""));
    }

    [Theory]
    [InlineData("watchlists", "Docket watchlists")]
    [InlineData("notifications", "Notifications")]
    [InlineData("compare", "Compare analyses")]
    public async Task Homepage_HasLinkedWorkspaceCardsWithIcons(string route, string title)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(await client.GetStringAsync("/"));
        var card = document.DocumentNode.SelectSingleNode($"//a[@href='{route}' and contains(@class,'feature-card')]");
        Assert.NotNull(card);
        Assert.Equal(title, card.SelectSingleNode("h3")?.InnerText);
        Assert.NotNull(card.SelectSingleNode(".//svg"));
        Assert.NotNull(card.SelectSingleNode("p"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/notifications")]
    [InlineData("/compare")]
    public async Task HeaderHasNotificationBell_AndNotificationsFollowCompareInNavigation(string route)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(await client.GetStringAsync(route));
        var bell = document.DocumentNode.SelectSingleNode("//header//a[@href='notifications' and contains(@class,'notification-bell')]");
        Assert.NotNull(bell);
        Assert.Contains("Notifications:", bell.GetAttributeValue("aria-label", ""));
        var links = document.DocumentNode.SelectNodes("//aside//nav//a").Select(node => node.GetAttributeValue("href", "")).ToList();
        Assert.Equal(links.IndexOf("compare") + 1, links.IndexOf("notifications"));
        if (route == "/")
        {
            var cards = document.DocumentNode.SelectNodes("//a[contains(@class,'feature-card--link')]").Select(node => node.GetAttributeValue("href", "")).ToList();
            Assert.Equal(cards.IndexOf("compare") + 1, cards.IndexOf("notifications"));
        }
    }

    [Fact]
    public async Task SavedReview_RendersEffectiveAnalysisEvidenceAndExportsWithoutChangingOriginal()
    {
        using var scope = _factory.Services.CreateScope();
        await using (var database = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AnalysisDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(Path.GetFullPath(_factory.DatabasePath), Path.GetFullPath(database.Database.GetDbConnection().DataSource), ignoreCase: true);
        }
        var repository = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var reviews = scope.ServiceProvider.GetRequiredService<ReviewService>();
        var quote = "Keep oversight reporting so that families can understand implementation.";
        var run = new AnalysisRun
        {
            SessionName = "Synthetic analyst walkthrough",
            DocumentId = "ED-DEMO-2026-0001",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow,
            TotalComments = 1,
            BatchSize = 5,
            Succeeded = true,
            Provenance = new AnalysisProvenance
            {
                AvailableComments = 4, FetchedComments = 4, SelectedComments = 1, Scope = "selected",
                SelectionDescription = "Synthetic test data - no real docket or model calls.",
                Model = "synthetic-test", PipelineVersion = "synthetic-fixture-v1",
                AgentVersions = new() { ["categorization"] = "synthetic:1", ["grouping"] = "synthetic:1" },
            },
            Sources =
            [
                new CommentSourceSnapshot
                {
                    CommentId = "ED-DEMO-COMMENT-1", SubmissionNumber = 1, Organization = "Example school",
                    Commenter = "Synthetic commenter", Title = "Oversight reporting", PostedAt = DateTimeOffset.UtcNow,
                    ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(quote))).ToLowerInvariant(),
                    Passages = [new SourcePassage { Id = "s1-p1", Kind = "pdf", PageNumber = 2, Title = "Synthetic attachment.pdf", Text = quote }],
                },
            ],
            Categorizations =
            [
                new CategorizationResult
                {
                    CommentId = "ED-DEMO-COMMENT-1", SubmissionNumber = 1, TextSource = "attachment",
                    RawResponse = "{\"primary_theme\":\"Oversight\",\"canonical_reason\":\"Public oversight\",\"stance\":\"opposing\",\"comment_summary\":\"Original synthetic summary\"}",
                    Parsed = new()
                    {
                        ["primary_theme"] = "Oversight", ["canonical_reason"] = "Public oversight", ["stance"] = "opposing",
                        ["comment_summary"] = "Original synthetic summary",
                        ["evidence"] = new[] { new { source_id = "s1-p1", quote } },
                    },
                },
            ],
            Grouped = new GroupedAnalysis
            {
                ParsedSuccessfully = true, OverallSummary = "Original synthetic collective analysis", OverallSentiment = "oppositional",
                ThemeGroups =
                [
                    new ThemeGroup
                    {
                        GroupName = "Public oversight", Count = 1, SubmissionNumbers = [1],
                        StanceDistribution = new() { ["opposing"] = 1 },
                        CommonArguments = ["Retaining oversight reporting"],
                        Evidence = [new FindingEvidence { Finding = "Retaining oversight reporting", SourceId = "s1-p1", Quote = quote }],
                    },
                ],
            },
        };
        var id = await repository.SaveRunAsync(run);
        var opened = await reviews.OpenAsync(id);
        var updated = await reviews.SaveReviewAsync(id, new CommentReview
        {
            CommentId = "ED-DEMO-COMMENT-1",
            PrimaryTheme = "Oversight", CanonicalReason = "Public oversight", Stance = "opposing",
            Summary = "Reviewed synthetic summary", Reviewer = "Test reviewer", Status = "approved",
            Notes = "Verified against saved page two.",
        }, opened.Version);

        var original = await repository.LoadRunAsync(id);
        Assert.NotNull(original);
        Assert.Equal("Original synthetic summary", AnalystEngine.GetField(original.Categorizations.Single(), "comment_summary"));
        Assert.Equal("Reviewed synthetic summary", AnalystEngine.GetField(updated.Effective.Categorizations.Single(), "comment_summary"));
        Assert.Single(updated.State.Revisions);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var analysisHtml = await client.GetStringAsync($"/analysis?runId={id:D}");
        Assert.Contains("Reviewed synthetic summary", analysisHtml);
        Assert.Contains("Review and search", analysisHtml);
        Assert.Contains("Coverage and quality", analysisHtml);
        var analysisDocument = new HtmlAgilityPack.HtmlDocument();
        analysisDocument.LoadHtml(analysisHtml);
        var workbenchLink = analysisDocument.DocumentNode.SelectSingleNode("//a[normalize-space(text())='Review and search']");
        Assert.NotNull(workbenchLink);
        Assert.Equal($"analysis?runId={id:D}#workbench", HtmlAgilityPack.HtmlEntity.DeEntitize(workbenchLink.GetAttributeValue("href", "")));
        var evidenceHtml = await client.GetStringAsync($"/evidence/{id:D}?sourceId=s1-p1");
        var evidenceDocument = new HtmlAgilityPack.HtmlDocument();
        evidenceDocument.LoadHtml(evidenceHtml);
        Assert.Contains(quote, evidenceDocument.DocumentNode.Descendants("mark").Select(node => node.InnerText));
        Assert.Contains("Page 2", evidenceHtml);
        Assert.Contains("Reviewed synthetic summary", evidenceHtml);
        using var stream = new MemoryStream(CollectiveAnalysisExporter.BuildWord(updated.Effective, updated.State));
        using var word = WordprocessingDocument.Open(stream, false);
        var mainPart = word.MainDocumentPart;
        Assert.NotNull(mainPart);
        Assert.NotNull(mainPart.Document);
        Assert.Contains("Reviewed synthetic summary", mainPart.Document.InnerText);
        Assert.Contains("Verified against saved page two", mainPart.Document.InnerText);

        if (_factory.KeepPreview)
        {
            _output.WriteLine($"PREVIEW_DATABASE={_factory.DatabasePath}");
            _output.WriteLine($"PREVIEW_RUN_ID={id:D}");
        }
    }
}

public sealed class AnalystWebApplicationFactory : WebApplicationFactory<Program>
{
    public bool KeepPreview { get; } = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANALYST_PREVIEW_DIRECTORY"));
    public string DatabasePath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("ANALYST_PREVIEW_DIRECTORY") ?? Path.GetTempPath(),
        $"analyst-preview-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<AnalysisDbContext>>();
            services.RemoveAll<DbContextOptions<AnalysisDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AnalysisDbContext>>();
            services.AddDbContextFactory<AnalysisDbContext>(options => options.UseSqlite($"Data Source={DatabasePath};Pooling=False"));
        });
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "",
            ["Persistence:Provider"] = "Sqlite",
            ["ConnectionStrings:AnalysisDb"] = $"Data Source={DatabasePath};Pooling=False",
            ["AnalysisBackend:Enabled"] = "false",
            ["Persistence:Payloads:CreateIfNotExists"] = "false",
            ["Attachments:OcrEndpoint"] = "",
            ["Monitoring:Enabled"] = "false",
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !KeepPreview)
        {
            foreach (var path in new[] { DatabasePath, DatabasePath + "-shm", DatabasePath + "-wal" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
