using DoedRegulatoryComments.Web.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AttachmentExtractorTests
{
    [Theory]
    [InlineData("", 1, 20, true)]
    [InlineData("Image only", 2, 20, true)]
    [InlineData("This page contains enough machine-readable text for direct extraction.", 1, 20, false)]
    public void ShouldUseOcr_UsesMeaningfulCharactersPerProcessedPage(
        string text,
        int pagesProcessed,
        int minimumCharacters,
        bool expected)
    {
        Assert.Equal(
            expected,
            AttachmentExtractor.ShouldUseOcr(text, pagesProcessed, minimumCharacters));
    }

    [Fact]
    public void ExtractPdfText_StopsAtConfiguredPageLimit()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = builder.AddPage(PageSize.Letter);
            page.AddText(
                $"Synthetic page {pageNumber} has enough searchable text.",
                12,
                new PdfPoint(40, 700),
                font);
        }

        var result = AttachmentExtractor.ExtractPdfText(
            builder.Build(),
            maxPages: 2,
            minTextCharactersPerPage: 10,
            maxTextCharacters: 10_000);

        Assert.Equal(3, result.PageCount);
        Assert.Equal(2, result.PagesProcessed);
        Assert.True(result.Truncated);
        Assert.Contains("Synthetic page 1", result.Text);
        Assert.Contains("Synthetic page 2", result.Text);
        Assert.DoesNotContain("Synthetic page 3", result.Text);
    }
}