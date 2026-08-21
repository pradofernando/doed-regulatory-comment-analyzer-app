using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DoedRegulatoryComments.Web.Services;

public sealed class FunctionAnalysisOptions
{
    public const string SectionName = "AnalysisBackend";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string FunctionKey { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 2;
    public int TimeoutMinutes { get; set; } = 90;
}

public sealed class FunctionAnalysisRunner : IAnalysisRunner
{
    private readonly HttpClient _client;
    private readonly IAnalysisRepository _repository;
    private readonly FunctionAnalysisOptions _options;

    public FunctionAnalysisRunner(
        HttpClient client,
        IAnalysisRepository repository,
        IOptions<FunctionAnalysisOptions> options)
    {
        _client = client;
        _repository = repository;
        _options = options.Value;
    }

    public async Task<AnalysisRun> RunAsync(
        string documentId,
        IReadOnlyList<CommentResource> comments,
        ApiSettings settings,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (comments.Count == 0)
            throw new InvalidOperationException("No comments were selected for analysis.");

        progress?.Report(new AnalysisProgress
        {
            Phase = "Submitting",
            Current = 0,
            Total = comments.Count,
            Message = "Queuing analysis with the Function App...",
        });

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "api/analysis-runs")
        {
            Content = JsonContent.Create(new
            {
                documentId,
                commentIds = comments.Select(comment => comment.Id).ToArray(),
                maxComments = comments.Count,
                batchSize = settings.BatchSize,
                models = new
                {
                    categorization = settings.ModelDeploymentName,
                    grouping = settings.ModelDeploymentName,
                    validation = settings.ModelDeploymentName,
                },
                runValidation = settings.RunValidation,
            }),
        };
        AddFunctionKey(submitRequest);

        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken)
            .ConfigureAwait(false);
        var submission = await submitResponse.Content.ReadFromJsonAsync<RunSubmission>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!submitResponse.IsSuccessStatusCode || submission is null)
        {
            var detail = await submitResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The Function App rejected the analysis request ({(int)submitResponse.StatusCode}): {detail}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(_options.TimeoutMinutes));
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), timeout.Token)
                .ConfigureAwait(false);

            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/analysis-runs/{submission.RunId:D}");
            AddFunctionKey(statusRequest);
            using var statusResponse = await _client.SendAsync(statusRequest, timeout.Token)
                .ConfigureAwait(false);
            statusResponse.EnsureSuccessStatusCode();
            var status = await statusResponse.Content.ReadFromJsonAsync<RunStatus>(
                cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Function App returned an empty run status.");

            progress?.Report(new AnalysisProgress
            {
                Phase = status.Status,
                Current = status.Status == "succeeded" ? comments.Count : 0,
                Total = comments.Count,
                Message = status.Status switch
                {
                    "queued" => "Waiting for an analysis worker...",
                    "running" => "The Function App is analyzing the selected comments...",
                    _ => $"Analysis {status.Status}.",
                },
            });

            if (status.Status == "failed")
                throw new InvalidOperationException(status.ErrorMessage ?? "The Function analysis failed.");
            if (status.Status != "succeeded") continue;

            var run = await _repository.LoadRunAsync(submission.RunId, timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Analysis {submission.RunId:D} completed but its Cosmos result could not be loaded.");
            run.PersistedId = submission.RunId;
            return run;
        }
    }

    private void AddFunctionKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.FunctionKey))
            request.Headers.Add("x-functions-key", _options.FunctionKey);
    }

    private sealed record RunSubmission(Guid RunId, string Status);
    private sealed record RunStatus(Guid RunId, string Status, string? ErrorMessage);
}