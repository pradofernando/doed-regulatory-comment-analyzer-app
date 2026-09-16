using System.Text.Json;
using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class AnalystEngineTests
{
    [Fact]
    public void LatestReview_RebuildsCountsAndSummaries_WithoutMutatingAnyOriginalEvidence()
    {
        var original = AnalystTestData.Run();
        original.PersistedId = Guid.NewGuid();
        original.Comments = [new CommentResource { Id = "COMMENT-1", Attributes = new() { Comment = "Original source body" } }];
        original.AttachmentText["COMMENT-1"] = new AttachmentExtractionResult
        {
            CommentId = "COMMENT-1", CombinedText = "Original attachment",
            Attachments = [new AttachmentText { Text = "Original attachment", Extracted = true }],
        };
        var originalJson = WorkspaceJson.Write(original);
        var earlier = AnalystTestData.Review(original.Categorizations[0]);
        earlier.At = DateTimeOffset.Parse("2026-01-05T10:00:00Z");
        earlier.Summary = "Superseded summary";
        var latest = WorkspaceJson.Clone(earlier);
        latest.At = earlier.At.AddMinutes(1);
        latest.PrimaryTheme = "Costs";
        latest.CanonicalReason = "Reporting costs";
        latest.Stance = "supportive";
        latest.Summary = "Reviewed cost concern";

        var effective = AnalystEngine.ApplyReviews(original, [latest, earlier]);

        Assert.Equal(originalJson, WorkspaceJson.Write(original));
        Assert.Equal(original.PersistedId, effective.PersistedId);
        Assert.Equal("Reviewed cost concern", AnalystEngine.GetField(effective.Categorizations[0], "comment_summary"));
        Assert.Equal(2, effective.Grouped.ThemeGroups.Count);
        Assert.Equal(2, effective.Grouped.ThemeGroups.Sum(g => g.Count));
        Assert.All(effective.Grouped.ThemeGroups, g => Assert.Equal(1, g.StanceDistribution["supportive"]));
        Assert.Contains(AnalystEngine.ReviewedGeneratedLabel, effective.Grouped.OverallSummary!);
        Assert.Contains("Reviewed cost concern", effective.Grouped.OverallSummary!);
        Assert.DoesNotContain("Superseded summary", effective.Grouped.OverallSummary!);
        Assert.Empty(effective.Grouped.Patterns);
        Assert.Empty(effective.Grouped.Recommendations);
        var regrouped = effective.Grouped.ThemeGroups.Single(g => g.GroupName == "Reporting costs");
        Assert.Equal("Keep access to services", Assert.Single(regrouped.Evidence).Quote);
        Assert.Equal(original.Categorizations[0].RawResponse, effective.Categorizations[0].RawResponse);

        effective.Sources[0].Passages[0].Text = "Changed effective copy";
        effective.Provenance!.AgentVersions["categorization"] = "changed";
        effective.Comments[0].Attributes.Comment = "Changed effective copy";
        effective.AttachmentText["COMMENT-1"].Attachments[0].Text = "Changed effective copy";
        Assert.Equal(originalJson, WorkspaceJson.Write(original));
        Assert.Equal("Original source body", original.Comments[0].Attributes.Comment);
        Assert.Equal("Original attachment", original.AttachmentText["COMMENT-1"].Attachments[0].Text);
    }

    [Fact]
    public void NotesApprovalAndFlagOnly_DoNotScrambleTheOriginalGroups()
    {
        var original = AnalystTestData.Run();
        var review = AnalystTestData.Review(original.Categorizations[0]);
        review.Status = "flagged";
        review.Notes = "Please verify the attachment";

        var effective = AnalystEngine.ApplyReviews(original, [review]);

        Assert.Equal(WorkspaceJson.Write(original.Grouped), WorkspaceJson.Write(effective.Grouped));
        Assert.Equal(original.Grouped.RawResponse, effective.Grouped.RawResponse);
        Assert.Equal(original.Grouped.ParsedSuccessfully, effective.Grouped.ParsedSuccessfully);
        Assert.Equal("flagged", AnalystEngine.GetField(effective.Categorizations[0], "review_status"));
        Assert.Equal(review.Notes, AnalystEngine.GetField(effective.Categorizations[0], "review_notes"));
        Assert.True(Assert.IsType<bool>(effective.Categorizations[0].Parsed["review_flagged"]));
        Assert.False(Assert.IsType<bool>(effective.Categorizations[0].Parsed["review_approved"]));
    }

    [Fact]
    public void RevertingToBaselineClassifications_RestoresBaselineGroups()
    {
        var original = AnalystTestData.Run();
        var changed = AnalystTestData.Review(original.Categorizations[0]);
        changed.At = DateTimeOffset.UtcNow.AddMinutes(-1);
        changed.PrimaryTheme = "Changed";
        changed.Summary = "Changed";
        var reverted = AnalystTestData.Review(original.Categorizations[0]);
        var effective = AnalystEngine.ApplyReviews(original, [changed, reverted]);
        Assert.Equal(WorkspaceJson.Write(original.Grouped), WorkspaceJson.Write(effective.Grouped));
    }

    [Fact]
    public void GetField_SupportsStringsJsonElementsAliasesAndClearedReviewedValues()
    {
        var cat = new CategorizationResult
        {
            Parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                """{"primary_topic":"Access","position":"oppose","rationale":"Original summary","details":["first","second"]}""")!,
        };
        Assert.Equal("Access", AnalystEngine.GetField(cat, "missing", "primary_theme", "primary_topic"));
        Assert.Equal("first second", AnalystEngine.GetField(cat, "details"));
        cat.Parsed["DirectString"] = "plain";
        Assert.Equal("plain", AnalystEngine.GetField(cat, "directstring"));
        var review = AnalystTestData.Review(cat);
        review.PrimaryTheme = "";
        review.CanonicalReason = "";
        review.Summary = "";
        var run = new AnalysisRun { Categorizations = [cat] };
        var effective = AnalystEngine.ApplyReviews(run, [review]);
        Assert.Equal("", AnalystEngine.GetField(effective.Categorizations[0], "primary_theme", "primary_topic", "topic"));
        Assert.Equal("", AnalystEngine.GetField(effective.Categorizations[0], "comment_summary", "rationale"));
    }

    [Fact]
    public void Filter_CombinesAllPassageTextMetadataDateThemeStanceOrganizationAndStatus()
    {
        var run = AnalystTestData.Run();
        var review = AnalystTestData.Review(run.Categorizations[0]);
        review.PrimaryTheme = "Implementation";
        review.Stance = "supportive";
        review.Status = "flagged";
        var state = new RunReviewState { Revisions = [review] };
        var filter = new CommentSearchFilter
        {
            Query = "attachment FOOTNOTE",
            Organization = "example",
            Theme = "Implementation",
            Stance = "supportive",
            ReviewStatus = "flagged",
            PostedFrom = new DateTime(2026, 1, 2),
            PostedTo = new DateTime(2026, 1, 2),
        };
        var match = Assert.Single(AnalystEngine.Filter(run, state, filter));
        Assert.Equal("COMMENT-1", match.CommentId);
        Assert.Equal("Implementation", AnalystEngine.GetField(match, "primary_theme"));
        filter.Query = "Detailed evidence.pdf";
        Assert.Single(AnalystEngine.Filter(run, state, filter));
        filter.Query = "synthetic-content-hash";
        Assert.Single(AnalystEngine.Filter(run, state, filter));
        filter.Organization = "Other School";
        Assert.Empty(AnalystEngine.Filter(run, state, filter));
        Assert.Equal("Access", AnalystEngine.GetField(run.Categorizations[0], "primary_theme"));
    }

    [Fact]
    public void Filter_LegacyMissingSourcesAreUnknown_NotFabricatedNoneOrEmptyComments()
    {
        var run = AnalystTestData.Run();
        run.Sources.Clear();
        var state = new RunReviewState();
        Assert.Equal(2, AnalystEngine.Filter(run, state, new()).Count);
        Assert.Equal("COMMENT-1", Assert.Single(AnalystEngine.Filter(run, state,
            new CommentSearchFilter { Query = "original full body" })).CommentId);
        Assert.Empty(AnalystEngine.Filter(run, state, new CommentSearchFilter { Organization = "none" }));
        Assert.Empty(AnalystEngine.Filter(run, state, new CommentSearchFilter { Query = "none" }));
        Assert.Empty(AnalystEngine.Filter(run, state, new CommentSearchFilter { PostedFrom = new DateTime(2026, 1, 1) }));
        run.Comments = [new CommentResource
        {
            Id = "COMMENT-2", Attributes = new()
            {
                Comment = "Retained legacy body", Organization = "Legacy organization",
                PostedDate = DateTimeOffset.Parse("2026-01-02T11:00:00Z"),
            },
        }];
        Assert.Equal("COMMENT-2", Assert.Single(AnalystEngine.Filter(run, state, new CommentSearchFilter
        {
            Query = "retained legacy", Organization = "Legacy", PostedFrom = new DateTime(2026, 1, 2),
        })).CommentId);
    }

    [Fact]
    public void Filter_RejectsReversedDateRange()
    {
        Assert.Throws<ArgumentException>(() => AnalystEngine.Filter(AnalystTestData.Run(), new(), new()
        {
            PostedFrom = new DateTime(2026, 1, 3), PostedTo = new DateTime(2026, 1, 2),
        }));
    }

    [Fact]
    public void Duplicates_FindExactAndSubstantiveNearText_WithoutCombiningOppositePositions()
    {
        var template = "I support this proposal. " + string.Join(" ", Enumerable.Range(1, 160).Select(i => $"policyword{i}"));
        var run = DuplicateRun(
            (template, "supportive"),
            (template.ToUpperInvariant().Replace(" ", " \n") + "!", "supportive"),
            (template + " One clarification.", "supportive"),
            (template.Replace("I support", "I oppose") + " One clarification.", "opposing"));
        var before = WorkspaceJson.Write(run);
        var groups = AnalystEngine.FindDuplicates(run);
        var exact = Assert.Single(groups, g => g.Kind == "exact");
        Assert.Equal(new[] { "COMMENT-1", "COMMENT-2" }, exact.CommentIds);
        var near = Assert.Single(groups, g => g.Kind == "near");
        Assert.Equal(new[] { "COMMENT-1", "COMMENT-2", "COMMENT-3" }, near.CommentIds);
        Assert.InRange(near.Similarity, AnalystEngine.NearDuplicateSimilarityThreshold, 1);
        Assert.Contains("representative", near.Variation);
        Assert.Equal(before, WorkspaceJson.Write(run));
        Assert.Equal(4, run.Categorizations.Count);
        Assert.DoesNotContain(groups.SelectMany(g => g.CommentIds), id => id == "COMMENT-4");
    }

    [Fact]
    public void Duplicates_RetainEveryExactSubmissionId_AndAvoidShortNearMatches()
    {
        var many = DuplicateRun(Enumerable.Repeat(("Identical short wording", "neutral"), 257).ToArray());
        var exact = Assert.Single(AnalystEngine.FindDuplicates(many));
        Assert.Equal(257, exact.CommentIds.Count);
        Assert.Contains("COMMENT-257", exact.CommentIds);
        var shortRun = DuplicateRun(("Thank you for this proposal.", "neutral"), ("Thank you for the proposal.", "neutral"));
        Assert.Empty(AnalystEngine.FindDuplicates(shortRun));
        Assert.True(AnalystEngine.MaxNearDuplicateCandidates < many.Categorizations.Count);
    }

    private static AnalysisRun DuplicateRun(params (string Text, string Stance)[] submissions) => new()
    {
        TotalComments = submissions.Length,
        Categorizations = submissions.Select((s, i) => new CategorizationResult
        {
            SubmissionNumber = i + 1, CommentId = $"COMMENT-{i + 1}",
            Parsed = new() { ["stance"] = s.Stance },
        }).ToList(),
        Sources = submissions.Select((s, i) => new CommentSourceSnapshot
        {
            SubmissionNumber = i + 1, CommentId = $"COMMENT-{i + 1}",
            Passages = [new SourcePassage { Id = $"s{i + 1}-p1", Text = s.Text }],
        }).ToList(),
    };
}
