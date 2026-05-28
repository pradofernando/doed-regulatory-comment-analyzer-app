using System.Net.Http.Json;
using System.Text.Json;

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

    public RegulationsGovClient(HttpClient http, ApiSettingsStore settingsStore, ILogger<RegulationsGovClient> logger)
    {
        _http = http;
        _settingsStore = settingsStore;
        _logger = logger;
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
            var lastDash = searchId.LastIndexOf('-');
            if (lastDash > 0)
            {
                searchId = searchId[..lastDash];
            }
            filterParam = "filter[docketId]";
        }

        var pageSize = Math.Clamp(request.PageSize <= 0 ? 25 : request.PageSize, 5, 250);
        var page = request.Page <= 0 ? 1 : request.Page;

        var query = new Dictionary<string, string?>
        {
            [filterParam] = searchId,
            ["page[size]"] = pageSize.ToString(),
            ["page[number]"] = page.ToString(),
            ["sort"] = "-postedDate",
            ["include"] = "attachments",
        };

        var url = BuildUrl(settings.BaseUrl, "comments", query);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeader(httpRequest, settings);

        try
        {
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

            return new FetchCommentsResult
            {
                Success = true,
                Comments = data.Data,
                TotalPages = data.Meta?.NumberOfPages,
                TotalElements = data.Meta?.TotalElements,
                RequestedUrl = url,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch comments");
            return new FetchCommentsResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                RequestedUrl = url,
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
            return await response.Content.ReadFromJsonAsync<CommentDetailResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get comment {CommentId}", commentId);
            return null;
        }
    }

    /// <summary>
    /// Downloads an attachment file (PDF, DOCX, …) referenced by a comment. Returns null on error.
    /// The Regulations.gov CDN (downloads.regulations.gov) requires browser-like headers, so we mirror
    /// what the Python function uses: X-Api-Key + a real User-Agent + a PDF/DOCX Accept + a Referer.
    /// </summary>
    public async Task<byte[]?> DownloadAttachmentAsync(string fileUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) return null;
        var settings = _settingsStore.Current;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);
        }
        httpRequest.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        httpRequest.Headers.TryAddWithoutValidation("Accept",
            "application/pdf,application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,*/*");
        httpRequest.Headers.TryAddWithoutValidation("Referer", "https://www.regulations.gov/");

        try
        {
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Attachment download failed {Status} for {Url}", (int)response.StatusCode, fileUrl);
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download attachment {Url}", fileUrl);
            return null;
        }
    }

    private static void AddAuthHeader(HttpRequestMessage request, ApiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            // Regulations.gov uses the X-Api-Key header; pass it for custom backends too — harmless if unused.
            request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);
        }
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
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
