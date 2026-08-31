using Yemekhane.Application.DailyTracking;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.DailyTracking;

public sealed class DailyTrackingViewModelTests
{
    [Fact]
    public async Task RealtimeDeduplicatesOrdersAndBoundsRows()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = Enumerable.Range(0, DailyTrackingViewModel.MaximumRows + 10)
            .Select(i => Row(Guid.NewGuid(), now.AddMilliseconds(-i))).ToArray();
        var api = new FakeApi(Page(initial));
        var realtime = new FakeRealtime();
        var vm = Create(api, realtime);
        await vm.InitializeAsync();
        var newest = Row(Guid.NewGuid(), now.AddSeconds(1));
        api.Next = Page([newest, newest]);

        realtime.Emit(newest.OperationId, newest.Timestamp);
        realtime.Emit(newest.OperationId, newest.Timestamp);

        Assert.Equal(DailyTrackingViewModel.MaximumRows, vm.Rows.Count);
        Assert.Equal(newest.OperationId, vm.Rows[0].OperationId);
        Assert.Equal(vm.Rows.Count, vm.Rows.Select(x => x.OperationId).Distinct().Count());
        Assert.Equal(vm.Rows.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.OperationId), vm.Rows);
    }

    [Fact]
    public async Task ReconnectAndResumeRecoverGapAndSoundIsOptional()
    {
        var now = DateTimeOffset.UtcNow;
        var original = Row(Guid.NewGuid(), now);
        var gap = Row(Guid.NewGuid(), now.AddSeconds(1), "DENY");
        var api = new FakeApi(Page([original]));
        var realtime = new FakeRealtime();
        var preferences = new FakePreferences();
        var sound = new FakeSound();
        var vm = Create(api, realtime, preferences, sound);
        await vm.InitializeAsync();
        await ((AsyncCommand)vm.ToggleLiveCommand).ExecuteForTestAsync();
        api.Next = Page([gap]);
        realtime.Emit(gap.OperationId, gap.Timestamp);
        Assert.Single(vm.Rows);

        vm.SoundEnabled = true;
        await ((AsyncCommand)vm.ToggleLiveCommand).ExecuteForTestAsync();

        Assert.Equal(2, vm.Rows.Count);
        Assert.True(preferences.SoundEnabled);
        Assert.Equal(1, sound.Count);
        api.Next = Page([]);
        realtime.SetState(RealtimeConnectionState.Connected);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task LoadingErrorIsExposedWithoutFakeRows()
    {
        var vm = Create(new FakeApi(new HttpRequestException()), new FakeRealtime());
        await vm.InitializeAsync();
        Assert.True(vm.HasError);
        Assert.True(vm.IsEmpty is false);
        Assert.Empty(vm.Rows);
    }

    private static DailyTrackingViewModel Create(FakeApi api, FakeRealtime realtime,
        FakePreferences? preferences = null, FakeSound? sound = null) =>
        new(api, realtime, preferences ?? new FakePreferences(), sound ?? new FakeSound());

    private static DailyTrackingPage Page(DailyTrackingRow[] rows) =>
        new(rows, new DailyTrackingSummary(rows.Length, rows.Count(x => x.Decision == "ALLOW"), rows.Count(x => x.Decision == "DENY")),
            DateTimeOffset.UtcNow, null, null, false);

    private static DailyTrackingRow Row(Guid id, DateTimeOffset timestamp, string decision = "ALLOW") =>
        new(id, timestamp, "CARD", Guid.NewGuid(), "100", "Ada Yılmaz", Guid.NewGuid(), "10-A",
            Guid.NewGuid(), "Öğle", Guid.NewGuid(), "Turnike", decision, "Test");

    private sealed class FakeApi : IDailyTrackingApiClient
    {
        private readonly Exception? error;
        public FakeApi(DailyTrackingPage next) => Next = next;
        public FakeApi(Exception error) => this.error = error;
        public DailyTrackingPage Next { get; set; } = Page([]);
        public Task<DailyTrackingPage> GetAsync(DailyTrackingQuery query, CancellationToken cancellationToken = default) =>
            error is null ? Task.FromResult(Next) : Task.FromException<DailyTrackingPage>(error);
    }

    private sealed class FakeRealtime : IDashboardRealtimeClient
    {
        public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
        public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged { add { } remove { } }
        public event EventHandler<RealtimeConnectionState>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Emit(Guid operationId, DateTimeOffset timestamp) => AccessReceived?.Invoke(this,
            new AccessDecisionCommittedEvent(operationId, "ALLOW", "Test", null, null, Guid.NewGuid(), Guid.NewGuid(), timestamp));
        public void SetState(RealtimeConnectionState state) => StateChanged?.Invoke(this, state);
    }

    private sealed class FakePreferences : IDailyTrackingPreferences { public bool SoundEnabled { get; set; } }
    private sealed class FakeSound : ITrackingSoundPlayer
    {
        public int Count { get; private set; }
        public ValueTask PlayAsync(string decision, CancellationToken cancellationToken = default) { Count++; return ValueTask.CompletedTask; }
    }
}

internal static class AsyncCommandTestExtensions
{
    public static async Task ExecuteForTestAsync(this AsyncCommand command)
    {
        command.Execute(null);
        await Task.Yield();
    }
}
