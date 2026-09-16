using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DoedRegulatoryComments.Web.Data;
using DoedRegulatoryComments.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class AnalystPersistenceTests
{
    [Fact]
    public async Task Sqlite_SaveLoad_PreservesSourceProvenanceBodyAndQuotedEvidence()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var original = AnalystTestData.Run();
        var id = await store.Analyses.SaveRunAsync(original);
        var loaded = (await store.Analyses.LoadRunAsync(id))!;
        Assert.Equal(id, loaded.PersistedId);
        Assert.Equal(WorkspaceJson.Write(original.Sources), WorkspaceJson.Write(loaded.Sources));
        Assert.Equal(WorkspaceJson.Write(original.Provenance), WorkspaceJson.Write(loaded.Provenance));
        Assert.Equal(original.Categorizations[0].RowData, loaded.Categorizations[0].RowData);
        Assert.Equal("Keep access to services", Assert.Single(loaded.Grouped.ThemeGroups[0].Evidence).Quote);
        Assert.Equal(7, loaded.Sources[0].Passages[1].PageNumber);
    }

    [Fact]
    public async Task AdditiveSqliteUpgrade_RehydratesLegacyRowsAndCreatesSeparateWorkspaceTable()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var id = await store.Analyses.SaveRunAsync(AnalystTestData.Run());
        await using (var db = await store.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TABLE "WorkspaceDocuments";
                ALTER TABLE "Runs" DROP COLUMN "SourcesJson";
                ALTER TABLE "Runs" DROP COLUMN "ProvenanceJson";
                ALTER TABLE "Runs" DROP COLUMN "SessionName";
                ALTER TABLE "Categorizations" DROP COLUMN "RowData";
                ALTER TABLE "ThemeGroups" DROP COLUMN "EvidenceJson";
                """);
        }
        await AnalysisDatabaseInitializer.InitializeAsync(store.Factory);
        await AnalysisDatabaseInitializer.InitializeAsync(store.Factory);
        var legacy = (await store.Analyses.LoadRunAsync(id))!;
        Assert.Empty(legacy.Sources);
        Assert.Null(legacy.Provenance);
        Assert.Null(legacy.SessionName);
        Assert.Equal("", legacy.Categorizations[0].RowData);
        Assert.Empty(legacy.Grouped.ThemeGroups[0].Evidence);
        Assert.Equal("Original collective summary", legacy.Grouped.OverallSummary);
        var item = await store.Workspace.SaveAsync("reviews", id.ToString("D"), WorkspaceJson.Write(new RunReviewState()), null);
        Assert.Equal(item, await store.Workspace.GetAsync("reviews", id.ToString("D")));
    }

    [Fact]
    public async Task CorruptPersistedSourceJson_IsNotSilentlyReportedAsAnEmptySourceSet()
    {
        await using var store = await AnalystSqliteStore.CreateAsync();
        var id = await store.Analyses.SaveRunAsync(AnalystTestData.Run());
        await using (var db = await store.Factory.CreateDbContextAsync())
            await db.Runs.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(r => r.SourcesJson, "{invalid"));
        await Assert.ThrowsAnyAsync<JsonException>(() => store.Analyses.LoadRunAsync(id));
    }

    [Fact]
    public void CosmosRoundTrip_PreservesNewEvidenceAndDefaultsMissingLegacyFields()
    {
        var original = AnalystTestData.Run();
        var loaded = CosmosAnalysisRepository.RoundTripForTesting(original);
        Assert.Equal(WorkspaceJson.Write(original.Sources), WorkspaceJson.Write(loaded.Sources));
        Assert.Equal(WorkspaceJson.Write(original.Provenance), WorkspaceJson.Write(loaded.Provenance));
        Assert.Equal(original.Categorizations[0].RowData, loaded.Categorizations[0].RowData);
        Assert.Equal("Keep access to services", Assert.Single(loaded.Grouped.ThemeGroups[0].Evidence).Quote);
        var id = Guid.NewGuid();
        var legacy = CosmosAnalysisRepository.DeserializeForTesting($$"""
            {"id":"{{id:D}}","schemaVersion":1,"documentId":"OLD","categorizations":[
                {"submissionNumber":1,"commentId":"OLD-1","rawResponse":"{\"stance\":\"opposing\"}"}
            ],"themeGroups":[{"groupName":"Legacy","count":1,"submissionNumbers":[1]}]}
            """);
        Assert.Equal(id, legacy.PersistedId);
        Assert.Empty(legacy.Sources);
        Assert.Null(legacy.Provenance);
        Assert.Equal("", legacy.Categorizations[0].RowData);
        Assert.Equal("opposing", AnalystEngine.GetField(legacy.Categorizations[0], "stance"));
        Assert.Empty(legacy.Grouped.ThemeGroups[0].Evidence);
    }

    [Fact]
    public void GzipVersionOne_StillHydratesCategorizationOnly_WithoutErasingInlineSources()
    {
        var payload = AnalysisPayloadCodec.Deserialize(Compress("""
            {"schemaVersion":1,"categorizations":[
                {"submissionNumber":1,"rawResponse":"old raw","parsedJson":"{\"stance\":\"supportive\"}"}
            ]}
            """))!;
        Assert.Equal(1, payload.SchemaVersion);
        Assert.Empty(payload.Sources);
        Assert.Null(payload.Provenance);
        var json = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("D"), schemaVersion = 2, documentId = "LEGACY-PAYLOAD",
            sources = new[] { AnalystTestData.Run().Sources[0] },
            categorizations = new[] { new { submissionNumber = 1, commentId = "COMMENT-1", rowData = "retained body" } },
        }, WorkspaceJson.Options);
        var loaded = CosmosAnalysisRepository.DeserializeForTesting(json, payload);
        Assert.Single(loaded.Sources);
        Assert.Equal("old raw", loaded.Categorizations[0].RawResponse);
        Assert.Equal("retained body", loaded.Categorizations[0].RowData);
        Assert.Equal("supportive", AnalystEngine.GetField(loaded.Categorizations[0], "stance"));
    }

    [Fact]
    public void GzipVersionTwo_UsesCamelCaseSourcesAndProvenanceAndRejectsFutureVersions()
    {
        var run = AnalystTestData.Run();
        var payload = new AnalysisRunPayload
        {
            SchemaVersion = 2, Sources = run.Sources, Provenance = run.Provenance,
            Categorizations = [new CategorizationPayload(1, "raw", "{}", "body")],
        };
        var compressed = AnalysisPayloadCodec.Serialize(payload);
        var loaded = AnalysisPayloadCodec.Deserialize(compressed)!;
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(WorkspaceJson.Write(payload.Sources), WorkspaceJson.Write(loaded.Sources));
        Assert.Equal(WorkspaceJson.Write(payload.Provenance), WorkspaceJson.Write(loaded.Provenance));
        Assert.Equal("body", loaded.Categorizations[0].RowData);
        using var stream = new MemoryStream(compressed);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var json = reader.ReadToEnd();
        Assert.Contains("\"sources\"", json);
        Assert.Contains("\"commentId\"", json);
        Assert.Contains("\"passages\"", json);
        Assert.Contains("\"pageNumber\"", json);
        Assert.Contains("\"provenance\"", json);
        Assert.Contains("\"agentVersions\"", json);
        Assert.DoesNotContain("\"Sources\"", json);
        Assert.Throws<NotSupportedException>(() => AnalysisPayloadCodec.Deserialize(Compress("""{"schemaVersion":999}""")));
    }

    [Theory]
    [InlineData('x', 2_100_000)]
    [InlineData('"', 950_000)]
    [InlineData('界', 650_000)]
    public async Task CosmosOffload_MeasuresSerializedSourceBytesAndPreservesEverySource(char value, int length)
    {
        var run = AnalystTestData.Run();
        run.Sources[0].Passages[0].Text = new string(value, length);
        var payloads = new RecordingPayloadStore();
        var repository = CosmosRepository(payloads, threshold: 4_000_000);
        var json = await repository.PrepareDocumentForTestingAsync(run);
        Assert.Equal(1, payloads.Saves);
        Assert.True(Encoding.UTF8.GetByteCount(json) < CosmosAnalysisRepository.MaxInlineDocumentBytes);
        using var document = JsonDocument.Parse(json);
        Assert.Empty(document.RootElement.GetProperty("sources").EnumerateArray());
        Assert.True(document.RootElement.GetProperty("sourcesOffloaded").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("sourceCount").GetInt32());
        Assert.Equal(2, payloads.Payload!.SchemaVersion);
        Assert.Equal(length, payloads.Payload.Sources[0].Passages[0].Text.Length);
        var loaded = CosmosAnalysisRepository.DeserializeForTesting(json, payloads.Payload);
        Assert.Equal(WorkspaceJson.Write(run.Sources), WorkspaceJson.Write(loaded.Sources));
        Assert.Equal(WorkspaceJson.Write(run.Provenance), WorkspaceJson.Write(loaded.Provenance));
        Assert.Equal(run.Categorizations[0].RowData, loaded.Categorizations[0].RowData);
        Assert.Equal(length, run.Sources[0].Passages[0].Text.Length);
    }

    [Fact]
    public async Task CosmosOffload_FailsClearlyWhenBlobStorageOrSourcePayloadIsMissing()
    {
        var run = AnalystTestData.Run();
        run.Sources[0].Passages[0].Text = new string('x', 2_100_000);
        var unavailable = new RecordingPayloadStore { IsConfigured = false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CosmosRepository(unavailable).PrepareDocumentForTestingAsync(run));
        Assert.Contains("No source evidence was discarded", error.Message);
        Assert.Equal(0, unavailable.Saves);

        var payloads = new RecordingPayloadStore();
        var json = await CosmosRepository(payloads).PrepareDocumentForTestingAsync(run);
        var missingSources = new AnalysisRunPayload
        {
            SchemaVersion = 2, Categorizations = payloads.Payload!.Categorizations, Sources = [],
        };
        Assert.Throws<InvalidOperationException>(() => CosmosAnalysisRepository.DeserializeForTesting(json, missingSources));
        Assert.Equal(2, run.Sources.Count);
    }

    [Fact]
    public async Task PythonStyleVersionTwoPayload_HydratesCamelCaseEvidenceWithoutOffloadMetadata()
    {
        var run = AnalystTestData.Run();
        var payloads = new RecordingPayloadStore();
        var json = await CosmosRepository(payloads, threshold: 1).PrepareDocumentForTestingAsync(run);
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        document.Remove("sourcesOffloaded");
        document.Remove("sourceCount");
        document.Remove("provenance");
        var loaded = CosmosAnalysisRepository.DeserializeForTesting(document.ToJsonString(), payloads.Payload);
        Assert.Equal(WorkspaceJson.Write(run.Sources), WorkspaceJson.Write(loaded.Sources));
        Assert.Equal(WorkspaceJson.Write(run.Provenance), WorkspaceJson.Write(loaded.Provenance));
    }

    private static CosmosAnalysisRepository CosmosRepository(RecordingPayloadStore payloads, int threshold = 512 * 1024) =>
        new(new CosmosContainerSet(null!, null!, false), NullLogger<CosmosAnalysisRepository>.Instance, payloads,
            Options.Create(new AnalysisPayloadOptions { OffloadThresholdBytes = threshold }),
            new OperationalTelemetry(Options.Create(new FoundryCostOptions())));

    private static byte[] Compress(string json)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return stream.ToArray();
    }

    private sealed class RecordingPayloadStore : IAnalysisPayloadStore
    {
        public bool IsConfigured { get; init; } = true;
        public AnalysisRunPayload? Payload { get; private set; }
        public int Saves { get; private set; }
        public Task EnsureCreatedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SaveAsync(Guid runId, AnalysisRunPayload payload, CancellationToken ct = default)
        {
            Saves++;
            Payload = AnalysisPayloadCodec.Deserialize(AnalysisPayloadCodec.Serialize(payload));
            return Task.FromResult($"analysis-runs/{runId:D}/categorizations.json.gz");
        }
        public Task<AnalysisRunPayload?> LoadAsync(string blobName, CancellationToken ct = default) => Task.FromResult(Payload);
        public Task DeleteAsync(string blobName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
