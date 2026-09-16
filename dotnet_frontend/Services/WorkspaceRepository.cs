using System.Net;
using System.Text;
using System.Text.Json;
using DoedRegulatoryComments.Web.Data;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace DoedRegulatoryComments.Web.Services;

public sealed class RelationalWorkspaceRepository(IDbContextFactory<AnalysisDbContext> factory)
    : IWorkspaceRepository
{
    public async Task<WorkspaceItem?> GetAsync(string kind, string id, CancellationToken ct = default)
    {
        WorkspaceStorage.ValidateKey(kind, id);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var item = await db.WorkspaceDocuments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Kind == kind && x.Id == id, ct).ConfigureAwait(false);
        return item is null ? null : ToItem(item);
    }

    public async Task<AnalysisPage<WorkspaceItem>> ListPageAsync(
        string kind, int take = 50, string? continuationToken = null, CancellationToken ct = default)
    {
        WorkspaceStorage.ValidateKind(kind);
        var after = WorkspaceStorage.DecodeToken("sql", kind, continuationToken);
        take = Math.Clamp(take, 1, 500);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.WorkspaceDocuments.AsNoTracking().Where(x => x.Kind == kind);
        if (after is not null)
            query = query.Where(x => x.Id.CompareTo(after) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(take + 1).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Take(take).Select(ToItem).ToList();
        return new AnalysisPage<WorkspaceItem>(items, rows.Count > take
            ? WorkspaceStorage.EncodeToken("sql", kind, items[^1].Id)
            : null);
    }

    public async Task<WorkspaceItem> SaveAsync(
        string kind, string id, string json, string? expectedVersion, CancellationToken ct = default)
    {
        WorkspaceStorage.ValidateWrite(kind, id, json, expectedVersion);
        var version = Guid.NewGuid().ToString("N");
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (expectedVersion is null)
        {
            db.WorkspaceDocuments.Add(new StoredWorkspaceDocument
            {
                Kind = kind, Id = id, Json = json, Version = version,
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                throw new WorkspaceConflictException();
            }
        }
        else
        {
            var changed = await db.WorkspaceDocuments
                .Where(x => x.Kind == kind && x.Id == id && x.Version == expectedVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Json, json)
                    .SetProperty(x => x.Version, version), ct).ConfigureAwait(false);
            if (changed != 1) throw new WorkspaceConflictException();
        }
        return new WorkspaceItem(id, kind, json, version);
    }

    public async Task DeleteAsync(
        string kind, string id, string expectedVersion, CancellationToken ct = default)
    {
        WorkspaceStorage.ValidateKey(kind, id);
        WorkspaceStorage.ValidateVersion(expectedVersion);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = await db.WorkspaceDocuments
            .Where(x => x.Kind == kind && x.Id == id && x.Version == expectedVersion)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted != 1) throw new WorkspaceConflictException();
    }

    private static WorkspaceItem ToItem(StoredWorkspaceDocument item) =>
        new(item.Id, item.Kind, item.Json, item.Version);

    private static bool IsDuplicateKey(DbUpdateException ex) => ex.InnerException switch
    {
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        SqlException sql => sql.Number is 2601 or 2627,
        _ => false,
    };
}

public sealed class CosmosWorkspaceRepository : IWorkspaceRepository
{
    private readonly Container _container;

    public CosmosWorkspaceRepository(
        CosmosClient client, CosmosPersistenceOptions persistenceOptions, IOptions<WorkspaceOptions> options)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ContainerName))
            throw new InvalidOperationException("Workspace:ContainerName is required.");
        _container = client.GetContainer(persistenceOptions.DatabaseName, options.Value.ContainerName);
    }

    internal CosmosWorkspaceRepository(Container container) => _container = container;

    public async Task<WorkspaceItem?> GetAsync(string kind, string id, CancellationToken ct = default)
    {
        var key = WorkspaceStorage.StorageId(kind, id);
        try
        {
            var response = await _container.ReadItemAsync<WorkspaceDocument>(
                key, new PartitionKey(key), cancellationToken: ct).ConfigureAwait(false);
            return response.Resource.ToItem(response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AnalysisPage<WorkspaceItem>> ListPageAsync(
        string kind, int take = 50, string? continuationToken = null, CancellationToken ct = default)
    {
        WorkspaceStorage.ValidateKind(kind);
        var token = WorkspaceStorage.DecodeToken("cosmos", kind, continuationToken);
        var query = new QueryDefinition(
            "SELECT c.id, c.itemId, c.kind, c.json, c._etag FROM c WHERE c.kind = @kind ORDER BY c.id")
            .WithParameter("@kind", kind);
        using var iterator = _container.GetItemQueryIterator<WorkspaceDocument>(
            query, token, new QueryRequestOptions { MaxItemCount = Math.Clamp(take, 1, 500) });
        if (!iterator.HasMoreResults)
            return new AnalysisPage<WorkspaceItem>(Array.Empty<WorkspaceItem>(), null);
        var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
        return new AnalysisPage<WorkspaceItem>(
            page.Select(x => x.ToItem()).ToList(),
            string.IsNullOrEmpty(page.ContinuationToken) ? null
                : WorkspaceStorage.EncodeToken("cosmos", kind, page.ContinuationToken));
    }

    public async Task<WorkspaceItem> SaveAsync(
        string kind, string id, string json, string? expectedVersion, CancellationToken ct = default)
    {
        WorkspaceStorage.ValidateWrite(kind, id, json, expectedVersion);
        var document = new WorkspaceDocument
        {
            Id = WorkspaceStorage.StorageId(kind, id), ItemId = id, Kind = kind, Json = json,
        };
        if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(document)) > WorkspaceStorage.MaxDocumentBytes)
            throw new ArgumentException("The workspace history is too large for a Cosmos DB item.", nameof(json));

        try
        {
            var response = expectedVersion is null
                ? await _container.CreateItemAsync(document, new PartitionKey(document.Id),
                    cancellationToken: ct).ConfigureAwait(false)
                : await _container.ReplaceItemAsync(document, document.Id, new PartitionKey(document.Id),
                    new ItemRequestOptions { IfMatchEtag = expectedVersion }, ct).ConfigureAwait(false);
            return document.ToItem(response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
            || (expectedVersion is not null && ex.StatusCode == HttpStatusCode.NotFound))
        {
            throw new WorkspaceConflictException();
        }
    }

    public async Task DeleteAsync(
        string kind, string id, string expectedVersion, CancellationToken ct = default)
    {
        var key = WorkspaceStorage.StorageId(kind, id);
        WorkspaceStorage.ValidateVersion(expectedVersion);
        try
        {
            await _container.DeleteItemAsync<WorkspaceDocument>(key, new PartitionKey(key),
                new ItemRequestOptions { IfMatchEtag = expectedVersion, EnableContentResponseOnWrite = false },
                ct).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new WorkspaceConflictException();
        }
    }

    private sealed class WorkspaceDocument
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("itemId")] public string ItemId { get; set; } = string.Empty;
        [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;
        [JsonProperty("json")] public string Json { get; set; } = "{}";
        [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)] public string? Etag { get; set; }

        public WorkspaceItem ToItem(string? version = null) =>
            new(ItemId, Kind, Json, version ?? Etag
                ?? throw new InvalidOperationException("The workspace item has no concurrency version."));
    }
}

internal static class WorkspaceStorage
{
    internal const int MaxDocumentBytes = 1_800_000;

    internal static void ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64
            || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Workspace kind must contain 1–64 letters, digits, hyphens or underscores.", nameof(kind));
    }

    internal static void ValidateKey(string kind, string id)
    {
        ValidateKind(kind);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id != id.Trim()
            || id.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
            throw new ArgumentException("The workspace ID is invalid or longer than 256 characters.", nameof(id));
    }

    internal static string StorageId(string kind, string id)
    {
        ValidateKey(kind, id);
        var key = $"{kind}:{id}";
        if (Encoding.UTF8.GetByteCount(key) > 1023)
            throw new ArgumentException("The workspace ID is too large.", nameof(id));
        return key;
    }

    internal static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 256)
            throw new ArgumentException("An expected version is required.", nameof(version));
    }

    internal static void ValidateWrite(string kind, string id, string json, string? expectedVersion)
    {
        ValidateKey(kind, id);
        if (expectedVersion is not null) ValidateVersion(expectedVersion);
        ArgumentNullException.ThrowIfNull(json);
        using var parsed = JsonDocument.Parse(json);
    }

    internal static string EncodeToken(string provider, string kind, string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkspaceJson.Write(new PageToken(1, provider, kind, value))));

    internal static string? DecodeToken(string provider, string kind, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var decoded = System.Text.Json.JsonSerializer.Deserialize<PageToken>(
                Convert.FromBase64String(token), WorkspaceJson.Options);
            if (decoded is { Version: 1 } && decoded.Provider == provider && decoded.Kind == kind
                && !string.IsNullOrWhiteSpace(decoded.Value))
                return decoded.Value;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
        }
        throw new ArgumentException("The workspace continuation token is invalid for this query.", nameof(token));
    }

    private sealed record PageToken(int Version, string Provider, string Kind, string Value);
}
