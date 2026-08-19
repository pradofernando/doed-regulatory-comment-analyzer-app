namespace DoedRegulatoryComments.Web.Services;

public sealed class AttachmentProcessingOptions
{
    public const string SectionName = "Attachments";
    public const long DefaultMaxDownloadBytes = 25 * 1024 * 1024;

    public string[] AllowedHosts { get; set; } = ["downloads.regulations.gov"];
    public long MaxDownloadBytes { get; set; } = DefaultMaxDownloadBytes;
    public int MaxRedirects { get; set; } = 3;
    public int MaxArchiveEntries { get; set; } = 1000;
    public long MaxArchiveUncompressedBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxExtractedTextCharacters { get; set; } = 500_000;
    public int MaxPdfPages { get; set; } = 100;
    public int MaxOcrPages { get; set; } = 50;
    public int MinPdfTextCharactersPerPage { get; set; } = 20;
    public string OcrEndpoint { get; set; } = string.Empty;
}

public enum AttachmentFileKind
{
    Pdf,
    WordOpenXml,
    LegacyWord,
}

public sealed class AttachmentDownloadResult
{
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public AttachmentFileKind? FileKind { get; init; }
    public string? MediaType { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => Error is null && FileKind.HasValue && Content.Length > 0;

    internal static AttachmentDownloadResult Rejected(string error, string? mediaType = null) =>
        new() { Error = error, MediaType = mediaType };
}