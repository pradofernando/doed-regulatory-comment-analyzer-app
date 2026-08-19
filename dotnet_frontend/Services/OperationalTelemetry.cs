using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace DoedRegulatoryComments.Web.Services;

public sealed class FoundryCostOptions
{
    public const string SectionName = "Telemetry:FoundryCost";

    public decimal InputUsdPerMillionTokens { get; set; }
    public decimal OutputUsdPerMillionTokens { get; set; }
}

public sealed class OperationalTelemetry
{
    public const string InstrumentationName = "DoedRegulatoryComments.Web";

    private readonly FoundryCostOptions _costOptions;
    private readonly Counter<long> _analysisJobs;
    private readonly Histogram<double> _analysisDuration;
    private readonly Histogram<double> _phaseDuration;
    private readonly Counter<long> _foundryTokens;
    private readonly Histogram<double> _foundryCost;
    private readonly Histogram<double> _foundryDuration;
    private readonly Counter<long> _foundryRetries;
    private readonly Counter<long> _foundryRateLimits;
    private readonly Counter<long> _attachmentFailures;
    private readonly Histogram<long> _attachmentBytes;
    private readonly Counter<long> _attachmentOcr;
    private readonly Histogram<double> _cosmosRequestCharge;

    public OperationalTelemetry(IOptions<FoundryCostOptions> costOptions)
    {
        _costOptions = costOptions.Value;
        _analysisJobs = Diagnostics.Meter.CreateCounter<long>("analysis.jobs", unit: "{job}");
        _analysisDuration = Diagnostics.Meter.CreateHistogram<double>("analysis.duration", unit: "s");
        _phaseDuration = Diagnostics.Meter.CreateHistogram<double>("analysis.phase.duration", unit: "s");
        _foundryTokens = Diagnostics.Meter.CreateCounter<long>("foundry.tokens", unit: "{token}");
        _foundryCost = Diagnostics.Meter.CreateHistogram<double>("foundry.estimated_cost", unit: "USD");
        _foundryDuration = Diagnostics.Meter.CreateHistogram<double>("foundry.request.duration", unit: "s");
        _foundryRetries = Diagnostics.Meter.CreateCounter<long>("foundry.retries", unit: "{retry}");
        _foundryRateLimits = Diagnostics.Meter.CreateCounter<long>("foundry.rate_limits", unit: "{response}");
        _attachmentFailures = Diagnostics.Meter.CreateCounter<long>("attachments.failures", unit: "{attachment}");
        _attachmentBytes = Diagnostics.Meter.CreateHistogram<long>("attachments.download.size", unit: "By");
        _attachmentOcr = Diagnostics.Meter.CreateCounter<long>("attachments.ocr", unit: "{attachment}");
        _cosmosRequestCharge = Diagnostics.Meter.CreateHistogram<double>("cosmos.request_charge", unit: "RU");
    }

    public Activity? StartAnalysis(Guid jobId, int commentCount)
    {
        var activity = Diagnostics.ActivitySource.StartActivity("analysis.run", ActivityKind.Internal);
        activity?.SetTag("analysis.job.id", jobId.ToString("D"));
        activity?.SetTag("analysis.comment.count", commentCount);
        _analysisJobs.Add(1, new KeyValuePair<string, object?>("state", "started"));
        return activity;
    }

    public void RecordAnalysisCompleted(AnalysisJobState state, TimeSpan duration)
    {
        var outcome = state.ToString().ToLowerInvariant();
        _analysisJobs.Add(1, new KeyValuePair<string, object?>("state", outcome));
        _analysisDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("state", outcome));
    }

    public void RecordPhaseDuration(string? phase, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(phase)) return;
        _phaseDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("phase", NormalizeTag(phase)));
    }

    public void RecordFoundryUsage(
        string operation,
        long inputTokens,
        long outputTokens,
        TimeSpan duration)
    {
        var operationTag = new KeyValuePair<string, object?>("operation", operation);
        _foundryTokens.Add(inputTokens, operationTag,
            new KeyValuePair<string, object?>("direction", "input"));
        _foundryTokens.Add(outputTokens, operationTag,
            new KeyValuePair<string, object?>("direction", "output"));
        _foundryDuration.Record(duration.TotalSeconds, operationTag,
            new KeyValuePair<string, object?>("outcome", "success"));
        _foundryCost.Record((double)EstimateCostUsd(inputTokens, outputTokens, _costOptions), operationTag);
    }

    public void RecordFoundryFailure(string operation, TimeSpan duration) =>
        _foundryDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", "failure"));

    public void RecordFoundryRetry(string operation) =>
        _foundryRetries.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void RecordFoundryRateLimit(string operation) =>
        _foundryRateLimits.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void RecordAttachmentDownload(AttachmentFileKind fileKind, long bytes) =>
        _attachmentBytes.Record(bytes,
            new KeyValuePair<string, object?>("format", fileKind.ToString().ToLowerInvariant()));

    public void RecordAttachmentFailure(string reason, string? format = null) =>
        _attachmentFailures.Add(1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("format", NormalizeAttachmentFormat(format)));

    public void RecordAttachmentOcr(bool succeeded) =>
        _attachmentOcr.Add(1,
            new KeyValuePair<string, object?>("outcome", succeeded ? "success" : "failure"));

    public void RecordCosmosRequestCharge(string operation, double requestCharge) =>
        _cosmosRequestCharge.Record(requestCharge,
            new KeyValuePair<string, object?>("operation", operation));

    internal static decimal EstimateCostUsd(
        long inputTokens,
        long outputTokens,
        FoundryCostOptions options) =>
        (inputTokens * options.InputUsdPerMillionTokens
            + outputTokens * options.OutputUsdPerMillionTokens) / 1_000_000m;

    private static string NormalizeTag(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string NormalizeAttachmentFormat(string? value)
    {
        var format = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (format is "pdf" or "application/pdf") return "pdf";
        if (format is "doc" or "docx" or "msw12" or "application/msword") return "word";
        return "unknown";
    }
}

internal static class Diagnostics
{
    internal static readonly ActivitySource ActivitySource = new(OperationalTelemetry.InstrumentationName);
    internal static readonly Meter Meter = new(OperationalTelemetry.InstrumentationName);
}