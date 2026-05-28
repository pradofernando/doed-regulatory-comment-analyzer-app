using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Mirrors the Python function-app workflow: Agent 1 categorizes each comment, Agent 2 groups them
/// in batches and produces a collective JSON analysis.
///
/// Implementation note: this version targets the new Microsoft Foundry "prompt agents" via the
/// Azure OpenAI Responses API (POST /openai/v1/responses) with an "agent_reference" extra body
/// field. There are no asst_… IDs and no threads — multi-turn state is maintained server-side
/// using <c>previous_response_id</c>.
/// </summary>
public sealed class FoundryAnalysisService
{
    private readonly ILogger<FoundryAnalysisService> _logger;
    private readonly AttachmentExtractor _attachments;
    private readonly IHttpClientFactory _httpFactory;

    // Foundry uses Azure Cognitive Services token audience.
    private static readonly string[] FoundryScopes = new[] { "https://cognitiveservices.azure.com/.default" };

    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public FoundryAnalysisService(
        ILogger<FoundryAnalysisService> logger,
        AttachmentExtractor attachments,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _attachments = attachments;
        _httpFactory = httpFactory;
    }

    public async Task<AnalysisRun> RunAsync(
        string documentId,
        IReadOnlyList<CommentResource> comments,
        ApiSettings settings,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var run = new AnalysisRun
        {
            DocumentId = documentId,
            BatchSize = settings.BatchSize > 0 ? settings.BatchSize : ApiSettings.DefaultBatchSize,
            TotalComments = comments.Count,
        };

        if (comments.Count == 0)
        {
            run.Succeeded = false;
            run.ErrorMessage = "No comments to analyze.";
            run.CompletedAt = DateTimeOffset.UtcNow;
            return run;
        }
        if (!settings.IsFoundryConfigured)
        {
            run.Succeeded = false;
            run.ErrorMessage = "Foundry endpoint or agent names are not configured. Set them on the Settings page.";
            run.CompletedAt = DateTimeOffset.UtcNow;
            return run;
        }

        try
        {
            // PHASE 2 — Pull attachment text for every comment whose inline text is empty/short.
            var attachmentText = new Dictionary<string, AttachmentExtractionResult>(StringComparer.OrdinalIgnoreCase);
            progress?.Report(new AnalysisProgress { Phase = "Extracting attachments", Current = 0, Total = comments.Count, Message = "Scanning for attachments…" });

            for (var i = 0; i < comments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = comments[i];
                var inline = c.Attributes.Comment ?? string.Empty;
                var needsAttachments =
                    string.IsNullOrWhiteSpace(inline)
                    || inline.Length < 100
                    || inline.Contains("attach", StringComparison.OrdinalIgnoreCase);

                progress?.Report(new AnalysisProgress
                {
                    Phase = "Extracting attachments",
                    Current = i + 1,
                    Total = comments.Count,
                    Message = needsAttachments
                        ? $"Comment {i + 1}/{comments.Count}: fetching attachments…"
                        : $"Comment {i + 1}/{comments.Count}: using inline text.",
                });

                if (!needsAttachments) continue;
                try
                {
                    var extraction = await _attachments.ExtractAsync(c.Id, cancellationToken).ConfigureAwait(false);
                    if (extraction.HasContent || !string.IsNullOrWhiteSpace(extraction.DetailComment))
                    {
                        attachmentText[c.Id] = extraction;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Attachment extraction failed for {CommentId}", c.Id);
                }
            }

            using var foundry = new FoundryResponsesClient(_httpFactory.CreateClient("foundry"), settings.FoundryEndpoint, new DefaultAzureCredential());

            // PHASE 3 — Per-comment categorization (each call is independent, no chaining).
            progress?.Report(new AnalysisProgress { Phase = "Categorizing", Current = 0, Total = comments.Count, Message = "Connecting to categorization agent…" });

            for (var i = 0; i < comments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = comments[i];
                var submissionNumber = i + 1;
                attachmentText.TryGetValue(c.Id, out var attachExt);

                progress?.Report(new AnalysisProgress
                {
                    Phase = "Categorizing",
                    Current = submissionNumber,
                    Total = comments.Count,
                    Message = $"Categorizing comment {submissionNumber}/{comments.Count} ({c.Id})…",
                });

                var (rowString, textSource, attCount) = BuildRowString(submissionNumber, c, attachExt);
                var (rawResponse, _) = await foundry.CreateResponseAsync(
                    settings.CategorizationAgentName,
                    settings.CategorizationAgentVersion,
                    rowString,
                    previousResponseId: null,
                    cancellationToken,
                    _logger).ConfigureAwait(false);
                var parsed = TryParseJsonObject(rawResponse);

                run.Categorizations.Add(new CategorizationResult
                {
                    SubmissionNumber = submissionNumber,
                    CommentId = c.Id,
                    RowData = rowString,
                    RawResponse = rawResponse,
                    Parsed = parsed ?? new Dictionary<string, object?>(),
                    TextSource = textSource,
                    AttachmentsExtracted = attCount,
                });
            }

            // PHASE 4 — Batched grouping analysis. Chain batches via previous_response_id.
            progress?.Report(new AnalysisProgress { Phase = "Grouping", Current = 0, Total = comments.Count, Message = "Connecting to grouping agent…" });

            var batchSize = run.BatchSize;
            var totalBatches = (int)Math.Ceiling(run.Categorizations.Count / (double)batchSize);
            string finalResponse = string.Empty;
            string? groupingChainId = null;

            for (var batchStart = 0; batchStart < run.Categorizations.Count; batchStart += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchIndex = (batchStart / batchSize) + 1;
                var batchEnd = Math.Min(batchStart + batchSize, run.Categorizations.Count);
                var isLast = batchEnd >= run.Categorizations.Count;

                progress?.Report(new AnalysisProgress
                {
                    Phase = "Grouping",
                    Current = batchIndex,
                    Total = totalBatches,
                    Message = $"Sending batch {batchIndex}/{totalBatches} (comments {batchStart + 1}-{batchEnd})…",
                });

                var sb = new StringBuilder();
                if (batchIndex == 1)
                {
                    sb.Append($"I will show you categorized public comments in batches of {batchSize}. ");
                    sb.Append("Please remember all comments as I show them to you. ");
                    sb.Append("After all batches, I will ask for your collective analysis.\n\n");
                }
                sb.Append($"Batch {batchIndex}:\n\n");

                for (var k = batchStart; k < batchEnd; k++)
                {
                    var cat = run.Categorizations[k];
                    sb.Append($"--- Submission {cat.SubmissionNumber} (CSV Row {cat.SubmissionNumber}) ---\n");
                    sb.Append(cat.RawResponse.Trim());
                    sb.Append("\n\n");
                }

                sb.Append(isLast
                    ? $"\nThat was the final batch. You've now seen all {run.Categorizations.Count} comments.\n\n" +
                      "Please provide your collective analysis as a SINGLE JSON object with EXACTLY these keys (no prose before or after, wrap in a ```json fenced code block):\n" +
                      "{\n" +
                      "  \"overall_summary\": \"<2-4 sentence executive summary of the comments as a whole>\",\n" +
                      "  \"theme_groups\": [\n" +
                      "    {\n" +
                      "      \"group_name\": \"<short label>\",\n" +
                      "      \"group_description\": \"<1-2 sentences>\",\n" +
                      "      \"count\": <int>,\n" +
                      "      \"submission_numbers\": [<int>, ...],\n" +
                      "      \"stance_distribution\": { \"support\": <int>, \"oppose\": <int>, \"neutral\": <int>, \"mixed\": <int> },\n" +
                      "      \"common_arguments\": [\"<string>\", ...]\n" +
                      "    }\n" +
                      "  ],\n" +
                      "  \"patterns\": [\"<string>\", ...],\n" +
                      "  \"recommendations\": [\"<string>\", ...],\n" +
                      "  \"overall_sentiment\": \"<short label, e.g. 'mostly supportive' or 'mixed/oppositional'>\"\n" +
                      "}\n\n" +
                      "Group every submission into the theme that fits best (do not omit any). " +
                      "If the comments are sparse or content is missing, still produce the JSON with your best assessment."
                    : "\nAcknowledge receipt. More batches coming...");

                var (batchResponse, newResponseId) = await foundry.CreateResponseAsync(
                    settings.GroupingAgentName,
                    settings.GroupingAgentVersion,
                    sb.ToString(),
                    previousResponseId: groupingChainId,
                    cancellationToken,
                    _logger).ConfigureAwait(false);
                groupingChainId = newResponseId;

                if (isLast)
                {
                    finalResponse = batchResponse;
                }
            }

            run.Grouped = ParseGroupedAnalysis(finalResponse);
            run.Comments = comments;
            run.AttachmentText = attachmentText;
            run.Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            run.Succeeded = false;
            run.ErrorMessage = "Analysis was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis run failed");
            run.Succeeded = false;
            var isRateLimit = ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("token rate limit", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("429");
            run.ErrorMessage = isRateLimit
                ? "Azure OpenAI rate limit (TPM) exhausted after several automatic retries. " +
                  "Lower the batch size in Settings, wait a minute, or request a quota increase in the Azure AI Foundry portal " +
                  "(Management center → Quota). Original error: " + ex.Message
                : ex.Message;
        }
        finally
        {
            run.CompletedAt = DateTimeOffset.UtcNow;
        }

        return run;
    }

    /// <summary>
    /// Starts the follow-up Q&amp;A "conversation" by sending a priming message containing the full
    /// collective analysis. The Responses API doesn't have threads — instead we capture the
    /// returned response ID and store it in <see cref="AnalysisRun.FollowUpThreadId"/> (the column
    /// name is legacy; it now holds the most recent response_id for the chain).
    /// </summary>
    public async Task<string> StartFollowUpThreadAsync(
        AnalysisRun run,
        ApiSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.FollowUpAgentName))
            throw new InvalidOperationException("FollowUpAgentName is not configured.");
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (!run.Succeeded) throw new InvalidOperationException("Cannot start follow-up chat on an unsuccessful run.");

        using var foundry = new FoundryResponsesClient(_httpFactory.CreateClient("foundry"), settings.FoundryEndpoint, new DefaultAzureCredential());

        var priming = BuildFollowUpPriming(run);
        var (_, responseId) = await foundry.CreateResponseAsync(
            settings.FollowUpAgentName,
            settings.FollowUpAgentVersion,
            priming,
            previousResponseId: null,
            cancellationToken,
            _logger).ConfigureAwait(false);

        run.FollowUpThreadId = responseId;
        return responseId;
    }

    /// <summary>
    /// Sends a user question to the follow-up agent, chained off the previous response, and
    /// returns the reply. Appends both turns to <see cref="AnalysisRun.FollowUpHistory"/>.
    /// </summary>
    public async Task<string> AskFollowUpAsync(
        AnalysisRun run,
        string question,
        ApiSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.FollowUpAgentName))
            throw new InvalidOperationException("FollowUpAgentName is not configured.");
        if (string.IsNullOrWhiteSpace(run.FollowUpThreadId))
            throw new InvalidOperationException("Follow-up conversation has not been started. Call StartFollowUpThreadAsync first.");
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Question cannot be empty.", nameof(question));

        using var foundry = new FoundryResponsesClient(_httpFactory.CreateClient("foundry"), settings.FoundryEndpoint, new DefaultAzureCredential());

        run.FollowUpHistory.Add(new FollowUpTurn("user", question.Trim(), DateTimeOffset.UtcNow));
        var (reply, newResponseId) = await foundry.CreateResponseAsync(
            settings.FollowUpAgentName,
            settings.FollowUpAgentVersion,
            question.Trim(),
            previousResponseId: run.FollowUpThreadId,
            cancellationToken,
            _logger).ConfigureAwait(false);

        run.FollowUpThreadId = newResponseId;
        run.FollowUpHistory.Add(new FollowUpTurn("agent", reply, DateTimeOffset.UtcNow));
        return reply;
    }

    private static string BuildFollowUpPriming(AnalysisRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a follow-up Q&A assistant for a public-comments analysis. I will paste the full analysis below, and then ask questions. Use ONLY the analysis below; if something is not covered, say so.");
        sb.AppendLine();
        sb.AppendLine($"Docket / document: {run.DocumentId}");
        sb.AppendLine($"Total comments analyzed: {run.TotalComments}");
        sb.AppendLine();
        sb.AppendLine("=== OVERALL SUMMARY ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.Grouped.OverallSummary) ? "(none)" : run.Grouped.OverallSummary);
        sb.AppendLine();
        sb.AppendLine($"Overall sentiment: {run.Grouped.OverallSentiment ?? "(none)"}");
        sb.AppendLine();
        sb.AppendLine("=== THEME GROUPS ===");
        foreach (var g in run.Grouped.ThemeGroups)
        {
            sb.AppendLine($"- [{g.Count}] {g.GroupName}: {g.GroupDescription}");
            if (g.SubmissionNumbers.Count > 0)
                sb.AppendLine($"   submissions: {string.Join(", ", g.SubmissionNumbers)}");
            if (g.StanceDistribution.Count > 0)
                sb.AppendLine($"   stance: {string.Join(", ", g.StanceDistribution.Select(kv => $"{kv.Key}={kv.Value}"))}");
            foreach (var arg in g.CommonArguments)
                sb.AppendLine($"   • {arg}");
        }
        sb.AppendLine();
        sb.AppendLine("=== PATTERNS ===");
        foreach (var p in run.Grouped.Patterns) sb.AppendLine($"- {p}");
        sb.AppendLine();
        sb.AppendLine("=== RECOMMENDATIONS ===");
        foreach (var r in run.Grouped.Recommendations) sb.AppendLine($"- {r}");
        sb.AppendLine();
        sb.AppendLine("=== PER-COMMENT INDEX (truncated) ===");
        foreach (var cat in run.Categorizations)
        {
            var snippet = cat.RawResponse.Length > 600 ? cat.RawResponse[..600] + "…" : cat.RawResponse;
            sb.AppendLine($"#{cat.SubmissionNumber} ({cat.CommentId}): {snippet.Replace("\n", " ").Trim()}");
        }
        sb.AppendLine();
        sb.AppendLine("Acknowledge that you have the analysis loaded, in one short sentence, then wait for my first question.");
        return sb.ToString();
    }

    private static (string row, string source, int attCount) BuildRowString(
        int submissionNumber,
        CommentResource c,
        AttachmentExtractionResult? attachExt)
    {
        var a = c.Attributes;
        var commenter = string.Join(' ', new[] { a.FirstName, a.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        var inline = (a.Comment ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(inline) && !string.IsNullOrWhiteSpace(attachExt?.DetailComment))
        {
            inline = attachExt!.DetailComment.Trim();
        }

        var attachTextRaw = attachExt?.CombinedText ?? string.Empty;
        var attCount = attachExt?.Attachments.Count(x => x.Extracted) ?? 0;

        var inlineLooksLikePointer = !string.IsNullOrWhiteSpace(inline)
            && inline.Length < 60
            && inline.Contains("attach", StringComparison.OrdinalIgnoreCase);
        var keepInline = !string.IsNullOrWhiteSpace(inline) && !inlineLooksLikePointer;
        var combined = (keepInline, attachTextRaw.Length > 0) switch
        {
            (true, true)  => inline + "\n\n" + attachTextRaw,
            (true, false) => inline,
            (false, true) => attachTextRaw,
            _ => "[No text available]",
        };

        var source = (keepInline, attachTextRaw.Length > 0) switch
        {
            (true, true)  => "inline+attachment",
            (true, false) => "inline",
            (false, true) => "attachment",
            _ => "none",
        };

        const int MaxChars = 12000;
        if (combined.Length > MaxChars) combined = combined[..MaxChars] + "… [truncated]";

        combined = combined.Replace("\r\n", " ").Replace('\n', ' ');

        var fields = new[]
        {
            submissionNumber.ToString(),
            c.Id,
            a.PostedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            commenter,
            a.Organization ?? string.Empty,
            a.Title ?? string.Empty,
            attCount > 0 ? "true" : "false",
            combined,
        };
        return (string.Join(',', fields.Select(EscapeCsvField)), source, attCount);
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needs = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needs) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (t.Contains("```json", StringComparison.OrdinalIgnoreCase))
        {
            var i = t.IndexOf("```json", StringComparison.OrdinalIgnoreCase) + 7;
            var j = t.IndexOf("```", i, StringComparison.Ordinal);
            if (j > i) return t[i..j].Trim();
        }
        else if (t.StartsWith("```"))
        {
            var i = t.IndexOf("```", StringComparison.Ordinal) + 3;
            var j = t.IndexOf("```", i, StringComparison.Ordinal);
            if (j > i) return t[i..j].Trim();
        }
        return t;
    }

    private static Dictionary<string, object?>? TryParseJsonObject(string raw)
    {
        var clean = StripFences(raw);
        try
        {
            using var doc = JsonDocument.Parse(clean);
            return JsonElementToDict(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static GroupedAnalysis ParseGroupedAnalysis(string raw)
    {
        var result = new GroupedAnalysis { RawResponse = raw };

        var clean = StripFences(raw);
        if (TryDeserialize(clean, out var direct))
        {
            direct!.RawResponse = raw;
            direct.ParsedSuccessfully = true;
            return direct;
        }

        foreach (var candidate in ExtractJsonObjectCandidates(raw)
                     .OrderByDescending(s => s.Length))
        {
            if (!candidate.Contains("theme_groups", StringComparison.OrdinalIgnoreCase)
                && !candidate.Contains("overall_summary", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryDeserialize(candidate, out var scanned))
            {
                scanned!.RawResponse = raw;
                scanned.ParsedSuccessfully = true;
                return scanned;
            }
        }

        result.ParsedSuccessfully = false;
        return result;
    }

    private static bool TryDeserialize(string text, out GroupedAnalysis? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<GroupedAnalysis>(text, ParseOpts);
            return value is not null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static IEnumerable<string> ExtractJsonObjectCandidates(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            var depth = 0;
            var inString = false;
            var escape = false;
            for (var j = i; j < text.Length; j++)
            {
                var ch = text[j];
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return text[i..(j + 1)];
                        break;
                    }
                }
            }
        }
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => JsonElementToDict(el),
        _ => el.GetRawText(),
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Foundry Responses API client (raw HTTP). Uses DefaultAzureCredential for bearer token,
    // POSTs to {endpoint}/openai/v1/responses, and threads an "agent_reference" extra field
    // into the body so the new prompt-agent platform handles routing/instructions.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private sealed class FoundryResponsesClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly TokenCredential _credential;
        private readonly Uri _responsesUri;
        private AccessToken _cachedToken;

        public FoundryResponsesClient(HttpClient http, string endpoint, TokenCredential credential)
        {
            _http = http;
            _credential = credential;
            var trimmed = (endpoint ?? string.Empty).TrimEnd('/');
            _responsesUri = new Uri($"{trimmed}/openai/v1/responses");
        }

        public async Task<(string Text, string ResponseId)> CreateResponseAsync(
            string agentName,
            string agentVersion,
            string userText,
            string? previousResponseId,
            CancellationToken ct,
            ILogger logger)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                var body = new Dictionary<string, object?>
                {
                    ["input"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = userText,
                        }
                    },
                    ["agent_reference"] = new Dictionary<string, object?>
                    {
                        ["name"] = agentName,
                        ["version"] = string.IsNullOrWhiteSpace(agentVersion) ? "latest" : agentVersion,
                        ["type"] = "agent_reference",
                    },
                };
                if (!string.IsNullOrWhiteSpace(previousResponseId))
                {
                    body["previous_response_id"] = previousResponseId;
                }

                using var req = new HttpRequestMessage(HttpMethod.Post, _responsesUri);
                var token = await GetTokenAsync(ct).ConfigureAwait(false);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = JsonContent.Create(body);

                HttpResponseMessage? resp = null;
                try
                {
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                        return ExtractText(doc.RootElement);
                    }

                    var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var isRateLimit = (int)resp.StatusCode == 429
                        || errBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                        || errBody.Contains("token rate limit", StringComparison.OrdinalIgnoreCase);

                    if (isRateLimit && attempt < maxAttempts)
                    {
                        var waitSeconds = ParseRetryAfter(errBody) ?? Math.Min(60, (int)Math.Pow(2, attempt) * 2);
                        logger.LogWarning(
                            "Foundry responses rate-limited (attempt {Attempt}/{Max}). Waiting {Seconds}s. Detail: {Err}",
                            attempt, maxAttempts, waitSeconds, Truncate(errBody, 400));
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false);
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Foundry Responses API returned {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(errBody, 1200)}");
                }
                finally
                {
                    resp?.Dispose();
                }
            }
        }

        private async Task<string> GetTokenAsync(CancellationToken ct)
        {
            if (_cachedToken.ExpiresOn - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(2))
            {
                _cachedToken = await _credential.GetTokenAsync(new TokenRequestContext(FoundryScopes), ct).ConfigureAwait(false);
            }
            return _cachedToken.Token;
        }

        private static (string Text, string ResponseId) ExtractText(JsonElement root)
        {
            var responseId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty
                : string.Empty;

            // 1) Convenience flat field (common in SDKs).
            if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
            {
                return (ot.GetString() ?? string.Empty, responseId);
            }

            // 2) Walk output[].content[].text (canonical Responses API shape).
            var sb = new StringBuilder();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content)) continue;
                    if (content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var typeEl)
                            && (typeEl.GetString() == "output_text" || typeEl.GetString() == "text")
                            && part.TryGetProperty("text", out var txt))
                        {
                            if (txt.ValueKind == JsonValueKind.String)
                            {
                                sb.Append(txt.GetString());
                            }
                            else if (txt.ValueKind == JsonValueKind.Object
                                && txt.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                            {
                                sb.Append(v.GetString());
                            }
                        }
                    }
                }
            }
            return (sb.ToString(), responseId);
        }

        private static int? ParseRetryAfter(string errMsg)
        {
            if (string.IsNullOrEmpty(errMsg)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(errMsg, @"retry[\s\-]*after[^\d]{0,15}(\d{1,4})\s*seconds?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var sec)) return Math.Min(120, Math.Max(1, sec) + 1);
            return null;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

        public void Dispose() { }
    }
}
