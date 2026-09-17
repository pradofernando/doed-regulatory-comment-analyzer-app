using DoedRegulatoryComments.Web.Components;
using DoedRegulatoryComments.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DoedRegulatoryComments.Web.Tests;

public sealed class NotificationCountTests
{
    [Fact]
    public async Task Count_IncludesUnreadNotificationsBeyondTheFirstPage()
    {
        var repository = new FakeWorkspace();
        repository.Items.AddRange(Enumerable.Range(0, 502).Select(i => Notification(i, read: i < 500)));
        using var services = Services(repository);

        var count = await services.GetRequiredService<NotificationCountService>().GetUnreadCountAsync();

        Assert.Equal(2, count);
        Assert.Equal(2, repository.PageCalls);
        Assert.All(repository.Kinds, kind => Assert.Equal(DocketMonitorService.NotificationKind, kind));
    }

    [Fact]
    public async Task ConcurrentReaders_ShareOneCachedCount_AndWritesInvalidateIt()
    {
        var repository = new FakeWorkspace { Items = [Notification(1), Notification(2, read: true)] };
        using var services = Services(repository);
        var counts = services.GetRequiredService<NotificationCountService>();
        var changes = services.GetRequiredService<NotificationChangeSignal>();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => counts.GetUnreadCountAsync()));
        Assert.All(results, value => Assert.Equal(1, value));
        Assert.Equal(1, repository.PageCalls);

        repository.Items.Add(Notification(3));
        var changed = counts.WhenChanged(counts.Version);
        changes.NotifyChanged();
        Assert.True(changed.IsCompletedSuccessfully);
        Assert.Equal(2, await counts.GetUnreadCountAsync());
        Assert.Equal(2, repository.PageCalls);
    }

    [Fact]
    public async Task CacheExpiry_ObservesChangesFromAnotherAppInstance()
    {
        var clock = new TestClock();
        var repository = new FakeWorkspace { Items = [Notification(1)] };
        using var services = Services(repository, clock);
        var counts = services.GetRequiredService<NotificationCountService>();
        Assert.Equal(1, await counts.GetUnreadCountAsync());
        repository.Items[0] = Notification(1, read: true);
        Assert.Equal(1, await counts.GetUnreadCountAsync());
        clock.Now += NotificationCountService.RefreshInterval;
        Assert.Equal(0, await counts.GetUnreadCountAsync());
        Assert.Equal(2, repository.PageCalls);
    }

    [Fact]
    public async Task InvalidationDuringPaging_DiscardsTheStaleResult()
    {
        var repository = new FakeWorkspace { Items = [Notification(1)] };
        using var services = Services(repository);
        var changes = services.GetRequiredService<NotificationChangeSignal>();
        repository.AfterPage = () =>
        {
            repository.AfterPage = null;
            repository.Items.Add(Notification(2));
            changes.NotifyChanged();
        };
        var counts = services.GetRequiredService<NotificationCountService>();
        Assert.Equal(2, await counts.GetUnreadCountAsync());
        Assert.Equal(2, repository.PageCalls);
    }

    [Fact]
    public async Task FailureDoesNotBecomeZeroOrAPartialCachedCount()
    {
        var repository = new FakeWorkspace
        {
            Items = Enumerable.Range(0, 502).Select(i => Notification(i)).ToList(),
            FailOnPage = 2,
        };
        using var services = Services(repository);
        var counts = services.GetRequiredService<NotificationCountService>();
        await Assert.ThrowsAsync<IOException>(() => counts.GetUnreadCountAsync());
        repository.FailOnPage = null;
        Assert.Equal(502, await counts.GetUnreadCountAsync());
        Assert.Equal(4, repository.PageCalls);
    }

    [Fact]
    public async Task CancelledCaller_DoesNotBlockFutureRefreshes()
    {
        var repository = new FakeWorkspace { Items = [Notification(1)] };
        using var services = Services(repository);
        var counts = services.GetRequiredService<NotificationCountService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counts.GetUnreadCountAsync(cancellation.Token));
        Assert.Equal(1, await counts.GetUnreadCountAsync());
    }

    [Theory]
    [InlineData(0, "Notifications: no unread notifications")]
    [InlineData(1, "Notifications: 1 unread notification")]
    [InlineData(12, "Notifications: 12 unread notifications")]
    [InlineData(250, "Notifications: 250 unread notifications")]
    public async Task Bell_RendersExactUnreadCountAndHidesZero(long count, string label)
    {
        var repository = new FakeWorkspace { Items = Enumerable.Range(0, (int)count).Select(i => Notification(i)).ToList() };
        // Read notifications never contribute to the badge.
        repository.Items.Add(Notification(999, read: true));
        var html = await RenderBell(repository);
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(html);
        var link = document.DocumentNode.SelectSingleNode("//a[contains(@class,'notification-bell')]");
        Assert.NotNull(link);
        Assert.Equal("notifications", link.GetAttributeValue("href", ""));
        Assert.Equal(label, link.GetAttributeValue("aria-label", ""));
        var badge = link.SelectSingleNode(".//span[contains(@class,'notification-bell__badge')]");
        if (count == 0) Assert.Null(badge);
        else
        {
            Assert.NotNull(badge);
            Assert.Equal(count.ToString("N0"), badge.InnerText);
        }
        Assert.NotNull(link.SelectSingleNode(".//svg[@aria-hidden='true']"));
        Assert.NotNull(document.DocumentNode.SelectSingleNode("//*[@role='status' and @aria-live='polite']"));
    }

    [Fact]
    public async Task Bell_ShowsUnavailableStateOnFailure_NotAFakeZero()
    {
        var html = await RenderBell(new FakeWorkspace { FailOnPage = 1 });
        Assert.Contains("unread count unavailable", html);
        Assert.Contains("notification-bell__unavailable", html);
        Assert.DoesNotContain("notification-bell__badge", html);
        Assert.DoesNotContain("no unread notifications", html);
    }

    [Fact]
    public async Task SharedHeader_RefreshesFromPersistedMarkReadActions()
    {
        using var factory = new AnalystWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = factory.Services.CreateScope();
        var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var monitor = scope.ServiceProvider.GetRequiredService<DocketMonitorService>();
        var changes = scope.ServiceProvider.GetRequiredService<NotificationChangeSignal>();
        var notification = Notification(1);
        var first = await workspace.SaveAsync(notification.Kind, notification.Id, notification.Json, null);
        notification = Notification(2);
        var second = await workspace.SaveAsync(notification.Kind, notification.Id, notification.Json, null);
        changes.NotifyChanged();

        var html = await client.GetStringAsync("/");
        Assert.Contains("Notifications: 2 unread notifications", html);
        await monitor.MarkNotificationReadAsync(first);
        html = await client.GetStringAsync("/compare");
        Assert.Contains("Notifications: 1 unread notification", html);
        await monitor.MarkNotificationReadAsync(second);
        html = await client.GetStringAsync("/notifications");
        Assert.Contains("Notifications: no unread notifications", html);
        Assert.DoesNotContain("notification-bell__badge", html);
    }

    private static WorkspaceItem Notification(int number, bool read = false) =>
        new(number.ToString(), DocketMonitorService.NotificationKind,
            WorkspaceJson.Write(new DocketNotification
            {
                Id = number.ToString(), Title = "Synthetic test notification",
                ReadAt = read ? DateTimeOffset.Parse("2026-09-01T12:00:00Z") : null,
            }), "version");

    private static ServiceProvider Services(FakeWorkspace repository, TestClock? clock = null) =>
        new ServiceCollection().AddLogging()
            .AddSingleton<IWorkspaceRepository>(repository)
            .AddSingleton<TimeProvider>(clock ?? new TestClock())
            .AddSingleton<NotificationChangeSignal>()
            .AddSingleton<NotificationCountService>()
            .BuildServiceProvider();

    private static async Task<string> RenderBell(FakeWorkspace repository)
    {
        using var services = Services(repository);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<NotificationBell>(ParameterView.Empty);
            return output.ToHtmlString();
        });
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeWorkspace : IWorkspaceRepository
    {
        public List<WorkspaceItem> Items { get; set; } = new();
        public List<string> Kinds { get; } = new();
        public int PageCalls { get; private set; }
        public int? FailOnPage { get; set; }
        public Action? AfterPage { get; set; }
        public Task<AnalysisPage<WorkspaceItem>> ListPageAsync(string kind, int take = 50, string? continuationToken = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PageCalls++;
            Kinds.Add(kind);
            if (PageCalls == FailOnPage) throw new IOException("Synthetic notification store failure.");
            var offset = continuationToken is null ? 0 : int.Parse(continuationToken);
            var page = Items.Skip(offset).Take(take).ToList();
            var token = offset + take < Items.Count ? (offset + take).ToString() : null;
            AfterPage?.Invoke();
            return Task.FromResult(new AnalysisPage<WorkspaceItem>(page, token));
        }
        public Task<WorkspaceItem?> GetAsync(string kind, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkspaceItem> SaveAsync(string kind, string id, string json, string? expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string kind, string id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
