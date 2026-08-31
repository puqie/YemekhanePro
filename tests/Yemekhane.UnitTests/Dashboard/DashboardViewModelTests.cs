using Yemekhane.Application.Dashboard;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Dashboard;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task MissingSessionShowsLoginRequiredWithoutApiData()
    {
        var vm = Create(new ThrowingApi(new LoginRequiredException()), new FakeRealtime(), false);
        await vm.InitializeAsync();
        Assert.True(vm.LoginRequired);
        Assert.False(vm.HasData);
        Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadingFailureAndRefreshAreExposed()
    {
        var api = new MutableApi { Error = new HttpRequestException() };
        var vm = Create(api, new FakeRealtime(), true);
        await vm.InitializeAsync();
        Assert.True(vm.HasError);
        api.Error = null;
        await vm.LoadAsync();
        Assert.False(vm.HasError);
        Assert.True(vm.HasData);
    }

    [Fact]
    public async Task RealtimeEventsUpdateAccessKpisAndDeviceWithoutManualRefresh()
    {
        var realtime = new FakeRealtime();
        var vm = Create(new MutableApi(), realtime, true);
        await vm.InitializeAsync();

        realtime.EmitAccess(new AccessDecisionCommittedEvent(Guid.NewGuid(), "DENY", "NO_RIGHT", null,
            null, DeviceId, Guid.NewGuid(), DateTimeOffset.UtcNow));
        realtime.EmitDevice(new DeviceStatusChangedEvent(DeviceId, "Turnike", "Online", "Offline", DateTimeOffset.UtcNow));

        Assert.Equal(1, vm.Snapshot!.Kpis.Denied);
        Assert.Single(vm.Snapshot.RecentAccess);
        Assert.Equal(1, vm.Snapshot.DeviceSummary.Offline);
        Assert.Equal("Offline", Assert.Single(vm.Snapshot.Devices).Status);
    }

    [Fact]
    public void QuickActionsAreDisabledWhenFeatureRoutesAreUnavailable()
    {
        var vm = Create(new MutableApi(), new FakeRealtime(), true);
        Assert.Equal(7, vm.QuickActions.Count);
        Assert.All(vm.QuickActions, item =>
        {
            Assert.False(item.IsAvailable);
            Assert.False(item.Command.CanExecute(null));
            Assert.False(string.IsNullOrWhiteSpace(item.UnavailableReason));
        });
    }

    [Fact]
    public void AvailableRouteCommandRaisesNavigationRequest()
    {
        var navigation = new ShellNavigationService([ShellRoutes.Students]);
        string? requested = null;
        navigation.NavigationRequested += (_, args) => requested = args.Route;
        navigation.Navigate(ShellRoutes.Students);
        Assert.Equal(ShellRoutes.Students, requested);
        Assert.Throws<InvalidOperationException>(() => navigation.Navigate(ShellRoutes.Reports));
    }

    private static readonly Guid DeviceId = Guid.NewGuid();

    private static DashboardViewModel Create(IDashboardApiClient api, FakeRealtime realtime, bool authenticated) =>
        new(api, realtime, new ShellNavigationService([ShellRoutes.Dashboard]), new FakeSession(authenticated));

    private sealed class FakeSession(bool authenticated) : IJwtSession
    {
        public string? AccessToken => authenticated ? "test-token" : null;
        public bool IsAuthenticated => authenticated;
    }

    private sealed class ThrowingApi(Exception exception) : IDashboardApiClient
    {
        public Task<DashboardSnapshot> GetAsync(CancellationToken cancellationToken = default) => Task.FromException<DashboardSnapshot>(exception);
    }

    private sealed class MutableApi : IDashboardApiClient
    {
        public Exception? Error { get; set; }
        public Task<DashboardSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Error is null ? Task.FromResult(Snapshot()) : Task.FromException<DashboardSnapshot>(Error);
    }

    private sealed class FakeRealtime : IDashboardRealtimeClient
    {
        public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
        public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
        public event EventHandler<RealtimeConnectionState>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            StateChanged?.Invoke(this, RealtimeConnectionState.Connected);
            return Task.CompletedTask;
        }
        public void EmitAccess(AccessDecisionCommittedEvent value) => AccessReceived?.Invoke(this, value);
        public void EmitDevice(DeviceStatusChangedEvent value) => DeviceStatusChanged?.Invoke(this, value);
    }

    private static DashboardSnapshot Snapshot() => new(new DateOnly(2026, 8, 31), DateTimeOffset.UtcNow,
        new DashboardKpis(2, 2, 2, 0, 2, 0, 0), [], new DashboardDeviceSummary(1, 1, 0, 0),
        [new DashboardDeviceRow(DeviceId, "Turnike", "SF300", "Online", null)], [], []);
}
