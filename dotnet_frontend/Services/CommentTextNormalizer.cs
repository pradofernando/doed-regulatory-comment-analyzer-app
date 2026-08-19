using System.Text;
using HtmlAgilityPack;

namespace DoedRegulatoryComments.Web.Services;

public static class CommentTextNormalizer
{
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "div", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "li", "main", "nav", "ol", "p", "pre", "section", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "ul",
    };

    private static readonly HashSet<string> IgnoredElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template",
    };

    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var document = new HtmlDocument();
        document.LoadHtml(HtmlEntity.DeEntitize(value));

        var output = new StringBuilder(value.Length);
        AppendNode(document.DocumentNode, output);
        return NormalizeWhitespace(output.ToString());
    }

    private static void AppendNode(HtmlNode node, StringBuilder output)
    {
        if (node.NodeType == HtmlNodeType.Comment || IgnoredElements.Contains(node.Name)) return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            output.Append(HtmlEntity.DeEntitize(node.InnerText));
            return;
        }

        if (node.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            AppendLineBreak(output);
            return;
        }

        var isBlock = BlockElements.Contains(node.Name);
        if (isBlock) AppendLineBreak(output);
        foreach (var child in node.ChildNodes) AppendNode(child, output);
        if (isBlock) AppendLineBreak(output);
    }

    private static void AppendLineBreak(StringBuilder output)
    {
        if (output.Length == 0) return;
        if (output[^1] != '\n' || output.Length == 1 || output[^2] != '\n') output.Append('\n');
    }

    private static string NormalizeWhitespace(string value)
    {
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ');
        var lines = new List<string>();
        var previousWasBlank = true;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = string.Join(' ', rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0)
            {
                if (!previousWasBlank) lines.Add(string.Empty);
                previousWasBlank = true;
                continue;
            }

            lines.Add(line);
            previousWasBlank = false;
        }

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines);
    }
}