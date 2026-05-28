using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Mirrors the Python function-app's PHASE 2 attachment-extraction step:
/// when a comment has empty or near-empty inline text, fetch its attachments
/// and extract the text from each PDF/DOCX so the AI agent has something to analyze.
/// </summary>
public sealed class AttachmentExtractor
{
    private readonly RegulationsGovClient _client;
    private readonly ILogger<AttachmentExtractor> _logger;

    public AttachmentExtractor(RegulationsGovClient client, ILogger<AttachmentExtractor> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AttachmentExtractionResult> ExtractAsync(string commentId, CancellationToken ct = default)
    {
        var result = new AttachmentExtractionResult { CommentId = commentId };
        var detail = await _client.GetCommentAsync(commentId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            result.Error = "Failed to load comment detail.";
            return result;
        }

        // Regulations.gov returns the full inline comment text ONLY on the detail endpoint —
        // the list endpoint omits it. Capture it here so the categorization phase can fall back to it.
        result.DetailComment = (detail.Data?.Attributes.Comment ?? string.Empty).Trim();

        var attachments = detail.Included.Where(i => string.Equals(i.Type, "attachments", StringComparison.OrdinalIgnoreCase)).ToList();
        if (attachments.Count == 0) return result;

        var combined = new StringBuilder();
        foreach (var att in attachments)
        {
            ct.ThrowIfCancellationRequested();
            var title = string.IsNullOrWhiteSpace(att.Attributes.Title) ? $"attachment_{att.Id}" : att.Attributes.Title!;
            var first = att.Attributes.FileFormats?.FirstOrDefault();
            if (first is null || string.IsNullOrWhiteSpace(first.FileUrl))
            {
                result.Attachments.Add(new AttachmentText { Title = title, Format = "", Error = "no file URL" });
                continue;
            }

            var format = (first.Format ?? "").ToLowerInvariant();
            var bytes = await _client.DownloadAttachmentAsync(first.FileUrl!, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                result.Attachments.Add(new AttachmentText { Title = title, Format = format, Error = "download failed" });
                continue;
            }

            string? text = null;
            try
            {
                text = format switch
                {
                    "pdf" => ExtractPdfText(bytes),
                    "docx" or "doc" or "msw12" => ExtractDocxText(bytes),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Attachment text extraction failed for {Title} ({Format})", title, format);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Attachments.Add(new AttachmentText { Title = title, Format = format, Error = string.IsNullOrEmpty(text) ? "no text extracted" : null });
                continue;
            }

            result.Attachments.Add(new AttachmentText
            {
                Title = title,
                Format = format,
                Text = text!.Trim(),
                Extracted = true,
            });
            combined.Append("\n\n--- Attachment: ").Append(title).Append(" ---\n\n").Append(text!.Trim());
        }

        result.CombinedText = combined.ToString().Trim();
        result.HasContent = result.CombinedText.Length > 0;
        return result;
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var pdf = PdfDocument.Open(stream);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            var t = page.Text;
            if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
        }
        return sb.ToString();
    }

    private static string ExtractDocxText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
        {
            var text = para.InnerText;
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
        }
        return sb.ToString();
    }
}

public class AttachmentExtractionResult
{
    public string CommentId { get; set; } = string.Empty;
    public List<AttachmentText> Attachments { get; set; } = new();
    public string CombinedText { get; set; } = string.Empty;
    public bool HasContent { get; set; }
    public string? Error { get; set; }

    /// <summary>Full inline comment text pulled from the /comments/{id} detail endpoint (which has it, unlike the list endpoint).</summary>
    public string DetailComment { get; set; } = string.Empty;
}

public class AttachmentText
{
    public string Title { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool Extracted { get; set; }
    public string? Error { get; set; }
}
