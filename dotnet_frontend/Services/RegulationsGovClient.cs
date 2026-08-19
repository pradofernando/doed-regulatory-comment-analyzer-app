using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Talks to the regulatory comments API. Defaults to Regulations.gov v4 but works against any
/// compatible JSON:API backend whose base URL is set in <see cref="ApiSettings.BaseUrl"/>.
/// </summary>
public class RegulationsGovClient
{
    private readonly HttpClient _http;
    private readonly ApiSettingsStore _settingsStore;
    private readonly ILogger<RegulationsGovClient> _logger;
    private readonly AttachmentProcessingOptions _attachmentOptions;

    public RegulationsGovClient(
        HttpClient http,
        ApiSettingsStore settingsStore,
        ILogger<RegulationsGovClient> logger,
        IOptions<AttachmentProcessingOptions> attachmentOptions)
    {
        _http = http;
        _settingsStore = settingsStore;
        _logger = logger;
        _attachmentOptions = attachmentOptions.Value;
    }

    public async Task<FetchCommentsResult> FetchCommentsAsync(FetchCommentsRequest request, CancellationToken ct = default)
    {
        var settings = _settingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new FetchCommentsResult { Success = false, ErrorMessage = "API base URL is not configured." };
        }
        if (string.IsNullOrWhiteSpace(request.DocumentId))
        {
            return new FetchCommentsResult { Success = false, ErrorMessage = "A document or docket ID is required." };
        }

        var searchId = request.DocumentId.Trim();
        var filterParam = "filter[commentOnId]";

        if (request.UseDocketFilter)
        {
            searchId = ToDocketId(searchId);
            filterParam = "filter[docketId]";
        }

        var pageSize = Math.Clamp(request.PageSize <= 0 ? 25 : request.PageSize, 5, 250);
        var page = 1;
        var comments = new List<CommentResource>();
        var commentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? totalPages = null;
        int? totalElements = null;
        string? requestedUrl = null;

        try
        {
            while (true)
            {
                var query = new Dictionary<string, string?>
                {
                    [filterParam] = searchId,
                    ["page[size]"] = pageSize.ToString(),
                    ["page[number]"] = page.ToString(),
                    ["sort"] = "-postedDate",
                    ["include"] = "attachments",
                };

                var url = BuildUrl(settings.BaseUrl, "comments", query);
                requestedUrl ??= url;
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeader(httpRequest, settings);
                using var response = await _http.SendAsync(httpRequest, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await SafeReadBody(response, ct);
                    return new FetchCommentsResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 400)}",
                        RequestedUrl = url,
                    };
                }

                var data = await response.Content.ReadFromJsonAsync<CommentListResponse>(cancellationToken: ct)
                           ?? new CommentListResponse();
                NormalizeComments(data.Data);
                totalPages = data.Meta?.NumberOfPages ?? totalPages;
                totalElements = data.Meta?.TotalElements ?? totalElements;

                var added = 0;
                foreach (var comment in data.Data)
                {
                    if (commentIds.Add(comment.Id))
                    {
                        comments.Add(comment);
                        added++;
                    }
                }

                if (data.Data.Count == 0
                    || added == 0
                    || (totalPages.HasValue && page >= totalPages.Value)
                    || (!totalPages.HasValue && data.Data.Count < pageSize))
                {
                    break;
                }

                page++;
            }

            return new FetchCommentsResult
            {
                Success = true,
                Comments = comments,
                TotalPages = totalPages,
                TotalElements = totalElements,
                RequestedUrl = requestedUrl,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch comments");
            return new FetchCommentsResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                RequestedUrl = requestedUrl,
            };
        }
    }

    public async Task<CommentDetailResponse?> GetCommentAsync(string commentId, CancellationToken ct = default)
    {
        var settings = _settingsStore.Current;
        if (string.IsNullOrWhiteSpace(commentId)) return null;

        var url = BuildUrl(settings.BaseUrl, $"comments/{Uri.EscapeDataString(commentId)}", new Dictionary<string, string?>
        {
            ["include"] = "attachments",
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeader(httpRequest, settings);

        try
        {
            using var response = await _http.SendAsync(httpRequest, ct);
            if (!response.IsSuccessStatusCode) return null;
            var detail = await response.Content.ReadFromJsonAsync<CommentDetailResponse>(cancellationToken: ct);
            NormalizeComment(detail?.Data);
            return detail;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get comment {CommentId}", commentId);
            return null;
        }
    }

    /// <summary>
    /// Downloads a validated attachment referenced by a comment, bounded by the configured byte limit.
    /// The Regulations.gov CDN (downloads.regulations.gov) requires browser-like headers, so we mirror
    /// what the Python function uses: X-Api-Key + a real User-Agent + a PDF/DOCX Accept + a Referer.
    /// </summary>
    public async Task<AttachmentDownloadResult> DownloadAttachmentAsync(
        string fileUrl,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var attachmentUri))
            return AttachmentDownloadResult.Rejected("Attachment URL is invalid.");

        var uriError = ValidateAttachmentUri(attachmentUri);
        if (uriError is not null)
            return AttachmentDownloadResult.Rejected(uriError);

        var settings = _settingsStore.Current;
        var currentUri = attachmentUri;
        HttpResponseMessage? response = null;

        try
        {
            for (var redirectCount = 0; ; redirectCount++)
            {
                using var request = CreateAttachmentRequest(currentUri, settings);
                response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode)) break;

                if (redirectCount >= _attachmentOptions.MaxRedirects)
                    return AttachmentDownloadResult.Rejected(
                        $"Attachment exceeded the {_attachmentOptions.MaxRedirects}-redirect limit.");

                var location = response.Headers.Location;
                if (location is null)
                    return AttachmentDownloadResult.Rejected("Attachment redirect did not include a Location header.");
                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                var redirectError = ValidateAttachmentUri(nextUri);
                if (redirectError is not null)
                {
                    _logger.LogWarning(
                        "Attachment redirect from host {SourceHost} was rejected: {Reason}",
                        currentUri.IdnHost,
                        redirectError);
                    return AttachmentDownloadResult.Rejected(redirectError);
                }

                response.Dispose();
                response = null;
                currentUri = nextUri;
            }

            var finalResponse = response
                ?? throw new InvalidOperationException("Attachment response was unavailable.");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Attachment download failed with status {Status} for host {Host}",
                    (int)response.StatusCode,
                    currentUri.IdnHost);
                return AttachmentDownloadResult.Rejected(
                    $"Attachment download returned HTTP {(int)response.StatusCode}.");
            }

            var mediaType = finalResponse.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
            if (!IsSupportedMediaType(mediaType))
                return AttachmentDownloadResult.Rejected(
                    $"Attachment MIME type '{mediaType ?? "missing"}' is not allowed.", mediaType);

            var maxBytes = _attachmentOptions.MaxDownloadBytes;
            if (finalResponse.Content.Headers.ContentLength is > 0 and var declaredLength
                && declaredLength > maxBytes)
            {
                return AttachmentDownloadResult.Rejected(
                    $"Attachment exceeds the {maxBytes}-byte download limit.", mediaType);
            }

            await using var input = await finalResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    totalBytes += read;
                    if (totalBytes > maxBytes)
                    {
                        return AttachmentDownloadResult.Rejected(
                            $"Attachment exceeds the {maxBytes}-byte download limit.", mediaType);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var content = output.ToArray();
            var fileKind = DetectFileKind(content);
            if (fileKind is null)
                return AttachmentDownloadResult.Rejected(
                    "Attachment file signature is not a supported PDF or Word document.", mediaType);
            if (fileKind == AttachmentFileKind.WordOpenXml
                && ValidateOpenXmlArchive(content) is { } archiveError)
            {
                return AttachmentDownloadResult.Rejected(archiveError, mediaType);
            }
            if (!MediaTypeMatchesFileKind(mediaType!, fileKind.Value))
                return AttachmentDownloadResult.Rejected(
                    $"Attachment MIME type '{mediaType}' does not match its file signature.", mediaType);

            return new AttachmentDownloadResult
            {
                Content = content,
                FileKind = fileKind,
                MediaType = mediaType,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download attachment from host {Host}", attachmentUri.IdnHost);
            return AttachmentDownloadResult.Rejected("Attachment download failed.");
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static HttpRequestMessage CreateAttachmentRequest(Uri uri, ApiSettings settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);
        }
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept",
            "application/pdf,application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,*/*");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.regulations.gov/");
        return request;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private string? ValidateAttachmentUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "Attachment URL must use HTTPS.";
        if (!uri.IsDefaultPort && uri.Port != 443)
            return "Attachment URL must use the standard HTTPS port.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Attachment URL must not contain user information.";

        var host = uri.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out _))
            return "Attachment URL must use an allowlisted DNS host, not an IP address.";
        var allowed = _attachmentOptions.AllowedHosts.Any(entry => HostMatches(host, entry));
        return allowed ? null : $"Attachment host '{host}' is not allowed.";
    }

    private static bool HostMatches(string host, string configuredHost)
    {
        var allowed = configuredHost.Trim().TrimEnd('.');
        if (allowed.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = allowed[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }
        return host.Equals(allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedMediaType(string? mediaType) => mediaType is
        "application/pdf"
        or "application/x-pdf"
        or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        or "application/msword"
        or "application/vnd.ms-word"
        or "application/zip"
        or "application/octet-stream"
        or "binary/octet-stream";

    private static AttachmentFileKind? DetectFileKind(byte[] content)
    {
        ReadOnlySpan<byte> bytes = content;
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8))
            return AttachmentFileKind.Pdf;
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(
                new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            return AttachmentFileKind.LegacyWord;
        if (bytes.Length < 4 || !bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
            return null;

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.GetEntry("[Content_Types].xml") is not null
                && archive.GetEntry("word/document.xml") is not null
                ? AttachmentFileKind.WordOpenXml
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private string? ValidateOpenXmlArchive(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > _attachmentOptions.MaxArchiveEntries)
                return $"Word attachment exceeds the {_attachmentOptions.MaxArchiveEntries}-entry archive limit.";

            long uncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > _attachmentOptions.MaxArchiveUncompressedBytes - uncompressedBytes)
                {
                    return $"Word attachment exceeds the {_attachmentOptions.MaxArchiveUncompressedBytes}-byte uncompressed limit.";
                }
                uncompressedBytes += entry.Length;
            }
            return null;
        }
        catch (InvalidDataException)
        {
            return "Word attachment archive is invalid.";
        }
    }

    private static bool MediaTypeMatchesFileKind(string mediaType, AttachmentFileKind fileKind) =>
        mediaType is "application/octet-stream" or "binary/octet-stream"
        || (fileKind == AttachmentFileKind.Pdf
            && mediaType is "application/pdf" or "application/x-pdf")
        || (fileKind == AttachmentFileKind.WordOpenXml
            && mediaType is "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/msword" or "application/vnd.ms-word" or "application/zip")
        || (fileKind == AttachmentFileKind.LegacyWord
            && mediaType is "application/msword" or "application/vnd.ms-word");

    private static void AddAuthHeader(HttpRequestMessage request, ApiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            // Regulations.gov uses the X-Api-Key header; pass it for custom backends too — harmless if unused.
            request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);
        }
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    internal static string ToDocketId(string id)
    {
        var trimmed = id.Trim();
        if (trimmed.Count(character => character == '-') < 4) return trimmed;
        var lastDash = trimmed.LastIndexOf('-');
        return lastDash > 0 ? trimmed[..lastDash] : trimmed;
    }

    private static void NormalizeComments(IEnumerable<CommentResource> comments)
    {
        foreach (var comment in comments) NormalizeComment(comment);
    }

    private static void NormalizeComment(CommentResource? comment)
    {
        if (comment is null) return;
        comment.Attributes.Comment = CommentTextNormalizer.Normalize(comment.Attributes.Comment);
    }

    private static string BuildUrl(string baseUrl, string path, IDictionary<string, string?> query)
    {
        var root = baseUrl.TrimEnd('/');
        var trimmedPath = path.TrimStart('/');
        var queryString = string.Join("&", query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return string.IsNullOrEmpty(queryString)
            ? $"{root}/{trimmedPath}"
            : $"{root}/{trimmedPath}?{queryString}";
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
