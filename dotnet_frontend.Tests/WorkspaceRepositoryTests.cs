using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class WorkspaceRepositoryTests
{
    [Fact]
    public async Task CreateReplaceAndDelete_RequireTheExpectedVersion()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var created = await store.Workspace.SaveAsync("reviews", "run-1", "{\"value\":1}", null);
        Assert.Equal(created, await store.Workspace.GetAsync("reviews", "run-1"));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            store.Workspace.SaveAsync("reviews", "run-1", "{\"value\":2}", null));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            store.Workspace.SaveAsync("reviews", "run-1", "{\"value\":2}", "stale"));

        var saved = await store.Workspace.SaveAsync("reviews", "run-1", "{\"value\":2}", created.Version);
        Assert.NotEqual(created.Version, saved.Version);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            store.Workspace.SaveAsync("reviews", "run-1", "{\"value\":3}", created.Version));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            store.Workspace.DeleteAsync("reviews", "run-1", created.Version));
        Assert.Equal(saved, await store.Workspace.GetAsync("reviews", "run-1"));

        await store.Workspace.DeleteAsync("reviews", "run-1", saved.Version);
        Assert.Null(await store.Workspace.GetAsync("reviews", "run-1"));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            store.Workspace.DeleteAsync("reviews", "run-1", saved.Version));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() =>
            store.Workspace.SaveAsync("reviews", "run-1", "{}", saved.Version));
    }

    [Fact]
    public async Task KeysAreScopedByKindAndCase_AndPagesUseStableKeysetBoundaries()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        for (var i = 1; i <= 5; i++)
            await store.Workspace.SaveAsync("savedViews", $"view-{i}", "{}", null);
        await store.Workspace.SaveAsync("reviews", "view-1", "{}", null);
        await store.Workspace.SaveAsync("SavedViews", "view-1", "{}", null);
        Assert.NotNull(await store.Workspace.GetAsync("SavedViews", "view-1"));
        Assert.Null(await store.Workspace.GetAsync("savedViews", "VIEW-1"));

        var first = await store.Workspace.ListPageAsync("savedViews", take: 2);
        Assert.Equal(new[] { "view-1", "view-2" }, first.Items.Select(x => x.Id));
        Assert.True(first.HasMore);
        await store.Workspace.DeleteAsync("savedViews", first.Items[0].Id, first.Items[0].Version);
        var second = await store.Workspace.ListPageAsync("savedViews", take: 2, continuationToken: first.ContinuationToken);
        var third = await store.Workspace.ListPageAsync("savedViews", take: 2, continuationToken: second.ContinuationToken);
        Assert.Equal(new[] { "view-3", "view-4" }, second.Items.Select(x => x.Id));
        Assert.Equal("view-5", Assert.Single(third.Items).Id);
        Assert.False(third.HasMore);
        Assert.All(first.Items.Concat(second.Items).Concat(third.Items), x => Assert.Equal("savedViews", x.Kind));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.Workspace.ListPageAsync("reviews", continuationToken: first.ContinuationToken));
    }

    [Fact]
    public async Task InvalidIdentifiersVersionsJsonAndPageTokens_AreRejectedBeforeWrites()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.Workspace.SaveAsync("bad/kind", "id", "{}", null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Workspace.SaveAsync("reviews", "bad/id", "{}", null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Workspace.SaveAsync("reviews", "id", "{}", ""));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => store.Workspace.SaveAsync("reviews", "id", "{", null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Workspace.ListPageAsync("reviews", continuationToken: "not-base64"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Workspace.DeleteAsync("reviews", "id", ""));
        Assert.Empty((await store.Workspace.ListPageAsync("reviews")).Items);
    }

    [Fact]
    public void CosmosPhysicalKeysAndTokens_DoNotCrossKindsOrProviders()
    {
        Assert.Equal("reviews:run-1", WorkspaceStorage.StorageId("reviews", "run-1"));
        Assert.NotEqual(WorkspaceStorage.StorageId("reviews", "same"), WorkspaceStorage.StorageId("savedViews", "same"));
        const string opaque = "[{\"range\":\"A/+\"}]";
        var token = WorkspaceStorage.EncodeToken("cosmos", "reviews", opaque);
        Assert.Equal(opaque, WorkspaceStorage.DecodeToken("cosmos", "reviews", token));
        Assert.Throws<ArgumentException>(() => WorkspaceStorage.DecodeToken("sql", "reviews", token));
        Assert.Throws<ArgumentException>(() => WorkspaceStorage.DecodeToken("cosmos", "savedViews", token));
    }

    [Fact]
    public void SqlServerModel_ContainsSeparateJsonDocumentsAndConcurrencyColumn()
    {
        using var db = new AnalysisDbContext(new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=SchemaOnly;Integrated Security=true;").Options);
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [WorkspaceDocuments]", script);
        Assert.Contains("PRIMARY KEY ([Kind], [Id])", script);
        Assert.Contains("[SourcesJson] nvarchar(max)", script);
        Assert.Contains("[ProvenanceJson] nvarchar(max)", script);
        Assert.Contains("[RowData] nvarchar(max)", script);
        Assert.Contains("[EvidenceJson] nvarchar(max)", script);
        Assert.True(db.Model.FindEntityType(typeof(StoredWorkspaceDocument))!.FindProperty("Version")!.IsConcurrencyToken);
    }
}
