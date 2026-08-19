using DoedRegulatoryComments.Web.Services;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public class AnalysisStoreTests
{
    [Fact]
    public void ToggleSelection_AddsThenRemoves()
    {
        var store = new AnalysisStore();
        var c = TestData.Comment("ED-1");

        store.ToggleSelection(c);
        Assert.True(store.IsSelected("ED-1"));
        Assert.Equal(1, store.SelectedCount);

        store.ToggleSelection(c);
        Assert.False(store.IsSelected("ED-1"));
        Assert.Equal(0, store.SelectedCount);
    }

    [Fact]
    public void IsSelected_IsCaseInsensitive()
    {
        var store = new AnalysisStore();
        store.ToggleSelection(TestData.Comment("ED-ABC"));
        Assert.True(store.IsSelected("ed-abc"));
    }

    [Fact]
    public void SelectAll_AddsAll_Idempotently()
    {
        var store = new AnalysisStore();
        var comments = new[] { TestData.Comment("A"), TestData.Comment("B"), TestData.Comment("A") };

        store.SelectAll(comments);

        Assert.Equal(2, store.SelectedCount);
        Assert.True(store.IsSelected("A"));
        Assert.True(store.IsSelected("B"));
    }

    [Fact]
    public void ClearSelection_Empties()
    {
        var store = new AnalysisStore();
        store.SelectAll(new[] { TestData.Comment("A"), TestData.Comment("B") });

        store.ClearSelection();

        Assert.Equal(0, store.SelectedCount);
    }

    [Fact]
    public void HasInput_TracksComments()
    {
        var store = new AnalysisStore();
        Assert.False(store.HasInput);

        store.Comments = new[] { TestData.Comment("A") };
        Assert.True(store.HasInput);
    }
}
