using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class CommentsBrowseStateTests
{
    private static FetchCommentsResult SuccessResult() => new()
    {
        Success = true,
        Comments = { TestData.Comment("ED-1") },
        RequestedUrl = "https://example/comments",
    };

    [Fact]
    public void HasResult_FalseByDefault()
    {
        var state = new CommentsBrowseState();
        Assert.False(state.HasResult);
    }

    [Fact]
    public void Save_StoresRequestAndResult_AndHasResultBecomesTrue()
    {
        var state = new CommentsBrowseState();
        var request = new FetchCommentsRequest { DocumentId = "ED-1" };
        var result = SuccessResult();

        state.Save(request, result);

        Assert.True(state.HasResult);
        Assert.Same(request, state.LastRequest);
        Assert.Same(result, state.LastResult);
    }

    [Fact]
    public void HasResult_FalseWhenResultUnsuccessful()
    {
        var state = new CommentsBrowseState();
        state.Save(new FetchCommentsRequest(), new FetchCommentsResult { Success = false });
        Assert.False(state.HasResult);
    }

    [Fact]
    public void Clear_ResetsResultAndFilterSort()
    {
        var state = new CommentsBrowseState
        {
            FilterText = "acme",
            SortColumn = CommentSortColumn.Organization,
            SortDescending = true,
        };
        state.Save(new FetchCommentsRequest(), SuccessResult());

        state.Clear();

        Assert.Null(state.LastResult);
        Assert.False(state.HasResult);
        Assert.Equal(string.Empty, state.FilterText);
        Assert.Equal(CommentSortColumn.None, state.SortColumn);
        Assert.False(state.SortDescending);
    }
}
