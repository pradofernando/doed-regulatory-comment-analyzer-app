using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DoedRegulatoryComments.Web.Services;

public static class AnalystEngine
{
    public const string ReviewedGeneratedLabel = "Reviewed-generated summary";
    public const double NearDuplicateSimilarityThreshold = 0.90;
    public const int NearDuplicateMinimumWords = 40;
    public const int NearDuplicateMinimumCharacters = 200;
    public const int NearDuplicateMinimumUniqueShingles = 20;
    public const int ShingleWordCount = 5;
    public const int MaxNearDuplicateCandidates = 64;
    public const int NearDuplicateSignatureSize = 16;

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static AnalysisRun ApplyReviews(AnalysisRun original, IReadOnlyList<CommentReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(reviews);
        var effective = WorkspaceJson.Clone(original);
        effective.PersistedId = original.PersistedId;
        effective.Comments = WorkspaceJson.Clone(original.Comments.ToList());
        effective.AttachmentText = new Dictionary<string, AttachmentExtractionResult>(
            WorkspaceJson.Clone(original.AttachmentText), StringComparer.OrdinalIgnoreCase);
        effective.Grouped.RawResponse = original.Grouped.RawResponse;
        effective.Grouped.ParsedSuccessfully = original.Grouped.ParsedSuccessfully;

        var latest = LatestReviews(reviews);
        var substantiveChange = false;
        foreach (var cat in effective.Categorizations)
        {
            if (!latest.TryGetValue(cat.CommentId, out var review)) continue;
            substantiveChange |= ClassificationChanged(cat, review);
            OverlayReview(cat, review);
        }
        if (substantiveChange)
            effective.Grouped = RebuildGroups(effective, original.Grouped);
        return effective;
    }

    public static string GetField(CategorizationResult cat, params string[] names)
    {
        foreach (var name in names)
        {
            if (!cat.Parsed.TryGetValue(name, out var value))
                value = cat.Parsed.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            var text = FieldText(value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }

    public static IReadOnlyList<CategorizationResult> Filter(
        AnalysisRun run, RunReviewState state, CommentSearchFilter filter)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.PostedFrom?.Date > filter.PostedTo?.Date)
            throw new ArgumentException("The start date must be on or before the end date.", nameof(filter));

        var latest = LatestReviews(state.Revisions);
        var sources = run.Sources.ToLookup(x => x.CommentId, StringComparer.OrdinalIgnoreCase);
        var comments = run.Comments.ToLookup(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var query = filter.Query.Trim();
        var organization = filter.Organization.Trim();
        var theme = filter.Theme.Trim();
        var stance = NormalizeStance(filter.Stance);
        var status = filter.ReviewStatus.Trim();
        var result = new List<CategorizationResult>();

        foreach (var originalCat in run.Categorizations)
        {
            latest.TryGetValue(originalCat.CommentId, out var review);
            var cat = originalCat;
            if (review is not null)
            {
                cat = WorkspaceJson.Clone(originalCat);
                OverlayReview(cat, review);
            }
            var retainedSources = sources[cat.CommentId]
                .Where(x => x.SubmissionNumber == cat.SubmissionNumber || x.SubmissionNumber == 0).ToList();
            var comment = comments[cat.CommentId].FirstOrDefault();
            var posted = retainedSources.Select(x => x.PostedAt).FirstOrDefault(x => x.HasValue)
                ?? comment?.Attributes.PostedDate
                ?? ReadPostedDate(cat);
            if ((filter.PostedFrom.HasValue || filter.PostedTo.HasValue) && !posted.HasValue)
                continue;
            if (filter.PostedFrom.HasValue && posted!.Value.UtcDateTime.Date < filter.PostedFrom.Value.Date)
                continue;
            if (filter.PostedTo.HasValue && posted!.Value.UtcDateTime.Date > filter.PostedTo.Value.Date)
                continue;
            if (organization.Length > 0
                && !retainedSources.Any(x => Contains(x.Organization, organization))
                && !Contains(comment?.Attributes.Organization, organization)
                && !Contains(GetField(cat, "organization", "commenter_organization"), organization))
                continue;
            if (theme.Length > 0
                && !Contains(GetField(cat, "primary_theme", "primary_topic", "topic"), theme)
                && !Contains(GetField(cat, "canonical_reason"), theme))
                continue;
            if (stance.Length > 0 && NormalizeStance(GetField(cat, "stance", "position")) != stance)
                continue;
            var reviewStatus = review?.Status ?? "unreviewed";
            if (status.Length > 0 && !reviewStatus.Equals(status, StringComparison.OrdinalIgnoreCase))
                continue;
            if (query.Length > 0 && !SearchText(run, cat, retainedSources, comment, review)
                .Any(text => Contains(text, query)))
                continue;
            result.Add(cat);
        }
        return result;
    }

    public static IReadOnlyList<DuplicateGroup> FindDuplicates(AnalysisRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sources = run.Sources.ToLookup(x => x.CommentId, StringComparer.OrdinalIgnoreCase);
        var comments = run.Comments.ToLookup(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<TextCandidate>();
        var exact = new Dictionary<(string Text, string Stance), TextCandidate>();

        foreach (var cat in run.Categorizations.OrderBy(x => x.SubmissionNumber).ThenBy(x => x.CommentId, StringComparer.Ordinal))
        {
            var text = string.Join("\n", sources[cat.CommentId]
                .Where(x => x.SubmissionNumber == cat.SubmissionNumber || x.SubmissionNumber == 0)
                .SelectMany(x => x.Passages).Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(text))
                text = LegacyBody(run, cat, comments[cat.CommentId].FirstOrDefault());
            var words = Words(text);
            var normalized = string.Join(" ", words);
            if (normalized.Length == 0) continue;
            var stance = NormalizeStance(GetField(cat, "stance", "position"));
            if (exact.TryGetValue((normalized, stance), out var same))
            {
                same.Comments.Add(cat);
                continue;
            }
            var candidate = new TextCandidate(text, normalized, words, stance, cat);
            exact.Add((normalized, stance), candidate);
            candidates.Add(candidate);
        }

        var results = candidates.Where(x => x.Comments.Count > 1).Select(x => new DuplicateGroup
        {
            Kind = "exact",
            Similarity = 1,
            CommentIds = x.Comments.Select(c => c.CommentId).ToList(),
            RepresentativeExcerpt = Excerpt(x.Text),
            Variation = "Identical text after case, punctuation and whitespace normalization. All submissions are retained; shared wording is not evidence of coordination.",
        }).ToList();

        var buckets = new Dictionary<(string Stance, ulong Signature), List<int>>();
        var clusters = new List<TextCluster>();
        foreach (var candidate in candidates)
        {
            if (candidate.Words.Length < NearDuplicateMinimumWords
                || candidate.Normalized.Length < NearDuplicateMinimumCharacters)
                continue;
            candidate.Shingles = Shingles(candidate.Words);
            if (candidate.Shingles.Count < NearDuplicateMinimumUniqueShingles) continue;

            var signatures = candidate.Shingles.Select(StableHash).Distinct().Order().Take(NearDuplicateSignatureSize).ToArray();
            var votes = new Dictionary<int, int>();
            foreach (var signature in signatures)
            {
                if (!buckets.TryGetValue((candidate.Stance, signature), out var bucket)) continue;
                foreach (var index in bucket)
                    votes[index] = votes.GetValueOrDefault(index) + 1;
            }
            var match = -1;
            var bestSimilarity = NearDuplicateSimilarityThreshold;
            foreach (var index in votes.OrderByDescending(x => x.Value).ThenBy(x => x.Key)
                .Take(MaxNearDuplicateCandidates).Select(x => x.Key))
            {
                var similarity = Jaccard(candidate.Shingles, clusters[index].Representative.Shingles);
                if (similarity >= bestSimilarity)
                {
                    match = index;
                    bestSimilarity = similarity;
                }
            }
            if (match >= 0)
            {
                clusters[match].Members.Add(candidate);
                clusters[match].Similarity = Math.Min(clusters[match].Similarity, bestSimilarity);
                continue;
            }

            var newIndex = clusters.Count;
            clusters.Add(new TextCluster(candidate));
            foreach (var signature in signatures)
            {
                var key = (candidate.Stance, signature);
                if (!buckets.TryGetValue(key, out var bucket))
                    buckets.Add(key, bucket = new());
                if (bucket.Count == MaxNearDuplicateCandidates) bucket.RemoveAt(0);
                bucket.Add(newIndex);
            }
        }

        results.AddRange(clusters.Where(x => x.Members.Count > 1).Select(x => new DuplicateGroup
        {
            Kind = "near",
            Similarity = x.Similarity,
            CommentIds = x.Members.SelectMany(m => m.Comments)
                .OrderBy(c => c.SubmissionNumber).ThenBy(c => c.CommentId, StringComparer.Ordinal)
                .Select(c => c.CommentId).ToList(),
            RepresentativeExcerpt = Excerpt(x.Representative.Text),
            Variation = $"Text variations: each member has at least {NearDuplicateSimilarityThreshold:P0} word-shingle Jaccard similarity to the representative. Different classified positions are kept separate. Candidate-based detection may miss matches; no inference of coordination.",
        }));
        return results;
    }

    internal static Dictionary<string, CommentReview> LatestReviews(IReadOnlyList<CommentReview> reviews) =>
        reviews.Select((review, index) => (review, index))
            .GroupBy(x => x.review.CommentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.review.At).ThenBy(x => x.index).Last().review,
                StringComparer.OrdinalIgnoreCase);

    internal static string NormalizeStance(string? stance) => (stance ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "support" or "supports" or "supportive" => "supportive",
        "oppose" or "opposed" or "opposition" or "oppositional" or "opposing" => "opposing",
        var value => value,
    };

    private static bool ClassificationChanged(CategorizationResult cat, CommentReview review) =>
        !GetField(cat, "primary_theme", "primary_topic", "topic").Equals(review.PrimaryTheme, StringComparison.Ordinal)
        || !GetField(cat, "canonical_reason", "primary_theme", "primary_topic", "topic").Equals(review.CanonicalReason, StringComparison.Ordinal)
        || NormalizeStance(GetField(cat, "stance", "position")) != NormalizeStance(review.Stance)
        || !GetField(cat, "comment_summary", "rationale").Equals(review.Summary, StringComparison.Ordinal);

    private static void OverlayReview(CategorizationResult cat, CommentReview review)
    {
        cat.Parsed["primary_theme"] = review.PrimaryTheme;
        cat.Parsed["canonical_reason"] = review.CanonicalReason;
        cat.Parsed["stance"] = NormalizeStance(review.Stance);
        cat.Parsed["comment_summary"] = review.Summary;
        foreach (var alias in new[] { "primary_topic", "topic" })
            if (cat.Parsed.ContainsKey(alias)) cat.Parsed[alias] = review.PrimaryTheme;
        if (cat.Parsed.ContainsKey("position")) cat.Parsed["position"] = NormalizeStance(review.Stance);
        if (cat.Parsed.ContainsKey("rationale")) cat.Parsed["rationale"] = review.Summary;
        cat.Parsed["review_status"] = review.Status;
        cat.Parsed["review_notes"] = review.Notes;
        cat.Parsed["reviewer"] = review.Reviewer;
        cat.Parsed["reviewed_at"] = review.At.ToString("O", CultureInfo.InvariantCulture);
        cat.Parsed["review_id"] = review.Id.ToString("D");
        cat.Parsed["review_approved"] = review.Status == "approved";
        cat.Parsed["review_flagged"] = review.Status == "flagged";
    }

    private static GroupedAnalysis RebuildGroups(AnalysisRun effective, GroupedAnalysis baseline)
    {
        var sourceSubmissions = effective.Sources.SelectMany(s => s.Passages.Select(p => (p.Id, s.SubmissionNumber)))
            .ToLookup(x => x.Id, x => x.SubmissionNumber, StringComparer.Ordinal);
        var groups = effective.Categorizations.GroupBy(c =>
            GetField(c, "canonical_reason", "primary_theme", "primary_topic", "topic").Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var members = g.OrderBy(c => c.SubmissionNumber).ThenBy(c => c.CommentId, StringComparer.Ordinal).ToList();
                var numbers = members.Select(c => c.SubmissionNumber).ToHashSet();
                var evidence = baseline.ThemeGroups.SelectMany(old => old.Evidence.Where(e =>
                    sourceSubmissions.Contains(e.SourceId)
                        ? sourceSubmissions[e.SourceId].Any(numbers.Contains)
                        : old.SubmissionNumbers.Any(numbers.Contains)))
                    .Concat(members.SelectMany(ReadCategorizationEvidence))
                    .Where(e => !string.IsNullOrWhiteSpace(e.Quote))
                    .DistinctBy(e => (e.SourceId, e.Quote))
                    .OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.Quote, StringComparer.Ordinal)
                    .Select(e => new FindingEvidence
                    {
                        SourceId = e.SourceId, Quote = e.Quote,
                        Finding = "Original quoted evidence retained; verify applicability to the reviewed classification.",
                    }).ToList();
                return new ThemeGroup
                {
                    GroupName = g.Key.Length == 0 ? "Unclassified" : g.Key,
                    GroupDescription = $"{ReviewedGeneratedLabel}. Current themes: "
                        + string.Join("; ", members.Select(c => GetField(c, "primary_theme", "primary_topic", "topic"))
                            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
                    Count = members.Count,
                    SubmissionNumbers = members.Select(c => c.SubmissionNumber).ToList(),
                    StanceDistribution = StanceCounts(members),
                    CommonArguments = members.Select(c => (c.SubmissionNumber, Summary: GetField(c, "comment_summary", "rationale")))
                        .Where(x => !string.IsNullOrWhiteSpace(x.Summary))
                        .Select(x => $"Submission {x.SubmissionNumber}: {x.Summary}").ToList(),
                    Evidence = evidence,
                };
            }).ToList();
        var stances = StanceCounts(effective.Categorizations);
        return new GroupedAnalysis
        {
            ThemeGroups = groups,
            OverallSummary = $"{ReviewedGeneratedLabel}. Deterministic counts and current submission summaries, not a new AI or agency conclusion. Original AI patterns and recommendations remain in the original report.\n"
                + string.Join("\n", groups.Select(g => $"{g.GroupName} ({g.Count}): {string.Join(" ", g.CommonArguments)}")),
            OverallSentiment = $"{ReviewedGeneratedLabel}: "
                + string.Join(", ", stances.Select(x => $"{x.Key}: {x.Value}")),
            ParsedSuccessfully = true,
        };
    }

    private static Dictionary<string, int> StanceCounts(IEnumerable<CategorizationResult> cats) =>
        cats.GroupBy(c =>
        {
            var stance = NormalizeStance(GetField(c, "stance", "position"));
            return stance.Length == 0 ? "unknown" : stance;
        }).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());

    private static IEnumerable<FindingEvidence> ReadCategorizationEvidence(CategorizationResult cat)
    {
        if (!cat.Parsed.TryGetValue("evidence", out var value) || value is null) return [];
        try
        {
            var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, WorkspaceJson.Options);
            return json.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<FindingEvidence>>(json, WorkspaceJson.Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string?> SearchText(
        AnalysisRun run, CategorizationResult cat, List<CommentSourceSnapshot> sources,
        CommentResource? comment, CommentReview? review)
    {
        yield return run.DocumentId;
        yield return cat.CommentId;
        yield return cat.SubmissionNumber.ToString(CultureInfo.InvariantCulture);
        yield return cat.RowData;
        yield return cat.RawResponse;
        foreach (var value in cat.Parsed.Values) yield return FieldText(value);
        yield return comment?.Attributes.Comment;
        yield return comment?.Attributes.Title;
        yield return comment?.Attributes.Organization;
        yield return comment?.Attributes.FirstName;
        yield return comment?.Attributes.LastName;
        yield return comment?.Attributes.AgencyId;
        yield return comment?.Attributes.DocumentType;
        yield return review?.Notes;
        yield return review?.Reviewer;
        if (run.AttachmentText.TryGetValue(cat.CommentId, out var attachment))
        {
            yield return attachment.DetailComment;
            yield return attachment.CombinedText;
            foreach (var item in attachment.Attachments)
            {
                yield return item.Title;
                yield return item.Text;
            }
        }
        foreach (var source in sources)
        {
            yield return source.Title;
            yield return source.Organization;
            yield return source.Commenter;
            yield return source.ContentHash;
            yield return source.ExtractionStatus;
            yield return source.PostedAt?.ToString("O", CultureInfo.InvariantCulture);
            yield return source.ModifiedAt?.ToString("O", CultureInfo.InvariantCulture);
            foreach (var warning in source.Warnings) yield return warning;
            foreach (var passage in source.Passages)
            {
                yield return passage.Id;
                yield return passage.Text;
                yield return passage.Title;
                yield return passage.Kind;
                yield return passage.Url;
            }
        }
    }

    private static DateTimeOffset? ReadPostedDate(CategorizationResult cat) =>
        DateTimeOffset.TryParse(GetField(cat, "postedDate", "posted_date", "postedAt"),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) is true;

    private static string FieldText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Array } element => string.Join(" ", element.EnumerateArray().Select(x => FieldText(x))),
        JsonElement { ValueKind: JsonValueKind.Object } element => string.Join(" ", element.EnumerateObject().Select(x => FieldText(x.Value))),
        JsonElement element => element.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => FieldText(JsonSerializer.SerializeToElement(value, WorkspaceJson.Options)),
    };

    private static string LegacyBody(AnalysisRun run, CategorizationResult cat, CommentResource? comment)
    {
        var text = comment?.Attributes.Comment ?? string.Empty;
        if (run.AttachmentText.TryGetValue(cat.CommentId, out var attachment))
            text = string.Join("\n", string.IsNullOrWhiteSpace(text) ? attachment.DetailComment : text, attachment.CombinedText);
        if (!string.IsNullOrWhiteSpace(text)) return text;
        text = GetField(cat, "comment_text", "comment", "body");
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (string.IsNullOrWhiteSpace(cat.RowData)) return string.Empty;
        try
        {
            var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(cat.RowData, WorkspaceJson.Options);
            return row is null ? string.Empty : GetField(new CategorizationResult { Parsed = row }, "comment_text", "comment", "body");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string[] Words(string text) => WordPattern.Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        .Select(m => m.Value).ToArray();

    private static HashSet<string> Shingles(string[] words)
    {
        var shingles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= words.Length - ShingleWordCount; i++)
            shingles.Add(string.Join(" ", words, i, ShingleWordCount));
        return shingles;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if ((double)Math.Min(left.Count, right.Count) / Math.Max(left.Count, right.Count) < NearDuplicateSimilarityThreshold)
            return 0;
        var intersection = left.Count(right.Contains);
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    private static ulong StableHash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text) hash = unchecked((hash ^ c) * 1099511628211UL);
        return hash;
    }

    private static string Excerpt(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private sealed class TextCandidate(string text, string normalized, string[] words, string stance, CategorizationResult comment)
    {
        public string Text { get; } = text;
        public string Normalized { get; } = normalized;
        public string[] Words { get; } = words;
        public string Stance { get; } = stance;
        public List<CategorizationResult> Comments { get; } = [comment];
        public HashSet<string> Shingles { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class TextCluster(TextCandidate representative)
    {
        public TextCandidate Representative { get; } = representative;
        public List<TextCandidate> Members { get; } = [representative];
        public double Similarity { get; set; } = 1;
    }
}
