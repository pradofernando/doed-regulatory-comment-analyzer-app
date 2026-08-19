using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace DoedRegulatoryComments.Web.Services;

public sealed class CosmosPersistenceOptions
{
    public const string SectionName = "Persistence:Cosmos";

    public string Endpoint { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "doed-regulatory-comments";
    public string ContainerName { get; set; } = "analysis-runs";
    public string SummaryContainerName { get; set; } = "analysis-run-summaries";
    public bool CreateIfNotExists { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException(
                "Persistence:Cosmos:Endpoint is required for managed identity, or provide a local ConnectionString.");
        if (string.IsNullOrWhiteSpace(DatabaseName))
            throw new InvalidOperationException("Persistence:Cosmos:DatabaseName is required.");
        if (string.IsNullOrWhiteSpace(ContainerName))
            throw new InvalidOperationException("Persistence:Cosmos:ContainerName is required.");
    }
}

public sealed record CosmosContainerSet(
    Container Runs,
    Container Summaries,
    bool HasDedicatedSummaries);

public sealed class CosmosAnalysisRepository : IAnalysisRepository
{
    private const string DocumentType = "analysisRun";
    private const string SummaryDocumentType = "analysisRunSummary";
    private const string SummaryBackfillMarkerId = "analysis-run-summary-backfill-v2";
    private const string SystemPartitionKey = "__system__";
    private const int CurrentSchemaVersion = 2;
    private const int MaxConcurrencyAttempts = 3;
    private static readonly SemaphoreSlim SummaryBackfillGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Container _container;
    private readonly Container _summaryContainer;
    private readonly bool _hasDedicatedSummaries;
    private readonly ILogger<CosmosAnalysisRepository> _logger;
    private readonly IAnalysisPayloadStore _payloadStore;
    private readonly AnalysisPayloadOptions _payloadOptions;
    private readonly OperationalTelemetry _telemetry;

    public CosmosAnalysisRepository(
        CosmosContainerSet containers,
        ILogger<CosmosAnalysisRepository> logger,
        IAnalysisPayloadStore payloadStore,
        IOptions<AnalysisPayloadOptions> payloadOptions,
        OperationalTelemetry telemetry)
    {
        _container = containers.Runs;
        _summaryContainer = containers.Summaries;
        _hasDedicatedSummaries = containers.HasDedicatedSummaries;
        _logger = logger;
        _payloadStore = payloadStore;
        _payloadOptions = payloadOptions.Value;
        _telemetry = telemetry;
    }

    public async Task<Guid> SaveRunAsync(AnalysisRun run, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var document = ToDocument(id, run);
        string? payloadBlobName = null;

        try
        {
            payloadBlobName = await OffloadPayloadIfNeededAsync(id, document, ct).ConfigureAwait(false);
            var response = await _container.CreateItemAsync(
                document,
                new PartitionKey(document.Id),
                new ItemRequestOptions { EnableContentResponseOnWrite = false },
                ct).ConfigureAwait(false);
            LogRequestCharge("create-run", response.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            await TryDeletePayloadAsync(payloadBlobName, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(
                "This analysis run exceeds Cosmos DB's 2 MB item limit even after payload offload. Use the AzureSql provider for this workload.", ex);
        }
        catch
        {
            await TryDeletePayloadAsync(payloadBlobName, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await TryUpsertSummaryAsync(document, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Saved Cosmos analysis run {RunId} for document {DocumentId} ({Count} comments, {Themes} themes).",
            id, run.DocumentId, run.TotalComments, run.Grouped.ThemeGroups.Count);
        return id;
    }

    public Task AppendFollowUpAsync(Guid runId, FollowUpTurn turn, CancellationToken ct = default) =>
        UpdateAsync(runId, document =>
        {
            document.FollowUpHistory.Add(new FollowUpTurnDocument
            {
                Position = document.FollowUpHistory.Count,
                Role = turn.Role,
                Text = turn.Text,
                At = turn.At,
            });
        }, ct);

    public Task SetFollowUpThreadAsync(Guid runId, string threadId, CancellationToken ct = default) =>
        UpdateAsync(runId, document => document.FollowUpThreadId = threadId, ct);

    public Task RenameRunAsync(Guid runId, string? sessionName, CancellationToken ct = default)
    {
        var normalized = AnalysisSessionNames.Normalize(sessionName);
        return RenameAndSyncSummaryAsync(runId, normalized, ct);
    }

    public async Task<IReadOnlyList<AnalysisRunSummary>> ListAsync(
        AnalysisListFilter filter,
        CancellationToken ct = default) =>
        (await ListPageAsync(filter, cancellationToken: ct).ConfigureAwait(false)).Items;

    public async Task<AnalysisPage<AnalysisRunSummary>> ListPageAsync(
        AnalysisListFilter filter,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        var decodedToken = CosmosContinuationTokenCodec.Decode(continuationToken);
        if (decodedToken.Source == CosmosPageSource.Aggregate)
        {
            return CosmosContinuationTokenCodec.EncodePage(
                await QueryPageAsync(
                    _container,
                    filter,
                    decodedToken.Token,
                    usesSummaryDocuments: false,
                    cancellationToken).ConfigureAwait(false),
                CosmosPageSource.Aggregate);
        }

        if (_hasDedicatedSummaries)
        {
            try
            {
                var summariesReady = await EnsureSummaryBackfillAsync(cancellationToken).ConfigureAwait(false);
                if (!summariesReady)
                {
                    if (decodedToken.Source == CosmosPageSource.Summary)
                        throw new InvalidOperationException(
                            "Cosmos summary migration is in progress. Refresh the Library to restart paging.");
                    return CosmosContinuationTokenCodec.EncodePage(
                        await QueryPageAsync(
                            _container,
                            filter,
                            decodedToken.Token,
                            usesSummaryDocuments: false,
                            cancellationToken).ConfigureAwait(false),
                        CosmosPageSource.Aggregate);
                }

                return CosmosContinuationTokenCodec.EncodePage(await QueryPageAsync(
                    _summaryContainer,
                    filter,
                    decodedToken.Token,
                    usesSummaryDocuments: true,
                    cancellationToken).ConfigureAwait(false), CosmosPageSource.Summary);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (decodedToken.Source == CosmosPageSource.Summary)
                    throw new InvalidOperationException(
                        "The Cosmos summary container became unavailable. Refresh the Library to restart paging.", ex);
                _logger.LogWarning(
                    "Cosmos summary container is unavailable; falling back to aggregate run queries.");
            }
        }
        else if (decodedToken.Source == CosmosPageSource.Summary)
        {
            throw new InvalidOperationException(
                "The continuation token belongs to a Cosmos summary container that is not configured.");
        }

        return CosmosContinuationTokenCodec.EncodePage(await QueryPageAsync(
            _container,
            filter,
            decodedToken.Token,
            usesSummaryDocuments: false,
            cancellationToken).ConfigureAwait(false), CosmosPageSource.Aggregate);
    }

    private async Task<AnalysisPage<AnalysisRunSummary>> QueryPageAsync(
        Container container,
        AnalysisListFilter filter,
        string? continuationToken,
        bool usesSummaryDocuments,
        CancellationToken ct)
    {
        var take = Math.Clamp(filter.Take, 1, 500);
        var predicates = new List<string> { "c.type = @type" };
        var documentIdFilter = AnalysisDocumentIds.Normalize(filter.DocumentId);

        if (!string.IsNullOrWhiteSpace(documentIdFilter))
        {
            predicates.Add(usesSummaryDocuments
                ? "c.documentIdNormalized = @documentId"
                : "(c.documentIdNormalized = @documentId OR (NOT IS_DEFINED(c.documentIdNormalized) AND UPPER(c.documentId) = @documentId))");
        }
        if (filter.SucceededOnly is true)
        {
            predicates.Add("c.succeeded = true");
        }

        var themeCountProjection = usesSummaryDocuments
            ? "c.themeCount"
            : "ARRAY_LENGTH(c.themeGroups)";
        var query = new QueryDefinition($"""
            SELECT
                c.id,
                c.sessionName,
                c.documentId,
                c.startedAt,
                c.completedAt,
                c.totalComments,
                {themeCountProjection} AS themeCount,
                c.succeeded,
                c.errorMessage,
                c.overallSentiment
            FROM c
            WHERE {string.Join(" AND ", predicates)}
            ORDER BY c.startedAt DESC
            """)
            .WithParameter("@type", usesSummaryDocuments ? SummaryDocumentType : DocumentType);
        if (!string.IsNullOrWhiteSpace(documentIdFilter))
        {
            query.WithParameter("@documentId", documentIdFilter);
        }

        var requestOptions = new QueryRequestOptions { MaxItemCount = take };
        if (usesSummaryDocuments && !string.IsNullOrWhiteSpace(documentIdFilter))
        {
            requestOptions.PartitionKey = new PartitionKey(documentIdFilter);
        }

        using var iterator = container.GetItemQueryIterator<RunSummaryDocument>(
            query,
            continuationToken,
            requestOptions);

        if (!iterator.HasMoreResults)
            return new AnalysisPage<AnalysisRunSummary>(Array.Empty<AnalysisRunSummary>(), null);

        var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
        LogRequestCharge(usesSummaryDocuments ? "list-summaries" : "list-runs", page.RequestCharge);
        var results = page.Select(item => new AnalysisRunSummary(
                Guid.Parse(item.Id),
                item.DocumentId,
                item.StartedAt,
                item.CompletedAt,
                item.TotalComments,
                item.ThemeCount,
                item.Succeeded,
                item.ErrorMessage,
                item.OverallSentiment,
                item.SessionName))
            .ToList();

        return new AnalysisPage<AnalysisRunSummary>(results, page.ContinuationToken);
    }

    public async Task<AnalysisRun?> LoadRunAsync(Guid id, CancellationToken ct = default)
    {
        var key = id.ToString("D");
        try
        {
            var response = await _container.ReadItemAsync<AnalysisRunDocument>(
                key, new PartitionKey(key), cancellationToken: ct).ConfigureAwait(false);
            LogRequestCharge("read-run", response.RequestCharge);
            await HydratePayloadAsync(response.Resource, ct).ConfigureAwait(false);
            return ToAnalysisRun(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteRunAsync(Guid id, CancellationToken ct = default)
    {
        var key = id.ToString("D");
        AnalysisRunDocument? existingDocument;
        try
        {
            var existing = await _container.ReadItemAsync<AnalysisRunDocument>(
                key, new PartitionKey(key), cancellationToken: ct).ConfigureAwait(false);
            existingDocument = existing.Resource;
            LogRequestCharge("read-before-delete", existing.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var documentIdNormalized = string.IsNullOrWhiteSpace(existingDocument.DocumentIdNormalized)
            ? AnalysisDocumentIds.Normalize(existingDocument.DocumentId)
            : existingDocument.DocumentIdNormalized;

        if (_hasDedicatedSummaries && !string.IsNullOrWhiteSpace(documentIdNormalized))
        {
            try
            {
                var response = await _summaryContainer.DeleteItemAsync<RunSummaryDocument>(
                    key,
                    new PartitionKey(documentIdNormalized),
                    new ItemRequestOptions { EnableContentResponseOnWrite = false },
                    ct).ConfigureAwait(false);
                LogRequestCharge("delete-summary", response.RequestCharge);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        await DeletePayloadAsync(existingDocument.PayloadBlobName, ct).ConfigureAwait(false);

        var deleteResponse = await _container.DeleteItemAsync<AnalysisRunDocument>(
            key,
            new PartitionKey(key),
            new ItemRequestOptions { EnableContentResponseOnWrite = false },
            ct).ConfigureAwait(false);
        LogRequestCharge("delete-run", deleteResponse.RequestCharge);
    }

    private async Task UpdateAsync(
        Guid runId,
        Action<AnalysisRunDocument> update,
        CancellationToken ct)
    {
        var key = runId.ToString("D");
        await CosmosOptimisticConcurrency.ExecuteAsync(
            async attempt =>
            {
                ItemResponse<AnalysisRunDocument> response;
                try
                {
                    response = await _container.ReadItemAsync<AnalysisRunDocument>(
                        key, new PartitionKey(key), cancellationToken: ct).ConfigureAwait(false);
                    LogRequestCharge("read-before-replace", response.RequestCharge);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return CosmosUpdateAttemptResult.Missing;
                }

                update(response.Resource);
                try
                {
                    var replace = await _container.ReplaceItemAsync(
                        response.Resource,
                        key,
                        new PartitionKey(key),
                        new ItemRequestOptions
                        {
                            IfMatchEtag = response.ETag,
                            EnableContentResponseOnWrite = false,
                        },
                        ct).ConfigureAwait(false);
                    LogRequestCharge("replace-run", replace.RequestCharge);
                    return CosmosUpdateAttemptResult.Updated;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    return CosmosUpdateAttemptResult.Conflict;
                }
            },
            MaxConcurrencyAttempts,
            attempt =>
                _logger.LogWarning(
                    "Cosmos run {RunId} changed concurrently; retrying update ({Attempt}/{MaxAttempts}).",
                    runId, attempt, MaxConcurrencyAttempts),
            () => new InvalidOperationException(
                $"Could not update analysis run {runId} after concurrent changes."))
            .ConfigureAwait(false);
    }

    private static AnalysisRunDocument ToDocument(Guid id, AnalysisRun run) => new()
    {
        Id = id.ToString("D"),
        SchemaVersion = CurrentSchemaVersion,
        SessionName = AnalysisSessionNames.Normalize(run.SessionName),
        DocumentId = run.DocumentId,
        DocumentIdNormalized = AnalysisDocumentIds.Normalize(run.DocumentId),
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        BatchSize = run.BatchSize,
        TotalComments = run.TotalComments,
        Succeeded = run.Succeeded,
        ErrorMessage = run.ErrorMessage,
        OverallSummary = run.Grouped.OverallSummary,
        OverallSentiment = run.Grouped.OverallSentiment,
        Patterns = run.Grouped.Patterns.ToList(),
        Recommendations = run.Grouped.Recommendations.ToList(),
        FollowUpThreadId = run.FollowUpThreadId,
        Categorizations = run.Categorizations.Select(item => new CategorizationDocument
        {
            SubmissionNumber = item.SubmissionNumber,
            CommentId = item.CommentId,
            RawResponse = item.RawResponse,
            ParsedJson = System.Text.Json.JsonSerializer.Serialize(item.Parsed, JsonOptions),
            TextSource = item.TextSource,
            AttachmentsExtracted = item.AttachmentsExtracted,
        }).ToList(),
        ThemeGroups = run.Grouped.ThemeGroups.Select((item, index) => new ThemeGroupDocument
        {
            Position = index,
            GroupName = item.GroupName,
            GroupDescription = item.GroupDescription,
            Count = item.Count,
            SubmissionNumbers = item.SubmissionNumbers.ToList(),
            StanceDistribution = new Dictionary<string, int>(item.StanceDistribution),
            CommonArguments = item.CommonArguments.ToList(),
        }).ToList(),
        FollowUpHistory = run.FollowUpHistory.Select((item, index) => new FollowUpTurnDocument
        {
            Position = index,
            Role = item.Role,
            Text = item.Text,
            At = item.At,
        }).ToList(),
    };

    private static AnalysisRun ToAnalysisRun(AnalysisRunDocument document)
    {
        if (document.SchemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Cosmos analysis schema version {document.SchemaVersion} is newer than supported version {CurrentSchemaVersion}.");

        var run = new AnalysisRun
        {
            SessionName = document.SessionName,
            DocumentId = document.DocumentId,
            StartedAt = document.StartedAt,
            CompletedAt = document.CompletedAt,
            BatchSize = document.BatchSize,
            TotalComments = document.TotalComments,
            Succeeded = document.Succeeded,
            ErrorMessage = document.ErrorMessage,
            FollowUpThreadId = document.FollowUpThreadId,
            Grouped = new GroupedAnalysis
            {
                OverallSummary = document.OverallSummary,
                OverallSentiment = document.OverallSentiment,
                Patterns = document.Patterns,
                Recommendations = document.Recommendations,
                ParsedSuccessfully = document.ThemeGroups.Count > 0,
            },
        };

        run.Categorizations.AddRange(document.Categorizations
            .OrderBy(item => item.SubmissionNumber)
            .Select(item => new CategorizationResult
            {
                SubmissionNumber = item.SubmissionNumber,
                CommentId = item.CommentId,
                RawResponse = item.RawResponse,
                Parsed = Deserialize<Dictionary<string, object?>>(item.ParsedJson) ?? new(),
                TextSource = item.TextSource,
                AttachmentsExtracted = item.AttachmentsExtracted,
            }));

        run.Grouped.ThemeGroups.AddRange(document.ThemeGroups
            .OrderBy(item => item.Position)
            .Select(item => new ThemeGroup
            {
                GroupName = item.GroupName,
                GroupDescription = item.GroupDescription,
                Count = item.Count,
                SubmissionNumbers = item.SubmissionNumbers,
                StanceDistribution = item.StanceDistribution,
                CommonArguments = item.CommonArguments,
            }));

        run.FollowUpHistory.AddRange(document.FollowUpHistory
            .OrderBy(item => item.Position)
            .Select(item => new FollowUpTurn(item.Role, item.Text, item.At)));
        return run;
    }

    internal static AnalysisRun RoundTripForTesting(AnalysisRun run, int? schemaVersion = null)
    {
        var document = ToDocument(Guid.NewGuid(), run);
        if (schemaVersion.HasValue) document.SchemaVersion = schemaVersion.Value;
        return ToAnalysisRun(document);
    }

    internal static string NormalizeDocumentIdForTesting(string? documentId) =>
        AnalysisDocumentIds.Normalize(documentId);

    private async Task RenameAndSyncSummaryAsync(
        Guid runId,
        string? sessionName,
        CancellationToken ct)
    {
        await UpdateAsync(runId, document => document.SessionName = sessionName, ct).ConfigureAwait(false);
        if (!_hasDedicatedSummaries) return;

        var key = runId.ToString("D");
        try
        {
            var response = await _container.ReadItemAsync<AnalysisRunDocument>(
                key, new PartitionKey(key), cancellationToken: ct).ConfigureAwait(false);
            LogRequestCharge("read-summary-source", response.RequestCharge);
            await TryUpsertSummaryAsync(response.Resource, CancellationToken.None).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private async Task TryUpsertSummaryAsync(AnalysisRunDocument document, CancellationToken ct)
    {
        if (!_hasDedicatedSummaries) return;
        try
        {
            var summary = ToSummaryDocument(document);
            var response = await _summaryContainer.UpsertItemAsync(
                summary,
                new PartitionKey(summary.DocumentIdNormalized),
                new ItemRequestOptions { EnableContentResponseOnWrite = false },
                ct).ConfigureAwait(false);
            LogRequestCharge("upsert-summary", response.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Cosmos summary container is unavailable; run {RunId} remains stored in the aggregate container.",
                document.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to update the derived Cosmos summary for run {RunId}; the aggregate remains stored.",
                document.Id);
            await TryInvalidateSummaryBackfillMarkerAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureSummaryBackfillAsync(CancellationToken ct)
    {
        await SummaryBackfillGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var leaseUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            SummaryBackfillMarkerDocument marker;
            try
            {
                var markerResponse = await _summaryContainer.ReadItemAsync<SummaryBackfillMarkerDocument>(
                    SummaryBackfillMarkerId,
                    new PartitionKey(SystemPartitionKey),
                    cancellationToken: ct).ConfigureAwait(false);
                LogRequestCharge("read-summary-backfill-marker", markerResponse.RequestCharge);
                marker = markerResponse.Resource;
                if (marker.CompletedAt.HasValue
                    || marker.State.Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (marker.LeaseExpiresAt > DateTimeOffset.UtcNow)
                    return false;

                marker.State = "inProgress";
                marker.LeaseExpiresAt = leaseUntil;
                marker.CompletedAt = null;
                try
                {
                    var takeover = await _summaryContainer.ReplaceItemAsync(
                        marker,
                        SummaryBackfillMarkerId,
                        new PartitionKey(SystemPartitionKey),
                        new ItemRequestOptions
                        {
                            IfMatchEtag = markerResponse.ETag,
                            EnableContentResponseOnWrite = false,
                        },
                        ct).ConfigureAwait(false);
                    LogRequestCharge("take-over-summary-backfill-lease", takeover.RequestCharge);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    return false;
                }
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                marker = new SummaryBackfillMarkerDocument
                {
                    State = "inProgress",
                    LeaseExpiresAt = leaseUntil,
                };
                try
                {
                    var lease = await _summaryContainer.CreateItemAsync(
                        marker,
                        new PartitionKey(SystemPartitionKey),
                        new ItemRequestOptions { EnableContentResponseOnWrite = false },
                        ct).ConfigureAwait(false);
                    LogRequestCharge("acquire-summary-backfill-lease", lease.RequestCharge);
                }
                catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
                {
                    return false;
                }
            }

            try
            {
                var query = new QueryDefinition("""
                    SELECT
                        c.id,
                        c.sessionName,
                        c.documentId,
                        c.documentIdNormalized,
                        c.startedAt,
                        c.completedAt,
                        c.totalComments,
                        ARRAY_LENGTH(c.themeGroups) AS themeCount,
                        c.succeeded,
                        c.errorMessage,
                        c.overallSentiment
                    FROM c
                    WHERE c.type = @type
                    """)
                    .WithParameter("@type", DocumentType);
                using var iterator = _container.GetItemQueryIterator<RunSummaryDocument>(
                    query,
                    requestOptions: new QueryRequestOptions { MaxItemCount = 100 });

                var migrated = 0;
                while (iterator.HasMoreResults)
                {
                    var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    LogRequestCharge("backfill-summary-source", page.RequestCharge);
                    foreach (var item in page)
                    {
                        item.Type = SummaryDocumentType;
                        item.SchemaVersion = CurrentSchemaVersion;
                        item.DocumentIdNormalized = AnalysisDocumentIds.Normalize(item.DocumentId);
                        var write = await _summaryContainer.UpsertItemAsync(
                            item,
                            new PartitionKey(item.DocumentIdNormalized),
                            new ItemRequestOptions { EnableContentResponseOnWrite = false },
                            ct).ConfigureAwait(false);
                        LogRequestCharge("backfill-summary-write", write.RequestCharge);
                        migrated++;
                    }
                }

                marker.State = "completed";
                marker.CompletedAt = DateTimeOffset.UtcNow;
                marker.LeaseExpiresAt = null;
                var markerWrite = await _summaryContainer.UpsertItemAsync(
                    marker,
                    new PartitionKey(SystemPartitionKey),
                    new ItemRequestOptions { EnableContentResponseOnWrite = false },
                    ct).ConfigureAwait(false);
                LogRequestCharge("write-summary-backfill-marker", markerWrite.RequestCharge);
                _logger.LogInformation("Backfilled {Count} Cosmos analysis summary documents.", migrated);
                return true;
            }
            catch
            {
                marker.State = "failed";
                marker.LeaseExpiresAt = DateTimeOffset.UtcNow;
                try
                {
                    await _summaryContainer.UpsertItemAsync(
                        marker,
                        new PartitionKey(SystemPartitionKey),
                        new ItemRequestOptions { EnableContentResponseOnWrite = false },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
                throw;
            }
        }
        finally
        {
            SummaryBackfillGate.Release();
        }
    }

    private async Task TryInvalidateSummaryBackfillMarkerAsync()
    {
        try
        {
            await _summaryContainer.DeleteItemAsync<SummaryBackfillMarkerDocument>(
                SummaryBackfillMarkerId,
                new PartitionKey(SystemPartitionKey),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not invalidate the Cosmos summary backfill marker.");
        }
    }

    private async Task<string?> OffloadPayloadIfNeededAsync(
        Guid runId,
        AnalysisRunDocument document,
        CancellationToken ct)
    {
        var payloadBytes = document.Categorizations.Sum(item =>
            Encoding.UTF8.GetByteCount(item.RawResponse)
            + Encoding.UTF8.GetByteCount(item.ParsedJson));
        if (payloadBytes < _payloadOptions.OffloadThresholdBytes)
            return null;

        if (!_payloadStore.IsConfigured)
        {
            _logger.LogWarning(
                "Cosmos run {RunId} contains {PayloadBytes} bytes of inline AI payload; configure Persistence:Payloads to offload large content.",
                runId,
                payloadBytes);
            return null;
        }

        var payload = new AnalysisRunPayload
        {
            Categorizations = document.Categorizations
                .Select(item => new CategorizationPayload(
                    item.SubmissionNumber,
                    item.RawResponse,
                    item.ParsedJson))
                .ToList(),
        };
        var blobName = await _payloadStore.SaveAsync(runId, payload, ct).ConfigureAwait(false);
        foreach (var categorization in document.Categorizations)
        {
            categorization.RawResponse = string.Empty;
            categorization.ParsedJson = "{}";
        }
        document.PayloadBlobName = blobName;
        return blobName;
    }

    private async Task HydratePayloadAsync(AnalysisRunDocument document, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(document.PayloadBlobName)) return;
        if (!_payloadStore.IsConfigured)
            throw new InvalidOperationException(
                "This analysis run stores its large payload in Blob Storage, but Persistence:Payloads is not configured.");

        var payload = await _payloadStore.LoadAsync(document.PayloadBlobName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The Blob payload for analysis run {document.Id} could not be found.");
        if (payload.SchemaVersion > 1)
            throw new NotSupportedException(
                $"Analysis payload schema version {payload.SchemaVersion} is not supported.");

        var bySubmission = payload.Categorizations.ToDictionary(item => item.SubmissionNumber);
        foreach (var categorization in document.Categorizations)
        {
            if (!bySubmission.TryGetValue(categorization.SubmissionNumber, out var stored))
                throw new InvalidOperationException(
                    $"Blob payload for run {document.Id} is missing submission {categorization.SubmissionNumber}.");
            categorization.RawResponse = stored.RawResponse;
            categorization.ParsedJson = stored.ParsedJson;
        }
    }

    private async Task TryDeletePayloadAsync(string? blobName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blobName) || !_payloadStore.IsConfigured) return;
        try
        {
            await _payloadStore.DeleteAsync(blobName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete analysis payload blob {BlobName}.", blobName);
        }
    }

    private async Task DeletePayloadAsync(string? blobName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blobName)) return;
        if (!_payloadStore.IsConfigured)
            throw new InvalidOperationException(
                "Cannot delete this analysis run because its Blob payload store is not configured.");
        await _payloadStore.DeleteAsync(blobName, ct).ConfigureAwait(false);
    }

    private static RunSummaryDocument ToSummaryDocument(AnalysisRunDocument document) => new()
    {
        Id = document.Id,
        Type = SummaryDocumentType,
        SchemaVersion = CurrentSchemaVersion,
        SessionName = document.SessionName,
        DocumentId = document.DocumentId,
        DocumentIdNormalized = document.DocumentIdNormalized,
        StartedAt = document.StartedAt,
        CompletedAt = document.CompletedAt,
        TotalComments = document.TotalComments,
        ThemeCount = document.ThemeGroups.Count,
        Succeeded = document.Succeeded,
        ErrorMessage = document.ErrorMessage,
        OverallSentiment = document.OverallSentiment,
    };

    private void LogRequestCharge(string operation, double requestCharge)
    {
        _telemetry.RecordCosmosRequestCharge(operation, requestCharge);
        _logger.LogDebug("Cosmos {Operation} consumed {RequestCharge:F2} RU.", operation, requestCharge);
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (System.Text.Json.JsonException) { return default; }
    }

    private sealed class AnalysisRunDocument
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("type")] public string Type { get; set; } = DocumentType;
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("sessionName")] public string? SessionName { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; } = string.Empty;
        [JsonProperty("documentIdNormalized")] public string DocumentIdNormalized { get; set; } = string.Empty;
        [JsonProperty("startedAt")] public DateTimeOffset StartedAt { get; set; }
        [JsonProperty("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
        [JsonProperty("batchSize")] public int BatchSize { get; set; }
        [JsonProperty("totalComments")] public int TotalComments { get; set; }
        [JsonProperty("succeeded")] public bool Succeeded { get; set; }
        [JsonProperty("errorMessage")] public string? ErrorMessage { get; set; }
        [JsonProperty("overallSummary")] public string? OverallSummary { get; set; }
        [JsonProperty("overallSentiment")] public string? OverallSentiment { get; set; }
        [JsonProperty("patterns")] public List<string> Patterns { get; set; } = new();
        [JsonProperty("recommendations")] public List<string> Recommendations { get; set; } = new();
        [JsonProperty("followUpThreadId")] public string? FollowUpThreadId { get; set; }
        [JsonProperty("payloadBlobName")] public string? PayloadBlobName { get; set; }
        [JsonProperty("categorizations")] public List<CategorizationDocument> Categorizations { get; set; } = new();
        [JsonProperty("themeGroups")] public List<ThemeGroupDocument> ThemeGroups { get; set; } = new();
        [JsonProperty("followUpHistory")] public List<FollowUpTurnDocument> FollowUpHistory { get; set; } = new();
    }

    private sealed class CategorizationDocument
    {
        [JsonProperty("submissionNumber")] public int SubmissionNumber { get; set; }
        [JsonProperty("commentId")] public string CommentId { get; set; } = string.Empty;
        [JsonProperty("rawResponse")] public string RawResponse { get; set; } = string.Empty;
        [JsonProperty("parsedJson")] public string ParsedJson { get; set; } = "{}";
        [JsonProperty("textSource")] public string TextSource { get; set; } = "inline";
        [JsonProperty("attachmentsExtracted")] public int AttachmentsExtracted { get; set; }
    }

    private sealed class ThemeGroupDocument
    {
        [JsonProperty("position")] public int Position { get; set; }
        [JsonProperty("groupName")] public string GroupName { get; set; } = string.Empty;
        [JsonProperty("groupDescription")] public string GroupDescription { get; set; } = string.Empty;
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("submissionNumbers")] public List<int> SubmissionNumbers { get; set; } = new();
        [JsonProperty("stanceDistribution")] public Dictionary<string, int> StanceDistribution { get; set; } = new();
        [JsonProperty("commonArguments")] public List<string> CommonArguments { get; set; } = new();
    }

    private sealed class FollowUpTurnDocument
    {
        [JsonProperty("position")] public int Position { get; set; }
        [JsonProperty("role")] public string Role { get; set; } = string.Empty;
        [JsonProperty("text")] public string Text { get; set; } = string.Empty;
        [JsonProperty("at")] public DateTimeOffset At { get; set; }
    }

    private sealed class RunSummaryDocument
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("type")] public string Type { get; set; } = SummaryDocumentType;
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("sessionName")] public string? SessionName { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; } = string.Empty;
        [JsonProperty("documentIdNormalized")] public string DocumentIdNormalized { get; set; } = string.Empty;
        [JsonProperty("startedAt")] public DateTimeOffset StartedAt { get; set; }
        [JsonProperty("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
        [JsonProperty("totalComments")] public int TotalComments { get; set; }
        [JsonProperty("themeCount")] public int ThemeCount { get; set; }
        [JsonProperty("succeeded")] public bool Succeeded { get; set; }
        [JsonProperty("errorMessage")] public string? ErrorMessage { get; set; }
        [JsonProperty("overallSentiment")] public string? OverallSentiment { get; set; }
    }

    private sealed class SummaryBackfillMarkerDocument
    {
        [JsonProperty("id")] public string Id { get; set; } = SummaryBackfillMarkerId;
        [JsonProperty("type")] public string Type { get; set; } = "migrationMarker";
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonProperty("documentIdNormalized")] public string DocumentIdNormalized { get; set; } = SystemPartitionKey;
        [JsonProperty("state")] public string State { get; set; } = "inProgress";
        [JsonProperty("leaseExpiresAt")] public DateTimeOffset? LeaseExpiresAt { get; set; }
        [JsonProperty("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
    }
}

internal enum CosmosPageSource
{
    Aggregate,
    Summary,
}

internal sealed record CosmosDecodedContinuationToken(CosmosPageSource? Source, string? Token);

internal static class CosmosContinuationTokenCodec
{
    private const string Prefix = "cosmos-v1:";

    public static CosmosDecodedContinuationToken Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(null, null);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return new(CosmosPageSource.Aggregate, value);

        var separator = value.IndexOf(':', Prefix.Length);
        if (separator < 0)
            throw new ArgumentException("The Cosmos continuation token is invalid.", nameof(value));
        var source = value[Prefix.Length..separator] switch
        {
            "aggregate" => CosmosPageSource.Aggregate,
            "summary" => CosmosPageSource.Summary,
            _ => throw new ArgumentException("The Cosmos continuation token source is invalid.", nameof(value)),
        };
        try
        {
            var token = Encoding.UTF8.GetString(Convert.FromBase64String(value[(separator + 1)..]));
            return new(source, token);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Cosmos continuation token is invalid.", nameof(value), ex);
        }
    }

    public static AnalysisPage<AnalysisRunSummary> EncodePage(
        AnalysisPage<AnalysisRunSummary> page,
        CosmosPageSource source)
    {
        if (!page.HasMore) return page;
        var sourceName = source == CosmosPageSource.Aggregate ? "aggregate" : "summary";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(page.ContinuationToken!));
        return page with { ContinuationToken = $"{Prefix}{sourceName}:{encoded}" };
    }
}

internal enum CosmosUpdateAttemptResult
{
    Updated,
    Missing,
    Conflict,
}

internal static class CosmosOptimisticConcurrency
{
    public static async Task ExecuteAsync(
        Func<int, Task<CosmosUpdateAttemptResult>> tryUpdate,
        int maxAttempts,
        Action<int>? onRetry = null,
        Func<Exception>? exhaustedException = null)
    {
        ArgumentNullException.ThrowIfNull(tryUpdate);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = await tryUpdate(attempt).ConfigureAwait(false);
            if (result is CosmosUpdateAttemptResult.Updated or CosmosUpdateAttemptResult.Missing)
                return;
            if (attempt < maxAttempts) onRetry?.Invoke(attempt);
        }

        throw exhaustedException?.Invoke()
            ?? new InvalidOperationException($"Cosmos update failed after {maxAttempts} concurrent changes.");
    }
}