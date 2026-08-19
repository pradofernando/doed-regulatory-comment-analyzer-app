using DoedRegulatoryComments.Web.Services;

namespace DoedRegulatoryComments.Web.Tests;

/// <summary>Shared helpers for building comment test data.</summary>
internal static class TestData
{
    public static CommentResource Comment(
        string id,
        string? first = null,
        string? last = null,
        string? org = null,
        string? title = null,
        string? comment = null,
        DateTimeOffset? posted = null)
        => new()
        {
            Id = id,
            Attributes = new CommentAttributes
            {
                FirstName = first,
                LastName = last,
                Organization = org,
                Title = title,
                Comment = comment,
                PostedDate = posted,
            },
        };
}
