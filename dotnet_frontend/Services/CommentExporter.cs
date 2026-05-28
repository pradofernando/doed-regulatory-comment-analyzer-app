using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Serializes comment payloads to formats the UI can hand back to the browser as downloads.
/// </summary>
public static class CommentExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string ToJson(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    public static string ToCsv(IReadOnlyList<CommentResource> comments)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            "comment_id",
            "posted_date",
            "modify_date",
            "agency_id",
            "document_type",
            "first_name",
            "last_name",
            "organization",
            "title",
            "comment",
        }));

        foreach (var c in comments)
        {
            var a = c.Attributes;
            sb.AppendLine(string.Join(",", new[]
            {
                Esc(c.Id),
                Esc(a.PostedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Esc(a.ModifyDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Esc(a.AgencyId),
                Esc(a.DocumentType),
                Esc(a.FirstName),
                Esc(a.LastName),
                Esc(a.Organization),
                Esc(a.Title),
                Esc(a.Comment),
            }));
        }

        return sb.ToString();
    }

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuoting = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        var escaped = value.Replace("\"", "\"\"");
        return needsQuoting ? $"\"{escaped}\"" : escaped;
    }
}
