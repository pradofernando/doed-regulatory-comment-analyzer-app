using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using DocOp = DocumentFormat.OpenXml.Wordprocessing;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Produces Word (.docx) and Excel (.xlsx) downloads of a completed <see cref="AnalysisRun"/>.
/// </summary>
public static class CollectiveAnalysisExporter
{
    public static byte[] BuildWord(AnalysisRun run, RunReviewState? reviewState = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocOp.Document();
            var body = main.Document.AppendChild(new DocOp.Body());

            AddHeading(body, "Regulatory Comments — Collective Analysis", level: 1);
            AddPara(body, $"Document: {run.DocumentId}");
            AddPara(body, $"Submissions included: {run.TotalComments}");
            AddPara(body, $"Started:  {run.StartedAt:yyyy-MM-dd HH:mm} UTC");
            AddPara(body, $"Finished: {run.CompletedAt:yyyy-MM-dd HH:mm} UTC");
            AddPara(body, $"Overall sentiment: {run.Grouped.OverallSentiment ?? "(none)"}");
            AddHeading(body, "Coverage, provenance, and review status", level: 2);
            AddPara(body, AnalystReportExporter.ContextText(run, reviewState));

            if (!string.IsNullOrWhiteSpace(run.Grouped.OverallSummary))
            {
                AddHeading(body, "Overall summary", level: 2);
                AddPara(body, run.Grouped.OverallSummary);
            }

            if (run.Grouped.ThemeGroups.Count > 0)
            {
                AddHeading(body, "Theme groups", level: 2);
                foreach (var t in run.Grouped.ThemeGroups.OrderByDescending(x => x.Count))
                {
                    AddHeading(body, $"{t.GroupName} ({t.Count})", level: 3);
                    if (!string.IsNullOrWhiteSpace(t.GroupDescription)) AddPara(body, t.GroupDescription);
                    if (t.StanceDistribution.Count > 0)
                        AddPara(body, "Stance: " + string.Join(" | ", t.StanceDistribution.Select(kv => $"{kv.Key}: {kv.Value}")));
                    foreach (var arg in t.CommonArguments) AddBullet(body, arg);
                }
            }

            if (run.Grouped.Patterns.Count > 0)
            {
                AddHeading(body, "Key patterns", level: 2);
                foreach (var p in run.Grouped.Patterns) AddBullet(body, p);
            }

            if (run.Grouped.Recommendations.Count > 0)
            {
                AddHeading(body, "Recommendations", level: 2);
                foreach (var r in run.Grouped.Recommendations) AddBullet(body, r);
            }

            AddHeading(body, "Per-comment categorizations", level: 2);
            var commentLookup = run.Comments.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var cat in run.Categorizations)
            {
                AddHeading(body, $"#{cat.SubmissionNumber} — {cat.CommentId}", level: 3);
                if (commentLookup.TryGetValue(cat.CommentId, out var c))
                {
                    var posted = c.Attributes.PostedDate?.ToString("yyyy-MM-dd") ?? "";
                    var who = string.Join(' ', new[] { c.Attributes.FirstName, c.Attributes.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    AddPara(body, $"Posted: {posted} · {(string.IsNullOrWhiteSpace(who) ? "(anonymous)" : who)} · {c.Attributes.Organization ?? ""}");
                    if (!string.IsNullOrWhiteSpace(c.Attributes.Title)) AddPara(body, $"Title: {c.Attributes.Title}");
                }
                AddPara(body, $"Text source: {cat.TextSource} · attachments extracted: {cat.AttachmentsExtracted}");
                AddPara(body, $"Current theme: {AnalystEngine.GetField(cat, "primary_theme", "primary_topic", "topic")}");
                AddPara(body, $"Current primary reason: {AnalystEngine.GetField(cat, "canonical_reason")}");
                AddPara(body, $"Current stance: {AnalystEngine.GetField(cat, "stance", "position")}");
                AddPara(body, $"Current summary: {AnalystEngine.GetField(cat, "comment_summary", "rationale")}");
                if (!string.IsNullOrWhiteSpace(cat.RawResponse))
                {
                    AddPara(body, "Original AI response (preserved, not a replacement for the current reviewed values):");
                    AddPara(body, cat.RawResponse);
                }
            }
            AddHeading(body, "Verified source quotations", level: 2);
            AddPara(body, AnalystReportExporter.EvidenceText(run));
            if (reviewState is not null)
            {
                AddHeading(body, "Issue response matrix - draft working material", level: 2);
                foreach (var issue in reviewState.Issues)
                {
                    AddHeading(body, issue.Concern, level: 3);
                    AddPara(body, $"Rule section: {(string.IsNullOrWhiteSpace(issue.RuleSection) ? "Not identified" : issue.RuleSection)}");
                    AddPara(body, $"Supporting submissions: {string.Join(", ", issue.SubmissionNumbers)}");
                    AddPara(body, $"Requested change: {issue.RequestedChange}");
                    AddPara(body, $"Draft response (human review required): {issue.DraftResponse}");
                    AddPara(body, $"Review status: {issue.ReviewStatus}; reviewer label: {issue.Reviewer}");
                    AddPara(body, issue.Notes);
                }
                AddHeading(body, "Human revision history", level: 2);
                foreach (var review in reviewState.Revisions)
                {
                    AddPara(body, $"{review.At:u} - {review.CommentId} - {review.Reviewer} - {review.Status}");
                    AddPara(body, $"{review.PrimaryTheme}; {review.CanonicalReason}; {review.Stance}");
                    AddPara(body, review.Summary);
                    AddPara(body, review.Notes);
                }
            }

            main.Document.Save();
        }
        return ms.ToArray();
    }

    public static byte[] BuildExcel(AnalysisRun run, RunReviewState? reviewState = null)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var sheets = wbPart.Workbook.AppendChild(new Sheets());
            uint sheetId = 1;

            AddSheet(wbPart, sheets, ref sheetId, "Summary", new[]
            {
                new[] { "Document", run.DocumentId },
                new[] { "Submissions included", run.TotalComments.ToString() },
                new[] { "Started (UTC)", run.StartedAt.ToString("yyyy-MM-dd HH:mm") },
                new[] { "Finished (UTC)", run.CompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "" },
                new[] { "Overall sentiment", run.Grouped.OverallSentiment ?? "" },
                new[] { "Overall summary", run.Grouped.OverallSummary ?? "" },
            });

            var themeRows = new List<string[]>
            {
                new[] { "Group", "Count", "Description", "Submissions", "Stance distribution", "Common arguments" }
            };
            foreach (var t in run.Grouped.ThemeGroups.OrderByDescending(x => x.Count))
            {
                themeRows.Add(new[]
                {
                    t.GroupName,
                    t.Count.ToString(),
                    t.GroupDescription ?? "",
                    string.Join(", ", t.SubmissionNumbers),
                    string.Join(" | ", t.StanceDistribution.Select(kv => $"{kv.Key}:{kv.Value}")),
                    string.Join("\n", t.CommonArguments),
                });
            }
            AddSheet(wbPart, sheets, ref sheetId, "Theme groups", themeRows);

            var catRows = new List<string[]>
            {
                new[] { "#", "Comment ID", "Text source", "Attachments extracted", "Current theme", "Current primary reason", "Current stance", "Current summary", "Original AI response" }
            };
            foreach (var c in run.Categorizations)
            {
                catRows.Add(new[]
                {
                    c.SubmissionNumber.ToString(),
                    c.CommentId,
                    c.TextSource,
                    c.AttachmentsExtracted.ToString(),
                    AnalystEngine.GetField(c, "primary_theme", "primary_topic", "topic"),
                    AnalystEngine.GetField(c, "canonical_reason"),
                    AnalystEngine.GetField(c, "stance", "position"),
                    AnalystEngine.GetField(c, "comment_summary", "rationale"),
                    c.RawResponse,
                });
            }
            AddSheet(wbPart, sheets, ref sheetId, "Categorizations", catRows);

            var prRows = new List<string[]> { new[] { "Type", "Item" } };
            foreach (var p in run.Grouped.Patterns) prRows.Add(new[] { "Pattern", p });
            foreach (var r in run.Grouped.Recommendations) prRows.Add(new[] { "Recommendation", r });
            AddSheet(wbPart, sheets, ref sheetId, "Patterns & recommendations", prRows);
            AddSheet(wbPart, sheets, ref sheetId, "Coverage and provenance",
                AnalystReportExporter.ContextText(run, reviewState).Split('\n').Select(line => new[] { line }));

            var sourceRows = new List<string[]>
            {
                new[] { "Source ID", "Comment ID", "Kind", "Attachment / title", "Page", "URL", "Captured text", "Capture status", "Warnings", "Content SHA-256" },
            };
            foreach (var source in run.Sources)
            {
                foreach (var passage in source.Passages)
                    sourceRows.Add([passage.Id, source.CommentId, passage.Kind, passage.Title,
                        passage.PageNumber?.ToString() ?? "", passage.Url ?? "", passage.Text,
                        source.ExtractionStatus, string.Join("; ", source.Warnings), source.ContentHash]);
            }
            AddSheet(wbPart, sheets, ref sheetId, "Captured sources", sourceRows);
            var evidenceRows = new List<string[]> { new[] { "Source ID", "Comment ID", "Page", "Verified quote", "URL" } };
            evidenceRows.AddRange(SourceEvidence.GetEvidence(run).Select(match => new[]
            {
                match.Passage.Id, match.Source.CommentId, AnalystReportExporter.PageLabel(match.Passage), match.Quote, match.Passage.Url ?? "",
            }));
            AddSheet(wbPart, sheets, ref sheetId, "Verified quotations", evidenceRows);
            if (reviewState is not null)
            {
                AddSheet(wbPart, sheets, ref sheetId, "Issue response matrix", AnalystReportExporter.IssueRows(reviewState.Issues));
                var revisionRows = new List<string[]>
                {
                    new[] { "UTC", "Comment ID", "Reviewer label", "Theme", "Primary reason", "Stance", "Summary", "Status", "Notes" },
                };
                revisionRows.AddRange(reviewState.Revisions.Select(review => new[]
                {
                    review.At.ToString("O"), review.CommentId, review.Reviewer, review.PrimaryTheme,
                    review.CanonicalReason, review.Stance, review.Summary, review.Status, review.Notes,
                }));
                AddSheet(wbPart, sheets, ref sheetId, "Human revisions", revisionRows);
            }

            wbPart.Workbook.Save();
        }
        return ms.ToArray();
    }

    // ─── Word helpers ───────────────────────────────────────────────────────

    private static void AddHeading(DocOp.Body body, string text, int level)
    {
        var p = new DocOp.Paragraph();
        var pPr = new DocOp.ParagraphProperties(new DocOp.ParagraphStyleId { Val = $"Heading{level}" });
        p.AppendChild(pPr);
        p.AppendChild(new DocOp.Run(new DocOp.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        body.AppendChild(p);
    }

    private static void AddPara(DocOp.Body body, string text)
    {
        if (string.IsNullOrEmpty(text)) { body.AppendChild(new DocOp.Paragraph()); return; }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var p = new DocOp.Paragraph();
            p.AppendChild(new DocOp.Run(new DocOp.Text(line) { Space = SpaceProcessingModeValues.Preserve }));
            body.AppendChild(p);
        }
    }

    private static void AddBullet(DocOp.Body body, string text)
    {
        var p = new DocOp.Paragraph();
        var pPr = new DocOp.ParagraphProperties(new DocOp.ParagraphStyleId { Val = "ListParagraph" });
        p.AppendChild(pPr);
        p.AppendChild(new DocOp.Run(new DocOp.Text("• " + text) { Space = SpaceProcessingModeValues.Preserve }));
        body.AppendChild(p);
    }

    // ─── Excel helpers ──────────────────────────────────────────────────────

    private static void AddSheet(WorkbookPart wbPart, Sheets sheets, ref uint sheetId, string name, IEnumerable<string[]> rows)
    {
        var wsPart = wbPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        wsPart.Worksheet = new Worksheet(sheetData);

        uint rowIndex = 1;
        foreach (var r in rows)
        {
            var row = new Row { RowIndex = rowIndex };
            for (var i = 0; i < r.Length; i++)
            {
                var cell = new Cell
                {
                    CellReference = $"{ColumnName(i + 1)}{rowIndex}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(r[i] ?? string.Empty)),
                };
                row.AppendChild(cell);
            }
            sheetData.AppendChild(row);
            rowIndex++;
        }
        wsPart.Worksheet.Save();

        sheets.AppendChild(new Sheet
        {
            Id = wbPart.GetIdOfPart(wsPart),
            SheetId = sheetId,
            Name = name,
        });
        sheetId++;
    }

    private static string ColumnName(int col)
    {
        var name = string.Empty;
        while (col > 0)
        {
            var rem = (col - 1) % 26;
            name = (char)('A' + rem) + name;
            col = (col - 1) / 26;
        }
        return name;
    }
}
