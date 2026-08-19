using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
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
    private readonly AttachmentProcessingOptions _options;
    private readonly IDocumentOcrService _ocr;
    private readonly OperationalTelemetry _telemetry;

    public AttachmentExtractor(
        RegulationsGovClient client,
        ILogger<AttachmentExtractor> logger,
        IOptions<AttachmentProcessingOptions> options,
        IDocumentOcrService ocr,
        OperationalTelemetry telemetry)
    {
        _client = client;
        _logger = logger;
        _options = options.Value;
        _ocr = ocr;
        _telemetry = telemetry;
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
            var download = await _client.DownloadAttachmentAsync(first.FileUrl!, ct).ConfigureAwait(false);
            if (!download.Succeeded)
            {
                _telemetry.RecordAttachmentFailure("download_rejected", format);
                result.Attachments.Add(new AttachmentText
                {
                    Title = title,
                    Format = format,
                    Error = download.Error ?? "download failed",
                });
                continue;
            }
            _telemetry.RecordAttachmentDownload(download.FileKind!.Value, download.Content.LongLength);

            string? text = null;
            var usedOcr = false;
            var pageCount = 0;
            var pagesProcessed = 0;
            var truncated = false;
            try
            {
                if (download.FileKind == AttachmentFileKind.Pdf)
                {
                    var pdf = ExtractPdfText(
                        download.Content,
                        _options.MaxPdfPages,
                        _options.MinPdfTextCharactersPerPage,
                        _options.MaxExtractedTextCharacters);
                    text = pdf.Text;
                    pageCount = pdf.PageCount;
                    pagesProcessed = pdf.PagesProcessed;
                    truncated = pdf.Truncated;

                    if (pdf.NeedsOcr && _ocr.IsConfigured)
                    {
                        var ocrPageLimit = Math.Min(
                            Math.Min(_options.MaxPdfPages, _options.MaxOcrPages),
                            Math.Max(1, pdf.PageCount));
                        var ocrText = await _ocr.ExtractPdfTextAsync(
                            download.Content,
                            ocrPageLimit,
                            ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ocrText))
                        {
                            text = LimitText(ocrText, _options.MaxExtractedTextCharacters, out var ocrTruncated);
                            usedOcr = true;
                            pagesProcessed = ocrPageLimit;
                            truncated |= ocrTruncated;
                            _telemetry.RecordAttachmentOcr(succeeded: true);
                        }
                        else
                        {
                            _telemetry.RecordAttachmentOcr(succeeded: false);
                        }
                    }
                }
                else if (download.FileKind == AttachmentFileKind.WordOpenXml)
                {
                    text = ExtractDocxText(download.Content, _options.MaxExtractedTextCharacters, out truncated);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _telemetry.RecordAttachmentFailure("extraction_failed", format);
                _logger.LogWarning(ex, "Attachment text extraction failed for {Title} ({Format})", title, format);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _telemetry.RecordAttachmentFailure("no_text", format);
                result.Attachments.Add(new AttachmentText { Title = title, Format = format, Error = string.IsNullOrEmpty(text) ? "no text extracted" : null });
                continue;
            }

            result.Attachments.Add(new AttachmentText
            {
                Title = title,
                Format = format,
                Text = text!.Trim(),
                Extracted = true,
                UsedOcr = usedOcr,
                PageCount = pageCount,
                PagesProcessed = pagesProcessed,
                Truncated = truncated,
            });
            combined.Append("\n\n--- Attachment: ").Append(title).Append(" ---\n\n").Append(text!.Trim());
        }

        result.CombinedText = combined.ToString().Trim();
        result.HasContent = result.CombinedText.Length > 0;
        return result;
    }

    internal static PdfTextExtraction ExtractPdfText(
        byte[] bytes,
        int maxPages,
        int minTextCharactersPerPage,
        int maxTextCharacters)
    {
        using var stream = new MemoryStream(bytes);
        using var pdf = PdfDocument.Open(stream);
        var pageCount = pdf.NumberOfPages;
        var pagesToProcess = Math.Min(pageCount, Math.Max(1, maxPages));
        var sb = new StringBuilder();
        var pagesProcessed = 0;
        var sparsePageFound = false;
        var textTruncated = false;
        foreach (var page in pdf.GetPages().Take(pagesToProcess))
        {
            var t = page.Text;
            pagesProcessed++;
            sparsePageFound |= ShouldUseOcr(t, 1, minTextCharactersPerPage);
            if (string.IsNullOrWhiteSpace(t)) continue;

            var remaining = Math.Max(0, maxTextCharacters - sb.Length);
            if (remaining == 0)
            {
                textTruncated = true;
                break;
            }
            if (t.Length > remaining)
            {
                sb.Append(t.AsSpan(0, remaining));
                textTruncated = true;
                break;
            }
            sb.AppendLine(t);
        }
        var text = sb.ToString().Trim();
        return new PdfTextExtraction(
            text,
            pageCount,
            pagesProcessed,
            pageCount > pagesProcessed || textTruncated,
            sparsePageFound || ShouldUseOcr(text, pagesProcessed, minTextCharactersPerPage));
    }

    internal static bool ShouldUseOcr(string? text, int pagesProcessed, int minTextCharactersPerPage)
    {
        var meaningfulCharacters = text?.Count(char.IsLetterOrDigit) ?? 0;
        var threshold = Math.Max(1, pagesProcessed) * Math.Max(1, minTextCharactersPerPage);
        return meaningfulCharacters < threshold;
    }

    private static string ExtractDocxText(byte[] bytes, int maxCharacters, out bool truncated)
    {
        truncated = false;
        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
        {
            var text = para.InnerText;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var remaining = Math.Max(0, maxCharacters - sb.Length);
            if (remaining == 0)
            {
                truncated = true;
                break;
            }
            if (text.Length > remaining)
            {
                sb.Append(text.AsSpan(0, remaining));
                truncated = true;
                break;
            }
            sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private static string LimitText(string text, int maxCharacters, out bool truncated)
    {
        truncated = text.Length > maxCharacters;
        return truncated ? text[..maxCharacters] : text;
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
    public bool UsedOcr { get; set; }
    public int PageCount { get; set; }
    public int PagesProcessed { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}

internal sealed record PdfTextExtraction(
    string Text,
    int PageCount,
    int PagesProcessed,
    bool Truncated,
    bool NeedsOcr);
