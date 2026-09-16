using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DoedRegulatoryComments.Web.Services;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";
    public bool Enabled { get; set; }
    public int PollIntervalMinutes { get; set; } = 60;
    public int MaxAutoAnalysisComments { get; set; } = 100;
}

public sealed class DocketWatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DocketId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool AutoAnalyze { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public Guid? LastRunId { get; set; }
    public string? LastError { get; set; }
    public Dictionary<string, string> Baseline { get; set; } = new();
    public List<string> PendingIds { get; set; } = new();
}

public sealed record WatchItem(DocketWatch Watch, string Version);

public sealed class DocketNotification
{
    public string Id { get; set; } = "";
    public Guid WatchId { get; set; }
    public string DocketId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public Guid? RunId { get; set; }
    public List<string> CommentIds { get; set; } = new();
}

public interface IDocketCommentSource
{
    Task<FetchCommentsResult> FetchAsync(string docketId, CancellationToken ct);
}

public sealed class RegulationsDocketSource(RegulationsGovClient client) : IDocketCommentSource
{
    public Task<FetchCommentsResult> FetchAsync(string docketId, CancellationToken ct) =>
        client.FetchCommentsAsync(new FetchCommentsRequest { DocumentId = docketId, UseDocketFilter = true, PageSize = 250 }, ct);
}

public sealed class DocketMonitorService(
    IWorkspaceRepository workspace, IDocketCommentSource source, IAnalysisRunner runner,
    IAnalysisRepository analyses, ApiSettingsStore settings, IOptions<MonitoringOptions> options,
    TimeProvider clock, ILogger<DocketMonitorService> logger)
{
    public const string WatchKind = "watches";
    public const string NotificationKind = "notifications";
    public const string LeaseKind = "watchLeases";
    private static readonly Regex DocketIdPattern = new(@"^[A-Z0-9][A-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private sealed record Lease(string Owner, DateTimeOffset ExpiresAt);
    private sealed record NotificationPointer(string NotificationId);

    public async Task<AnalysisPage<WorkspaceItem>> ListNotificationsAsync(string? token = null, CancellationToken ct = default)
    {
        var page = await workspace.ListPageAsync("notificationOrder", 50, token, ct);
        var items = new List<WorkspaceItem>();
        foreach (var index in page.Items)
        {
            var id = WorkspaceJson.Read<NotificationPointer>(index).NotificationId;
            items.Add(await workspace.GetAsync(NotificationKind, id, ct)
                ?? throw new InvalidOperationException("A notification index references a missing record."));
        }
        return new(items, page.ContinuationToken);
    }

    public async Task<IReadOnlyList<WatchItem>> ListAsync(CancellationToken ct = default)
    {
        var watches = new List<WatchItem>();
        string? token = null;
        do
        {
            var page = await workspace.ListPageAsync(WatchKind, 100, token, ct);
            watches.AddRange(page.Items.Select(i => new WatchItem(WorkspaceJson.Read<DocketWatch>(i), i.Version)));
            token = page.ContinuationToken;
        } while (token is not null);
        return watches.OrderBy(x => x.Watch.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<WatchItem> SaveAsync(DocketWatch input, string? version, CancellationToken ct = default)
    {
        var docket = AnalysisDocumentIds.Normalize(input.DocketId);
        if (!DocketIdPattern.IsMatch(docket)) throw new ArgumentException("Enter a valid docket ID (letters, numbers, periods, underscores and hyphens; at most 64 characters).");
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 120)
            throw new ArgumentException("Enter a watch name of 1 to 120 characters.");
        var existing = await workspace.GetAsync(WatchKind, input.Id.ToString("D"), ct);
        if (existing?.Version != version) throw new WorkspaceConflictException();
        var watch = existing is null ? new DocketWatch { Id = input.Id, DocketId = docket } : WorkspaceJson.Read<DocketWatch>(existing);
        if (watch.DocketId != docket) throw new ArgumentException("Create a new watch to follow a different docket; existing baselines cannot be reassigned.");
        watch.Name = input.Name.Trim();
        watch.DeadlineAt = input.DeadlineAt?.ToUniversalTime();
        watch.Enabled = input.Enabled;
        watch.AutoAnalyze = input.AutoAnalyze;
        var saved = await workspace.SaveAsync(WatchKind, watch.Id.ToString("D"), WorkspaceJson.Write(watch), version, ct);
        return new(watch, saved.Version);
    }

    public Task DeleteAsync(Guid id, string version, CancellationToken ct = default) =>
        workspace.DeleteAsync(WatchKind, id.ToString("D"), version, ct);

    public static Dictionary<string, string> Fingerprints(IEnumerable<CommentResource> comments) =>
        comments.ToDictionary(c => c.Id, c => Hash(WorkspaceJson.Write(new
        {
            c.Attributes.ModifyDate, c.Attributes.PostedDate, c.Attributes.Comment,
            c.Attributes.Title, c.Attributes.Organization, c.Attributes.FirstName, c.Attributes.LastName,
        })), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Changes(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> current) =>
        current.Where(p => !baseline.TryGetValue(p.Key, out var prior) || prior != p.Value).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();

    public async Task CheckAsync(Guid id, CancellationToken ct = default)
    {
        var key = id.ToString("D");
        var owner = Guid.NewGuid().ToString("N");
        var leaseItem = await workspace.GetAsync(LeaseKind, key, ct);
        if (leaseItem is not null && WorkspaceJson.Read<Lease>(leaseItem).ExpiresAt > clock.GetUtcNow())
            throw new InvalidOperationException("This docket is already being checked by another worker. Try again after it finishes.");
        try
        {
            await workspace.SaveAsync(LeaseKind, key, WorkspaceJson.Write(new Lease(owner, clock.GetUtcNow().AddMinutes(15))), leaseItem?.Version, ct);
        }
        catch (WorkspaceConflictException)
        {
            throw new InvalidOperationException("Another worker started checking this docket.");
        }
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = RenewAsync(key, owner, leaseLost, renewalStop.Token);
        try
        {
            var item = await workspace.GetAsync(WatchKind, key, leaseLost.Token)
                ?? throw new InvalidOperationException("The watch no longer exists.");
            var watch = WorkspaceJson.Read<DocketWatch>(item);
            await ReminderAsync(watch, leaseLost.Token);
            var result = await source.FetchAsync(watch.DocketId, leaseLost.Token);
            leaseLost.Token.ThrowIfCancellationRequested();
            if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "Docket retrieval failed.");
            if (result.TotalElements is { } total && result.Comments.Count != total)
                throw new InvalidOperationException($"Incomplete docket retrieval: received {result.Comments.Count} of {total} comments. The baseline was not advanced.");
            var fingerprints = Fingerprints(result.Comments);
            var changed = watch.LastCheckedAt.HasValue ? Changes(watch.Baseline, fingerprints) : Array.Empty<string>();
            if (changed.Count > 0)
            {
                var added = changed.Count(comment => !watch.Baseline.ContainsKey(comment));
                await NotifyAsync(watch, "changes", Hash(WorkspaceJson.Write(fingerprints.OrderBy(p => p.Key))),
                    $"{added} new and {changed.Count - added} modified submissions",
                    "Changes reflect API metadata and available inline text, not proof of a changed policy position. " +
                    (changed.Count > 1000 ? $"Only the first 1,000 of {changed.Count} IDs are listed. " : "") +
                    "Attachment-only changes with unchanged metadata may not be detected.", changed.Take(1000).ToList(), null, leaseLost.Token);
            }
            watch = await UpdateAsync(id, current =>
            {
                current.Baseline = fingerprints;
                current.LastCheckedAt = clock.GetUtcNow();
                current.LastError = null;
                // Keep all unprocessed IDs, including changes from failed prior analysis.
                current.PendingIds = current.PendingIds.Concat(changed).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(fingerprints.ContainsKey).ToList();
            }, leaseLost.Token);

            if (watch.AutoAnalyze && watch.PendingIds.Count > 0)
            {
                var selectedIds = watch.PendingIds.Take(options.Value.MaxAutoAnalysisComments).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected = result.Comments.Where(c => selectedIds.Contains(c.Id)).ToList();
                if (selected.Count == 0) throw new InvalidOperationException("Pending submissions were not present in the retrieved snapshot.");
                var run = await runner.RunAsync(watch.DocketId, selected, WorkspaceJson.Clone(settings.Current), null, leaseLost.Token,
                    new AnalysisInputMetadata
                    {
                        FetchedComments = result.Comments.Count, AvailableComments = result.TotalElements,
                        IsSelection = true, UseDocketFilter = true,
                        Description = "Watchlist change-only selection; not a full-docket analysis.",
                    });
                if (!run.Succeeded) throw new InvalidOperationException(run.ErrorMessage ?? "Automatic analysis failed.");
                var runId = run.PersistedId ?? await analyses.SaveRunAsync(run, leaseLost.Token);
                await NotifyAsync(watch, "analysis", runId.ToString("D"), "Change-only analysis saved",
                    $"Analyzed {selected.Count} pending submissions. Other pending IDs remain queued for a later check. " +
                    "Compare coverage before drawing conclusions about the whole docket.", selectedIds.ToList(), runId, leaseLost.Token);
                await UpdateAsync(id, current =>
                {
                    current.PendingIds.RemoveAll(selectedIds.Contains);
                    current.LastRunId = runId;
                    current.LastError = null;
                }, leaseLost.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ExpectedFailure(ex))
        {
            logger.LogWarning(ex, "Docket check failed for {WatchId}", id);
            var item = await workspace.GetAsync(WatchKind, key, ct);
            if (item is not null)
            {
                var watch = await UpdateAsync(id, current => current.LastError = ex.Message, ct);
                await NotifyAsync(watch, "error", Hash(ex.Message + clock.GetUtcNow().ToString("yyyy-MM-dd")),
                    "Docket check needs attention", ex.Message, [], null, ct);
            }
            throw;
        }
        finally
        {
            await renewalStop.CancelAsync();
            try { await renew; }
            catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
            var current = await workspace.GetAsync(LeaseKind, key, CancellationToken.None);
            if (current is not null && WorkspaceJson.Read<Lease>(current).Owner == owner)
            {
                try { await workspace.DeleteAsync(LeaseKind, key, current.Version, CancellationToken.None); }
                catch (WorkspaceConflictException) { logger.LogInformation("Docket lease changed before release for {WatchId}", id); }
            }
        }
    }

    private async Task RenewAsync(string id, string owner, CancellationTokenSource leaseLost, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(4));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var item = await workspace.GetAsync(LeaseKind, id, ct);
                if (item is null || WorkspaceJson.Read<Lease>(item).Owner != owner) throw new WorkspaceConflictException();
                await workspace.SaveAsync(LeaseKind, id, WorkspaceJson.Write(new Lease(owner, clock.GetUtcNow().AddMinutes(15))), item.Version, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ExpectedFailure(ex))
        {
            logger.LogError(ex, "Docket lease renewal failed");
            await leaseLost.CancelAsync();
        }
    }

    private async Task<DocketWatch> UpdateAsync(Guid id, Action<DocketWatch> update, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var item = await workspace.GetAsync(WatchKind, id.ToString("D"), ct)
                ?? throw new InvalidOperationException("The watch was deleted.");
            var watch = WorkspaceJson.Read<DocketWatch>(item);
            update(watch);
            try
            {
                await workspace.SaveAsync(WatchKind, id.ToString("D"), WorkspaceJson.Write(watch), item.Version, ct);
                return watch;
            }
            catch (WorkspaceConflictException) when (attempt < 2)
            {
                logger.LogInformation("Retrying a concurrent watch update for {WatchId}", id);
            }
        }
    }

    private Task ReminderAsync(DocketWatch watch, CancellationToken ct)
    {
        if (watch.DeadlineAt is not { } deadline) return Task.CompletedTask;
        var remaining = deadline - clock.GetUtcNow();
        var window = remaining <= TimeSpan.Zero ? "passed" : remaining <= TimeSpan.FromDays(1) ? "one-day" :
            remaining <= TimeSpan.FromDays(7) ? "one-week" : null;
        return window is null ? Task.CompletedTask : NotifyAsync(watch, "deadline", $"{deadline:O}:{window}",
            $"Deadline reminder: {watch.Name}", $"Manually entered deadline: {deadline:u}. Verify against the official notice. Window: {window}.", [], null, ct);
    }

    private async Task NotifyAsync(DocketWatch watch, string kind, string eventKey, string title, string body,
        List<string> comments, Guid? runId, CancellationToken ct)
    {
        var id = Hash($"{watch.Id:D}:{kind}:{eventKey}");
        var notification = new DocketNotification
        {
            Id = id, WatchId = watch.Id, DocketId = watch.DocketId, Kind = kind, Title = title, Body = body,
            CommentIds = comments, RunId = runId, CreatedAt = clock.GetUtcNow(),
        };
        var saved = await workspace.GetAsync(NotificationKind, id, ct);
        if (saved is null)
        {
            try { saved = await workspace.SaveAsync(NotificationKind, id, WorkspaceJson.Write(notification), null, ct); }
            catch (WorkspaceConflictException)
            {
                saved = await workspace.GetAsync(NotificationKind, id, ct)
                    ?? throw new InvalidOperationException("A concurrently created notification could not be read.");
            }
        }
        var timestamp = WorkspaceJson.Read<DocketNotification>(saved).CreatedAt.UtcTicks;
        var indexId = $"{long.MaxValue - timestamp:D19}-{id}";
        if (await workspace.GetAsync("notificationOrder", indexId, ct) is null)
        {
            try { await workspace.SaveAsync("notificationOrder", indexId, WorkspaceJson.Write(new NotificationPointer(id)), null, ct); }
            catch (WorkspaceConflictException) { logger.LogInformation("Notification index {NotificationId} was already created", id); }
        }
    }

    public static bool ExpectedFailure(Exception ex) => ex is InvalidOperationException or ArgumentException or IOException
        or HttpRequestException or JsonException or System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException
        or Microsoft.Azure.Cosmos.CosmosException or Azure.RequestFailedException or OperationCanceledException;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class DocketMonitorWorker(IServiceScopeFactory scopes, IOptions<MonitoringOptions> options, ILogger<DocketMonitorWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.PollIntervalMinutes));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<DocketMonitorService>();
                foreach (var item in (await service.ListAsync(stoppingToken)).Where(w => w.Watch.Enabled))
                {
                    try { await service.CheckAsync(item.Watch.Id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (DocketMonitorService.ExpectedFailure(ex))
                    {
                        logger.LogWarning(ex, "Watch {WatchId} needs attention", item.Watch.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (DocketMonitorService.ExpectedFailure(ex))
            {
                logger.LogError(ex, "The docket monitoring pass failed; it will retry on the next interval");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
