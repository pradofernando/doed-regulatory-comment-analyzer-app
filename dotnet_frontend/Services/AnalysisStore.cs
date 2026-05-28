namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Per-circuit holder for the comments queued for analysis, the user's manual selection,
/// and the most-recent run. Lets the Comments page hand off its result to the Analysis page
/// without re-fetching.
/// </summary>
public sealed class AnalysisStore
{
    public string? DocumentId { get; set; }
    public IReadOnlyList<CommentResource> Comments { get; set; } = Array.Empty<CommentResource>();
    public AnalysisRun? LastRun { get; set; }
    public bool IsRunning { get; set; }
    public AnalysisProgress? LastProgress { get; set; }

    /// <summary>
    /// DB primary key of the run currently held in <see cref="LastRun"/>, if it has been persisted.
    /// Set after a successful save (so new turns can be appended) and after loading a run
    /// from the Library page.
    /// </summary>
    public Guid? LoadedRunId { get; set; }

    public bool HasInput => Comments.Count > 0;

    /// <summary>
    /// Manual per-comment selection. When non-empty, RunAnalysis uses only these;
    /// otherwise it falls back to all fetched comments.
    /// Key is comment ID (case-insensitive). Cleared whenever a new fetch is staged.
    /// </summary>
    public Dictionary<string, CommentResource> SelectedComments { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsSelected(string commentId) => SelectedComments.ContainsKey(commentId);

    public void ClearSelection() => SelectedComments.Clear();

    public void ToggleSelection(CommentResource c)
    {
        if (SelectedComments.ContainsKey(c.Id))
            SelectedComments.Remove(c.Id);
        else
            SelectedComments[c.Id] = c;
    }

    public void SelectAll(IEnumerable<CommentResource> comments)
    {
        foreach (var c in comments)
            SelectedComments[c.Id] = c;
    }

    public int SelectedCount => SelectedComments.Count;
}
