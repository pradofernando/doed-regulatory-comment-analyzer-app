namespace DoedRegulatoryComments.Web.Services;

public sealed class NotificationChangeSignal
{
    private readonly object _gate = new();
    private long _version;
    private TaskCompletionSource _changed = NewSignal();

    public long Version => Interlocked.Read(ref _version);

    public Task WhenChanged(long observedVersion)
    {
        lock (_gate)
            return observedVersion == _version ? _changed.Task : Task.CompletedTask;
    }

    public void NotifyChanged()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            previous = _changed;
            _changed = NewSignal();
        }
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class NotificationCountService(
    IServiceScopeFactory scopes, TimeProvider clock, NotificationChangeSignal changes) : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private CountSnapshot? _cached;

    public long Version => changes.Version;
    public Task WhenChanged(long observedVersion) => changes.WhenChanged(observedVersion);

    public async Task<long> GetUnreadCountAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var version = changes.Version;
                if (_cached is { } cached && cached.Version == version && cached.ExpiresAt > clock.GetUtcNow())
                    return cached.Count;

                // One paged count is shared across browser circuits; never count only the inbox's visible page.
                await using var scope = scopes.CreateAsyncScope();
                var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
                long count = 0;
                string? token = null;
                do
                {
                    var page = await workspace.ListPageAsync(
                        DocketMonitorService.NotificationKind, 500, token, ct).ConfigureAwait(false);
                    count += page.Items.LongCount(item => !WorkspaceJson.Read<DocketNotification>(item).ReadAt.HasValue);
                    token = page.ContinuationToken;
                } while (!string.IsNullOrEmpty(token));

                if (changes.Version != version) continue;
                _cached = new CountSnapshot(version, count, clock.GetUtcNow().Add(RefreshInterval));
                return count;
            }
        }
        finally { _refresh.Release(); }
    }

    public void Dispose() => _refresh.Dispose();

    private sealed record CountSnapshot(long Version, long Count, DateTimeOffset ExpiresAt);
}
