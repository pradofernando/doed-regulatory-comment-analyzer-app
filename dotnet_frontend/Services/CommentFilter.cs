namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Columns the Comments table can be sorted by.
/// </summary>
public enum CommentSortColumn
{
    None,
    Posted,
    Commenter,
    Organization,
    Title,
}

/// <summary>
/// Pure, side-effect-free filtering and sorting for the Comments table.
/// Kept separate from the Razor page so it can be unit tested.
/// </summary>
public static class CommentFilter
{
    /// <summary>
    /// Returns the comments matching <paramref name="filterText"/> (case-insensitive substring
    /// match against comment ID, commenter name, organization and title), ordered by
    /// <paramref name="sortColumn"/>.
    /// </summary>
    public static IReadOnlyList<CommentResource> Apply(
        IReadOnlyList<CommentResource>? comments,
        string? filterText,
        CommentSortColumn sortColumn,
        bool sortDescending)
    {
        IEnumerable<CommentResource> query = comments ?? Array.Empty<CommentResource>();

        if (!string.IsNullOrWhiteSpace(filterText))
        {
            var term = filterText.Trim();
            query = query.Where(c => Matches(c, term));
        }

        query = sortColumn switch
        {
            CommentSortColumn.Posted => sortDescending
                ? query.OrderByDescending(c => c.Attributes.PostedDate ?? DateTimeOffset.MinValue)
                : query.OrderBy(c => c.Attributes.PostedDate ?? DateTimeOffset.MinValue),
            CommentSortColumn.Commenter => OrderText(query, c => CommenterName(c.Attributes), sortDescending),
            CommentSortColumn.Organization => OrderText(query, c => c.Attributes.Organization ?? string.Empty, sortDescending),
            CommentSortColumn.Title => OrderText(query, c => c.Attributes.Title ?? string.Empty, sortDescending),
            _ => query,
        };

        return query.ToList();
    }

    /// <summary>
    /// True when the comment matches the (already-trimmed) search term in any searchable field.
    /// </summary>
    public static bool Matches(CommentResource comment, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return true;
        var a = comment.Attributes;
        return Contains(comment.Id, term)
            || Contains(CommenterName(a), term)
            || Contains(a.Organization, term)
            || Contains(a.Title, term);
    }

    /// <summary>Combined first + last name, trimmed.</summary>
    public static string CommenterName(CommentAttributes a) =>
        $"{a.FirstName} {a.LastName}".Trim();

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<CommentResource> OrderText(
        IEnumerable<CommentResource> query,
        Func<CommentResource, string> key,
        bool descending) =>
        descending
            ? query.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : query.OrderBy(key, StringComparer.OrdinalIgnoreCase);
}
