using Yemekhane.Application.Notifications;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Notifications;

public sealed class NotificationCenterViewModelTests
{
    [Fact]
    public async Task BadgeDrawerRealtimeDedupeAndNavigationWork()
    {
        var id = Guid.NewGuid();
        var api = new FakeApi(new NotificationItem(id, "Warning", "DeviceError", "Cihaz", "Hata",
            DateTimeOffset.UtcNow, "Device", "device-1", ShellRoutes.Devices, null, 1, DateTimeOffset.UtcNow, null, null));
        var realtime = new FakeRealtime();
        var navigation = new ShellNavigationService([ShellRoutes.Devices]);
        string? navigated = null; navigation.NavigationRequested += (_, e) => navigated = e.Route;
        using var vm = new NotificationCenterViewModel(api, realtime, navigation);

        await vm.InitializeAsync();
        Assert.Equal(1, vm.UnreadCount);
        realtime.Push(new NotificationEvent(id, "Error", "DeviceError", "Cihaz", "Tekrar hata",
            DateTimeOffset.UtcNow, "Device", "device-1", ShellRoutes.Devices, Count: 2));
        Assert.Single(vm.Items);
        Assert.Equal(1, vm.UnreadCount);

        vm.OpenCommand.Execute(vm.Items[0]);
        await WaitUntilAsync(() => api.Marked == id);
        Assert.Equal(ShellRoutes.Devices, navigated);
        Assert.Equal(0, vm.UnreadCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    { for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(10); Assert.True(condition()); }

    private sealed class FakeApi(NotificationItem item) : INotificationApiClient
    {
        public Guid? Marked { get; private set; }
        public Task<NotificationPage> ListAsync(int pageSize = 30, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotificationPage([item], null, 1));
        public Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default) { Marked = id; return Task.CompletedTask; }
        public Task MarkAllReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRealtime : INotificationRealtimeClient
    {
        public event EventHandler<NotificationEvent>? NotificationReceived;
        public event EventHandler<RealtimeConnectionState>? StateChanged;
        public void Push(NotificationEvent value) => NotificationReceived?.Invoke(this, value);
        public void SetState(RealtimeConnectionState value) => StateChanged?.Invoke(this, value);
    }
}
