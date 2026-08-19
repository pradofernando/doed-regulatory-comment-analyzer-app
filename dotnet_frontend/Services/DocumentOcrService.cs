using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace DoedRegulatoryComments.Web.Services;

public interface IDocumentOcrService
{
    bool IsConfigured { get; }
    Task<string> ExtractPdfTextAsync(byte[] pdfContent, int maxPages, CancellationToken ct = default);
}

public sealed class AzureDocumentOcrService : IDocumentOcrService
{
    private readonly DocumentIntelligenceClient? _client;

    public AzureDocumentOcrService(
        IOptions<AttachmentProcessingOptions> options,
        TokenCredential credential)
    {
        var endpoint = options.Value.OcrEndpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        _client = new DocumentIntelligenceClient(new Uri(endpoint), credential);
    }

    public bool IsConfigured => _client is not null;

    public async Task<string> ExtractPdfTextAsync(
        byte[] pdfContent,
        int maxPages,
        CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Document Intelligence OCR is not configured.");
        if (pdfContent.Length == 0)
            throw new ArgumentException("PDF content cannot be empty.", nameof(pdfContent));

        var options = new AnalyzeDocumentOptions("prebuilt-read", BinaryData.FromBytes(pdfContent))
        {
            Pages = $"1-{Math.Max(1, maxPages)}",
        };
        Operation<AnalyzeResult> operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            options,
            ct).ConfigureAwait(false);
        return operation.Value.Content?.Trim() ?? string.Empty;
    }
}