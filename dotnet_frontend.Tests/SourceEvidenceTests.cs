using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class SourceEvidenceTests
{
    [Fact]
    public void Capture_PreservesRealPagesAndStableContentHashes()
    {
        var comment = TestData.Comment("A", comment: "An inline concern.");
        var extraction = new AttachmentExtractionResult
        {
            Attachments = [new AttachmentText
            {
                Extracted = true, Format = "pdf", Title = "Evidence.pdf", Url = "https://downloads.regulations.gov/evidence.pdf",
                Pages = [new ExtractedPage(3, "The exact page-three passage."), new ExtractedPage(4, "Page four.")],
            }],
        };
        var snapshot = SourceEvidence.Capture(12, comment, extraction);
        Assert.Equal(3, snapshot.Passages.Count);
        Assert.Equal("s12-p2", snapshot.Passages[1].Id);
        Assert.Equal(3, snapshot.Passages[1].PageNumber);
        Assert.Equal(snapshot.ContentHash, SourceEvidence.Capture(12, comment, extraction).ContentHash);
        comment.Attributes.Comment = "Changed inline concern.";
        Assert.NotEqual(snapshot.ContentHash, SourceEvidence.Capture(12, comment, extraction).ContentHash);
    }

    [Fact]
    public void Capture_ReportsTruncationWithoutInventingWordPageNumbers()
    {
        var result = SourceEvidence.Capture(1, TestData.Comment("A"), new AttachmentExtractionResult
        {
            Attachments = [new AttachmentText { Extracted = true, Format = "docx", Text = new string('x', 30_000), Title = "Long.docx" }],
        });
        Assert.Equal(SourceEvidence.MaxCapturedCharacters, result.Passages.Sum(p => p.Text.Length));
        Assert.All(result.Passages, p => Assert.Null(p.PageNumber));
        Assert.Equal("partial", result.ExtractionStatus);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Capture_DoesNotSplitSurrogatePairs()
    {
        var text = new string('x', 1999) + "\U0001F4DA" + " pages";
        var source = SourceEvidence.Capture(1, TestData.Comment("A", comment: text), new AttachmentExtractionResult());
        Assert.Equal(text, string.Concat(source.Passages.Select(p => p.Text)));
        Assert.All(source.Passages, p => Assert.False(char.IsHighSurrogate(p.Text[^1])));
    }

    [Fact]
    public void Evidence_RejectsWrongIdsQuotesAndCrossCommentReferences()
    {
        var run = AnalystTestData.Run();
        var fields = new Dictionary<string, object?>
        {
            ["evidence"] = new[]
            {
                new { source_id = "s1-p1", quote = "Keep access to services" },
                new { source_id = "s1-p1", quote = "An invented quotation" },
                new { source_id = "s2-p1", quote = "Reduce reporting" },
                new { source_id = "s999-p1", quote = "Keep access to services" },
            },
        };
        SourceEvidence.ValidateEvidence(run.Sources[0], fields);
        run.Categorizations[0].Parsed = fields;
        var evidence = SourceEvidence.GetEvidence(run, "COMMENT-1");
        Assert.Single(evidence);
        Assert.Equal("Keep access to services", evidence[0].Quote);
        Assert.Contains(run.Sources[0].Warnings, warning => warning.Contains("3 unsupported"));
    }

    [Fact]
    public void GroupEvidence_RequiresExactFindingAndCorrectMembership()
    {
        var run = AnalystTestData.Run();
        var group = run.Grouped.ThemeGroups[0];
        group.SubmissionNumbers = [1];
        group.CommonArguments = ["Access"];
        group.Evidence =
        [
            new() { Finding = "Access", SourceId = "s1-p1", Quote = "Keep access" },
            new() { Finding = "Different finding", SourceId = "s1-p1", Quote = "Keep access" },
            new() { Finding = "Access", SourceId = "s2-p1", Quote = "Reduce reporting" },
        ];
        SourceEvidence.ValidateGroupedEvidence(run);
        Assert.Single(group.Evidence);
        Assert.Single(SourceEvidence.GetEvidence(run, finding: "Access"));
        Assert.Empty(SourceEvidence.GetEvidence(run, finding: "Different finding"));
    }

    [Fact]
    public void Context_IsBoundedRelevantAndExplicitAboutOmissions()
    {
        var run = AnalystTestData.Run();
        for (var i = 3; i < 50; i++)
            run.Sources[0].Passages.Add(new SourcePassage { Id = $"s1-p{i}", Text = new string('z', 2000) });
        var context = SourceEvidence.BuildChatContext(run, "implementation deadline");
        Assert.True(context.Length <= SourceEvidence.ChatContextCharacters);
        Assert.Contains("s1-p2", context);
        Assert.Contains("not exhaustive", context);
        Assert.Contains("untrusted data", context);
        Assert.Null(SourceEvidence.Resolve(run, "s999-p1"));
        run.Sources.Clear();
        Assert.Empty(SourceEvidence.GetEvidence(run));
    }

    [Theory]
    [InlineData(false, 5, 5, "full")]
    [InlineData(true, 5, 1, "selected")]
    [InlineData(false, 10, 5, "limited")]
    public void Provenance_DistinguishesScope(bool selected, int available, int count, string expected)
    {
        var result = SourceEvidence.CreateProvenance(new ApiSettings(), count, new AnalysisInputMetadata
        {
            IsSelection = selected, FetchedComments = count, AvailableComments = available, UseDocketFilter = false,
        });
        Assert.Equal(expected, result.Scope);
        Assert.Equal("document", result.QueryScope);
        Assert.False(result.RunValidation);
        Assert.Equal("unknown", SourceEvidence.CreateProvenance(new ApiSettings(), 1, null).Scope);
    }
}
