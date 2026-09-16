using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DoedRegulatoryComments.Web.Services;

public sealed class ReviewService(IAnalysisRepository analyses, IWorkspaceRepository workspace)
{
    public const string ReviewKind = "reviews";
    public const string DraftLabel = "Draft for human review";
    public const int MaxReviewerLength = 120;
    public const int MaxSummaryLength = 20_000;
    public const int MaxNotesLength = 20_000;
    public const int MaxDraftLength = 20_000;

    private static readonly HashSet<string> Stances = ["supportive", "opposing", "neutral", "mixed", "procedural"];
    private static readonly HashSet<string> ReviewStatuses = ["unreviewed", "approved", "flagged"];
    private static readonly HashSet<string> IssueStatuses = ["draft", "in-review", "approved", "flagged"];
    private static readonly Regex SourceCitation = new(
        @"\[(?:source:)?(?<id>s\d+-p\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex LegalCitation = new(
        @"\b\d+\s+(?:U\.?\s*S\.?\s*C\.?|C\.?\s*F\.?\s*R\.?)\s*(?:§{1,2}\s*)?\d+(?:\.\d+)*(?:\([a-zA-Z0-9]+\))*|§{1,2}\s*\d+(?:\.\d+)*(?:\([a-zA-Z0-9]+\))*|\b\d+\s+(?:U\.?\s*S\.?|F\.\s*(?:2d|3d|4th)|S\.\s*Ct\.|Fed\.\s*Reg\.)\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public async Task<AnalysisWorkspace> OpenAsync(Guid runId, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A saved analysis run ID is required.", nameof(runId));
        var original = await analyses.LoadRunAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Analysis run {runId:D} was not found.");
        original.PersistedId = runId;
        var item = await workspace.GetAsync(ReviewKind, runId.ToString("D"), ct).ConfigureAwait(false);
        var state = item is null ? SeedState(runId, original) : WorkspaceJson.Read<RunReviewState>(item);
        state.Revisions ??= new();
        state.Issues ??= new();
        state.IssueHistory ??= new();
        return new AnalysisWorkspace
        {
            Original = original, Effective = AnalystEngine.ApplyReviews(original, state.Revisions),
            State = state, Version = item?.Version,
        };
    }

    public async Task<AnalysisWorkspace> SaveReviewAsync(
        Guid runId, CommentReview review, string? expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        var current = await OpenAsync(runId, ct).ConfigureAwait(false);
        CheckVersion(current.Version, expectedVersion);
        var commentId = Text(review.CommentId, "Comment ID", 128, required: true);
        var matches = current.Original.Categorizations
            .Where(x => x.CommentId.Equals(commentId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new ArgumentException("Select a unique submission belonging to this analysis run.", nameof(review));
        var cat = matches[0];
        var stance = AnalystEngine.NormalizeStance(review.Stance);
        if (!Stances.Contains(stance)) throw new ArgumentException("Choose a supported stance.", nameof(review));
        var status = Text(review.Status, "Review status", 32, required: true).ToLowerInvariant();
        if (!ReviewStatuses.Contains(status)) throw new ArgumentException("Choose a supported review status.", nameof(review));

        var savedReview = new CommentReview
        {
            Id = Guid.NewGuid(),
            At = DateTimeOffset.UtcNow,
            CommentId = cat.CommentId,
            Reviewer = ReviewerLabel(review.Reviewer),
            PrimaryTheme = Text(review.PrimaryTheme, "Theme", 200),
            CanonicalReason = Text(review.CanonicalReason, "Primary reason", 500),
            Stance = stance,
            Summary = Text(review.Summary, "Summary", MaxSummaryLength),
            Status = status,
            Notes = Text(review.Notes, "Notes", MaxNotesLength),
        };
        var effectiveCat = current.Effective.Categorizations.Single(x => x.CommentId == cat.CommentId);
        if (savedReview.Summary != AnalystEngine.GetField(effectiveCat, "comment_summary", "rationale"))
            ValidateCitations(current.Original, [cat.SubmissionNumber], savedReview.Summary);
        var state = WorkspaceJson.Clone(current.State);
        state.Revisions.Add(savedReview);
        if (savedReview.PrimaryTheme != AnalystEngine.GetField(effectiveCat, "primary_theme", "primary_topic")
            || savedReview.CanonicalReason != AnalystEngine.GetField(effectiveCat, "canonical_reason")
            || savedReview.Stance != AnalystEngine.NormalizeStance(AnalystEngine.GetField(effectiveCat, "stance"))
            || savedReview.Summary != AnalystEngine.GetField(effectiveCat, "comment_summary", "rationale"))
        {
            foreach (var issue in state.Issues.Where(i => i.SubmissionNumbers.Contains(cat.SubmissionNumber) && i.ReviewStatus == "approved"))
            {
                state.IssueHistory.Add(WorkspaceJson.Clone(issue));
                issue.ReviewStatus = "in-review";
                issue.UpdatedAt = savedReview.At;
                issue.Notes += "\nSupporting classification changed; approval needs renewed review.";
                state.IssueHistory.Add(WorkspaceJson.Clone(issue));
            }
        }
        return await SaveStateAsync(runId, current.Original, state, expectedVersion, ct).ConfigureAwait(false);
    }

    public async Task<AnalysisWorkspace> SaveIssueAsync(
        Guid runId, IssueResponse issue, string? expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var current = await OpenAsync(runId, ct).ConfigureAwait(false);
        CheckVersion(current.Version, expectedVersion);
        var status = Text(issue.ReviewStatus, "Issue status", 32, required: true).ToLowerInvariant();
        if (!IssueStatuses.Contains(status)) throw new ArgumentException("Choose a supported issue review status.", nameof(issue));
        var numbers = (issue.SubmissionNumbers ?? []).Distinct().Order().ToList();
        var knownNumbers = current.Original.Categorizations.Select(x => x.SubmissionNumber).ToHashSet();
        if (numbers.Any(x => x < 1 || !knownNumbers.Contains(x)))
            throw new ArgumentException("Issue evidence must reference submissions belonging to this analysis run.", nameof(issue));

        var section = Text(issue.RuleSection, "Rule section", 500);
        var draft = Text(issue.DraftResponse, "Draft response", MaxDraftLength);
        var concern = Text(issue.Concern, "Concern", 4_000, required: true);
        var requested = Text(issue.RequestedChange, "Requested change", 8_000);
        if (section.Length > 0 && !SourceTexts(current.Original, numbers)
            .Any(text => CitationKey(text).Contains(CitationKey(section), StringComparison.Ordinal)))
            throw new ArgumentException("A rule section must be supported by the selected saved source text. Leave unknown sections blank.", nameof(issue));
        ValidateCitations(current.Original, numbers, section, concern, requested, draft);
        if (!draft.StartsWith(DraftLabel, StringComparison.OrdinalIgnoreCase))
            draft = draft.Length == 0 ? DraftLabel : $"{DraftLabel}\n\n{draft}";
        draft = Text(draft, "Labeled draft response", MaxDraftLength);
        var savedIssue = new IssueResponse
        {
            Id = issue.Id == Guid.Empty ? Guid.NewGuid() : issue.Id,
            RuleSection = section,
            Concern = concern,
            SubmissionNumbers = numbers,
            RequestedChange = requested,
            DraftResponse = draft,
            ReviewStatus = status,
            Reviewer = ReviewerLabel(issue.Reviewer),
            Notes = Text(issue.Notes, "Issue notes", MaxNotesLength),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var state = WorkspaceJson.Clone(current.State);
        var existing = state.Issues.SingleOrDefault(x => x.Id == savedIssue.Id);
        if (existing is not null)
        {
            // The deterministic baseline is not a saved revision until its first edit.
            if (!state.IssueHistory.Any(x => x.Id == existing.Id))
                state.IssueHistory.Add(WorkspaceJson.Clone(existing));
            state.Issues[state.Issues.IndexOf(existing)] = savedIssue;
        }
        else
        {
            state.Issues.Add(savedIssue);
        }
        state.IssueHistory.Add(WorkspaceJson.Clone(savedIssue));
        return await SaveStateAsync(runId, current.Original, state, expectedVersion, ct).ConfigureAwait(false);
    }

    private async Task<AnalysisWorkspace> SaveStateAsync(
        Guid runId, AnalysisRun original, RunReviewState state, string? expectedVersion, CancellationToken ct)
    {
        var saved = await workspace.SaveAsync(
            ReviewKind, runId.ToString("D"), WorkspaceJson.Write(state), expectedVersion, ct).ConfigureAwait(false);
        return new AnalysisWorkspace
        {
            Original = original, Effective = AnalystEngine.ApplyReviews(original, state.Revisions),
            State = state, Version = saved.Version,
        };
    }

    private static RunReviewState SeedState(Guid runId, AnalysisRun original)
    {
        var knownNumbers = original.Categorizations.Select(x => x.SubmissionNumber).ToHashSet();
        return new RunReviewState
        {
            Issues = original.Grouped.ThemeGroups.Select((group, index) => new IssueResponse
            {
                Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{runId:D}:issue:{index}:{group.GroupName}")).AsSpan(0, 16)),
                Concern = group.GroupName,
                SubmissionNumbers = group.SubmissionNumbers.Where(knownNumbers.Contains).Distinct().Order().ToList(),
                RuleSection = string.Empty,
                RequestedChange = string.Empty,
                DraftResponse = DraftLabel,
                ReviewStatus = "draft",
                UpdatedAt = original.CompletedAt ?? original.StartedAt,
            }).ToList(),
        };
    }

    private static void CheckVersion(string? actual, string? expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new WorkspaceConflictException();
        if (expected is not null) WorkspaceStorage.ValidateVersion(expected);
    }

    private static string Text(string? value, string field, int limit, bool required = false)
    {
        var result = value?.Trim() ?? string.Empty;
        if (required && result.Length == 0) throw new ArgumentException($"{field} is required.");
        if (result.Length > limit) throw new ArgumentException($"{field} must be {limit:N0} characters or fewer.");
        return result;
    }

    private static string ReviewerLabel(string? label)
    {
        var result = Text(label, "Human reviewer label", MaxReviewerLength, required: true);
        if (result.Any(char.IsControl)) throw new ArgumentException("Use a single-line human reviewer label.");
        return result;
    }

    private static IEnumerable<string> SourceTexts(AnalysisRun run, IReadOnlyCollection<int> numbers)
    {
        var membership = run.Categorizations
            .Where(c => numbers.Contains(c.SubmissionNumber)).ToLookup(c => c.SubmissionNumber, c => c.CommentId);
        return run.Sources
            .Where(s => membership[s.SubmissionNumber].Contains(s.CommentId, StringComparer.OrdinalIgnoreCase))
            .SelectMany(s => s.Passages).Select(p => p.Text);
    }

    private static void ValidateCitations(AnalysisRun run, IReadOnlyCollection<int> numbers, params string[] values)
    {
        var membership = run.Categorizations
            .Where(c => numbers.Contains(c.SubmissionNumber)).ToLookup(c => c.SubmissionNumber, c => c.CommentId);
        var passages = run.Sources
            .Where(s => membership[s.SubmissionNumber].Contains(s.CommentId, StringComparer.OrdinalIgnoreCase))
            .SelectMany(s => s.Passages).ToList();
        var ids = passages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var sourceTexts = passages.Select(p => CitationKey(p.Text)).ToList();
        foreach (var value in values)
        {
            foreach (Match citation in SourceCitation.Matches(value))
            {
                if (!ids.Contains(citation.Groups["id"].Value))
                    throw new ArgumentException("A citation refers to a passage not retained for the selected submission(s).");
            }
            foreach (Match citation in LegalCitation.Matches(value))
            {
                if (!sourceTexts.Any(source => source.Contains(CitationKey(citation.Value), StringComparison.Ordinal)))
                    throw new ArgumentException("A legal citation is not supported by the selected saved source text. Leave it out pending human verification.");
            }
        }
    }

    private static string CitationKey(string text) =>
        Whitespace.Replace(text.Normalize(NormalizationForm.FormKC), " ").Trim().ToUpperInvariant();
}
