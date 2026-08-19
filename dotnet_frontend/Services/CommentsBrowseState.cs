namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Per-circuit cache of the last Comments-page fetch and the user's filter/sort choices.
/// Lets a user open a comment detail and return to the Comments page without re-fetching,
/// mirroring how <see cref="AnalysisStore"/> hands off state between pages.
/// </summary>
public sealed class CommentsBrowseState
{
    /// <summary>The request used for the most recent successful fetch (also keeps the form populated).</summary>
    public FetchCommentsRequest? LastRequest { get; set; }

    /// <summary>The most recent successful fetch result, or null if none / it failed.</summary>
    public FetchCommentsResult? LastResult { get; set; }

    public string FilterText { get; set; } = string.Empty;
    public CommentSortColumn SortColumn { get; set; } = CommentSortColumn.None;
    public bool SortDescending { get; set; }

    /// <summary>True when a successful result is cached and can be restored.</summary>
    public bool HasResult => LastResult is { Success: true };

    /// <summary>Stash a successful fetch so it survives navigation.</summary>
    public void Save(FetchCommentsRequest request, FetchCommentsResult result)
    {
        LastRequest = request;
        LastResult = result;
    }

    /// <summary>Drop the cached result and reset filter/sort (e.g. on Reset).</summary>
    public void Clear()
    {
        LastResult = null;
        FilterText = string.Empty;
        SortColumn = CommentSortColumn.None;
        SortDescending = false;
    }
}
