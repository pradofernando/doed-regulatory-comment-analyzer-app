using System.IO.Compression;
using System.Text.Json;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace DoedRegulatoryComments.Web.Services;

public sealed class AnalysisPayloadOptions
{
    public const string SectionName = "Persistence:Payloads";

    public string BlobContainerUri { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "analysis-run-payloads";
    public int OffloadThresholdBytes { get; set; } = 512 * 1024;
    public bool CreateIfNotExists { get; set; }
}

public sealed record CategorizationPayload(
    int SubmissionNumber,
    string RawResponse,
    string ParsedJson);

public sealed class AnalysisRunPayload
{
    public int SchemaVersion { get; init; } = 1;
    public List<CategorizationPayload> Categorizations { get; init; } = new();
}

public interface IAnalysisPayloadStore
{
    bool IsConfigured { get; }
    Task EnsureCreatedAsync(CancellationToken ct = default);
    Task<string> SaveAsync(Guid runId, AnalysisRunPayload payload, CancellationToken ct = default);
    Task<AnalysisRunPayload?> LoadAsync(string blobName, CancellationToken ct = default);
    Task DeleteAsync(string blobName, CancellationToken ct = default);
}

public sealed class BlobAnalysisPayloadStore : IAnalysisPayloadStore
{
    private readonly BlobContainerClient? _container;

    public BlobAnalysisPayloadStore(
        IOptions<AnalysisPayloadOptions> options,
        TokenCredential credential)
    {
        var value = options.Value;
        if (!string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            _container = new BlobContainerClient(value.ConnectionString, value.ContainerName);
            return;
        }
        if (string.IsNullOrWhiteSpace(value.BlobContainerUri)) return;

        _container = new BlobContainerClient(new Uri(value.BlobContainerUri), credential);
    }

    public bool IsConfigured => _container is not null;

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (_container is null) return;
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    public async Task<string> SaveAsync(
        Guid runId,
        AnalysisRunPayload payload,
        CancellationToken ct = default)
    {
        if (_container is null)
            throw new InvalidOperationException("Analysis payload Blob Storage is not configured.");

        var blobName = $"analysis-runs/{runId:D}/categorizations.json.gz";
        var blob = _container.GetBlobClient(blobName);
        var content = AnalysisPayloadCodec.Serialize(payload);
        using var stream = new MemoryStream(content, writable: false);
        await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct).ConfigureAwait(false);
        return blobName;
    }

    public async Task<AnalysisRunPayload?> LoadAsync(
        string blobName,
        CancellationToken ct = default)
    {
        if (_container is null)
            throw new InvalidOperationException("Analysis payload Blob Storage is not configured.");

        var blob = _container.GetBlobClient(blobName);
        try
        {
            var response = await blob.DownloadContentAsync(ct).ConfigureAwait(false);
            return AnalysisPayloadCodec.Deserialize(response.Value.Content.ToArray());
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct = default)
    {
        if (_container is null || string.IsNullOrWhiteSpace(blobName)) return;
        await _container.DeleteBlobIfExistsAsync(
            blobName,
            DeleteSnapshotsOption.IncludeSnapshots,
            cancellationToken: ct).ConfigureAwait(false);
    }
}

internal static class AnalysisPayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Serialize(AnalysisRunPayload payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            JsonSerializer.Serialize(gzip, payload, JsonOptions);
        }
        return output.ToArray();
    }

    public static AnalysisRunPayload? Deserialize(byte[] content)
    {
        using var input = new MemoryStream(content, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<AnalysisRunPayload>(gzip, JsonOptions);
    }
}