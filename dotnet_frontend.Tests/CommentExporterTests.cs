using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class CommentExporterTests
{
    [Fact]
    public void ToCsv_WritesHeaderRow()
    {
        var csv = CommentExporter.ToCsv(Array.Empty<CommentResource>());
        var firstLine = csv.Split('\n')[0].TrimEnd('\r');
        Assert.Equal(
            "comment_id,posted_date,modify_date,agency_id,document_type,first_name,last_name,organization,title,comment",
            firstLine);
    }

    [Fact]
    public void ToCsv_EscapesCommasQuotesAndNewlines()
    {
        var comments = new[]
        {
            TestData.Comment("ED-1", first: "Jane", last: "Doe", org: "Org, Inc", title: "He said \"hi\"", comment: "line1\nline2"),
        };

        var csv = CommentExporter.ToCsv(comments);

        Assert.Contains("\"Org, Inc\"", csv);          // comma forces quoting
        Assert.Contains("\"He said \"\"hi\"\"\"", csv);  // embedded quotes doubled
        Assert.Contains("\"line1\nline2\"", csv);        // newline forces quoting
    }

    [Fact]
    public void ToCsv_EmitsOneDataRowPerComment()
    {
        var comments = new[]
        {
            TestData.Comment("ED-1", first: "A"),
            TestData.Comment("ED-2", first: "B"),
        };

        var lines = CommentExporter.ToCsv(comments)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 rows
    }

    [Fact]
    public void ToJson_ProducesIndentedJsonContainingPayload()
    {
        var json = CommentExporter.ToJson(new { hello = "world", count = 2 });
        Assert.Contains("\"hello\": \"world\"", json);
        Assert.Contains("\"count\": 2", json);
    }
}
