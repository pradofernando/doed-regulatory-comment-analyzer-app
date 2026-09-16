using DoedRegulatoryComments.Web.Components;
using DoedRegulatoryComments.Web.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class AnalystPresentationTests
{
    [Fact]
    public void Coverage_DoesNotTreatMissingLegacySnapshotsAsEmptyComments()
    {
        var coverage = AnalysisCoverage.From(new AnalysisRun { TotalComments = 9 });
        Assert.Null(coverage.Unreadable);
        Assert.Null(coverage.Available);
        Assert.Null(coverage.WithUsableSource);
        Assert.Equal(9, coverage.Uncaptured);
        Assert.Equal("unknown", coverage.Scope);
    }

    [Fact]
    public void Coverage_SeparatesSelectionFromUnreadableAndPartialSources()
    {
        var run = Example();
        run.Provenance = new AnalysisProvenance
        {
            AvailableComments = 100, FetchedComments = 100, SelectedComments = 3, Scope = "selected",
        };
        run.TotalComments = 3;
        run.Sources.Add(new CommentSourceSnapshot { CommentId = "unreadable", ExtractionStatus = "unreadable" });
        run.Sources.Add(new CommentSourceSnapshot { CommentId = "partial", ExtractionStatus = "partial" });
        var coverage = AnalysisCoverage.From(run);
        Assert.Equal(100, coverage.Available);
        Assert.Equal(3, coverage.Selected);
        Assert.Equal(1, coverage.Unreadable);
        Assert.Equal(1, coverage.Partial);
        Assert.Equal(1, coverage.WithUsableSource);
    }

    [Fact]
    public async Task Citations_OnlyLinkKnownSourcesAndEscapeText()
    {
        var run = Example();
        var html = await Render<CitedText>(new()
        {
            [nameof(CitedText.Run)] = run,
            [nameof(CitedText.Text)] = "<script>alert('x')</script> [source:s1-p1] [source:s99-p9] #1 #999",
        });
        Assert.Contains($"evidence/{run.PersistedId:D}?sourceId=s1-p1", html);
        Assert.Contains("[source:s99-p9]", html);
        Assert.DoesNotContain("sourceId=s99-p9", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("#999", html);
    }

    [Fact]
    public async Task CoveragePanel_RendersScopeAndLimitations()
    {
        var run = Example();
        run.Provenance = new AnalysisProvenance { Scope = "selected", FetchedComments = 30, SelectedComments = 1 };
        var html = await Render<CoverageSummary>(new() { [nameof(CoverageSummary.Run)] = run });
        Assert.Contains("Scope: selected", html);
        Assert.Contains("not a representative survey", html);
        Assert.Contains("not a calibrated accuracy score", html);
    }

    [Fact]
    public void WordAndExcel_ExportEffectiveValuesAndKeepSourceAndReviewHistory()
    {
        var run = Example();
        var state = new RunReviewState
        {
            Revisions = [new CommentReview { CommentId = "ED-1", Stance = "opposing", Summary = "Reviewed summary", Notes = "Checked source", Status = "approved" }],
            Issues = [new IssueResponse { Concern = "Reporting burden", DraftResponse = "Draft for human review", SubmissionNumbers = [1] }],
        };
        using (var stream = new MemoryStream(CollectiveAnalysisExporter.BuildWord(run, state)))
        using (var document = WordprocessingDocument.Open(stream, false))
        {
            var mainPart = document.MainDocumentPart;
            Assert.NotNull(mainPart);
            Assert.NotNull(mainPart.Document);
            var text = mainPart.Document.InnerText;
            Assert.Contains("Current stance: opposing", text);
            Assert.Contains("Reviewed summary", text);
            Assert.Contains("Original AI response", text);
            Assert.Contains("Checked source", text);
            Assert.Contains("human review", text);
        }
        using var excelStream = new MemoryStream(CollectiveAnalysisExporter.BuildExcel(run, state));
        using var workbook = SpreadsheetDocument.Open(excelStream, false);
        var workbookPart = workbook.WorkbookPart;
        Assert.NotNull(workbookPart);
        Assert.NotNull(workbookPart.Workbook);
        var names = workbookPart.Workbook.Descendants<Sheet>().Select(s => s.Name?.Value).ToList();
        Assert.Contains("Captured sources", names);
        Assert.Contains("Human revisions", names);
        Assert.Contains("Issue response matrix", names);
        var textValues = string.Join(" ", workbookPart.WorksheetParts.Select(p => p.Worksheet?.InnerText));
        Assert.Contains("Reviewed summary", textValues);
        Assert.Contains("s1-p1", textValues);
        Assert.Contains("Checked source", textValues);
        Assert.Empty(workbookPart.WorksheetParts.SelectMany(p => p.Worksheet?.Descendants<CellFormula>() ?? []));
    }

    [Fact]
    public void IssueCsv_QuotesCommasAndNeutralizesSpreadsheetFormulas()
    {
        var csv = AnalystReportExporter.IssuesCsv([
            new IssueResponse { RuleSection = "  =1+1", Concern = "Reason, with \"quotes\"", DraftResponse = "@SUM(A1)", SubmissionNumbers = [1, 2] },
        ]);
        Assert.Contains("'  =1+1", csv);
        Assert.Contains("\"Reason, with \"\"quotes\"\"\"", csv);
        Assert.Contains("'@SUM(A1)", csv);
        Assert.Contains("human review required", csv);
    }

    private static AnalysisRun Example() => new()
    {
        PersistedId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        DocumentId = "ED-SAMPLE",
        Succeeded = true,
        TotalComments = 1,
        Grouped = new GroupedAnalysis { OverallSummary = "Reviewed summary", ParsedSuccessfully = true },
        Categorizations =
        [
            new CategorizationResult
            {
                CommentId = "ED-1", SubmissionNumber = 1, RawResponse = "{\"stance\":\"supportive\",\"comment_summary\":\"Original summary\"}",
                Parsed = new Dictionary<string, object?>
                {
                    ["primary_theme"] = "Reporting",
                    ["canonical_reason"] = "Reporting burden",
                    ["stance"] = "opposing",
                    ["comment_summary"] = "Reviewed summary",
                    ["evidence"] = new[] { new { source_id = "s1-p1", quote = "Maintain meaningful reporting." } },
                },
            },
        ],
        Sources =
        [
            new CommentSourceSnapshot
            {
                CommentId = "ED-1", SubmissionNumber = 1, ExtractionStatus = "complete",
                Passages = [new SourcePassage { Id = "s1-p1", Text = "Maintain meaningful reporting.", Kind = "pdf", Title = "Submission.pdf", PageNumber = 2 }],
            },
        ],
    };

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
