using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class CommentFilterTests
{
    private static readonly IReadOnlyList<CommentResource> Sample = new[]
    {
        TestData.Comment("ED-2025-0001", first: "Alice", last: "Anderson", org: "Acme School District", title: "Support the rule", posted: new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero)),
        TestData.Comment("ED-2025-0002", first: "Bob", last: "Brown", org: "Beta Foundation", title: "Concerns about funding", posted: new DateTimeOffset(2025, 3, 10, 0, 0, 0, TimeSpan.Zero)),
        TestData.Comment("ED-2025-0003", first: "Carol", last: "Clark", org: "acme charter", title: "Neutral observation", posted: new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)),
    };

    [Fact]
    public void Apply_NullComments_ReturnsEmpty()
    {
        var result = CommentFilter.Apply(null, null, CommentSortColumn.None, false);
        Assert.Empty(result);
    }

    [Fact]
    public void Apply_NoFilterNoSort_PreservesOrder()
    {
        var result = CommentFilter.Apply(Sample, "", CommentSortColumn.None, false);
        Assert.Equal(new[] { "ED-2025-0001", "ED-2025-0002", "ED-2025-0003" }, result.Select(c => c.Id));
    }

    [Theory]
    [InlineData("alice", 1)]      // first name
    [InlineData("brown", 1)]      // last name
    [InlineData("acme", 2)]       // organization, case-insensitive, two matches
    [InlineData("funding", 1)]    // title
    [InlineData("0003", 1)]       // comment id
    [InlineData("zzz", 0)]        // no match
    public void Apply_FilterText_MatchesExpectedCount(string term, int expected)
    {
        var result = CommentFilter.Apply(Sample, term, CommentSortColumn.None, false);
        Assert.Equal(expected, result.Count);
    }

    [Fact]
    public void Apply_WhitespaceFilter_ReturnsAll()
    {
        var result = CommentFilter.Apply(Sample, "   ", CommentSortColumn.None, false);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Apply_SortPostedAscending()
    {
        var result = CommentFilter.Apply(Sample, null, CommentSortColumn.Posted, sortDescending: false);
        Assert.Equal(new[] { "ED-2025-0001", "ED-2025-0003", "ED-2025-0002" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Apply_SortPostedDescending()
    {
        var result = CommentFilter.Apply(Sample, null, CommentSortColumn.Posted, sortDescending: true);
        Assert.Equal(new[] { "ED-2025-0002", "ED-2025-0003", "ED-2025-0001" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Apply_SortCommenter_IsCaseInsensitiveAndAlphabetical()
    {
        var result = CommentFilter.Apply(Sample, null, CommentSortColumn.Commenter, sortDescending: false);
        Assert.Equal(new[] { "ED-2025-0001", "ED-2025-0002", "ED-2025-0003" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Apply_SortOrganization_Descending()
    {
        var result = CommentFilter.Apply(Sample, null, CommentSortColumn.Organization, sortDescending: true);
        // Beta Foundation, acme charter, Acme School District (ordinal-ignore-case desc)
        Assert.Equal("ED-2025-0002", result[0].Id);
    }

    [Fact]
    public void Apply_FilterThenSort_Combined()
    {
        var result = CommentFilter.Apply(Sample, "acme", CommentSortColumn.Organization, sortDescending: false);
        Assert.Equal(2, result.Count);
        // "acme charter" vs "Acme School District" — case-insensitive: charter < school
        Assert.Equal("ED-2025-0003", result[0].Id);
        Assert.Equal("ED-2025-0001", result[1].Id);
    }

    [Fact]
    public void Matches_NullFields_DoNotThrow()
    {
        var bare = TestData.Comment("ID-1");
        Assert.True(CommentFilter.Matches(bare, "id-1"));
        Assert.False(CommentFilter.Matches(bare, "nope"));
    }

    [Fact]
    public void CommenterName_TrimsWhenOneSideMissing()
    {
        var a = new CommentAttributes { FirstName = "Solo", LastName = null };
        Assert.Equal("Solo", CommentFilter.CommenterName(a));
    }
}
