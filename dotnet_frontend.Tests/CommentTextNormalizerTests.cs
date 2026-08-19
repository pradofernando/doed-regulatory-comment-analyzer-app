using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class CommentTextNormalizerTests
{
    [Fact]
    public void Normalize_DecodesEntitiesAndPreservesConsecutiveBreaks()
    {
        const string source = "Michigan&#39;s Children supports the statement.<br/><br/>The proposed removal is unnecessary.";

        var result = CommentTextNormalizer.Normalize(source);

        Assert.Equal(
            $"Michigan's Children supports the statement.{Environment.NewLine}{Environment.NewLine}The proposed removal is unnecessary.",
            result);
    }

    [Fact]
    public void Normalize_RemovesMarkupAndNonContentElements()
    {
        const string source = "<p>First <strong>point</strong>.</p><p>Second&nbsp;point.</p><script>alert('x')</script><style>.hidden{}</style>";

        var result = CommentTextNormalizer.Normalize(source);

        Assert.Equal($"First point.{Environment.NewLine}{Environment.NewLine}Second point.", result);
        Assert.DoesNotContain("script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result, StringComparison.OrdinalIgnoreCase);
    }
}