using System.Runtime.ExceptionServices;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Devices;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class DevicesViewModelTests
{
    private static readonly Guid DeviceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task RealtimeAndActualActionResultUpdateCard()
    {
        var api = new FakeApi();
        var realtime = new FakeRealtime();
        using var viewModel = new DevicesViewModel(api, realtime, new HashSet<string> { "devices.read", "devices.manage" });
        await viewModel.InitializeAsync();
        var card = Assert.Single(viewModel.Devices);

        realtime.Emit(new DeviceStatusChangedEvent(DeviceId, "Okuyucu", "Disconnected", "Connecting", DateTimeOffset.UtcNow));
        Assert.Equal("Connecting", card.Status);

        viewModel.ConnectCommand.Execute(card);
        await WaitUntilAsync(() => api.ActionCalls == 1);
        await WaitUntilAsync(() => !card.IsBusy);

        Assert.Equal("Connected", card.Status);
        Assert.Equal("Gerçek adapter sonucu", card.OperationMessage);
    }

    [Fact]
    public async Task FormExposesSimulatorOnlyWhenApiMarksDevelopment()
    {
        var api = new FakeApi { SimulatorAllowed = false };
        using var viewModel = new DevicesViewModel(api, new FakeRealtime(), new HashSet<string> { "devices.manage" });
        await viewModel.InitializeAsync();

        viewModel.AddCommand.Execute(null);

        Assert.True(viewModel.IsEditorOpen);
        Assert.DoesNotContain("Simulator", viewModel.DeviceTypes);
    }

    [Fact]
    public async Task FailureIsPerCardAndIncludesDeviceEndpointAttemptAndRetry()
    {
        var api = new FakeApi { ActionResult = new DeviceActionResponse(false, "Error", "Exception occurred",
            "TCP_CONNECT_FAILED", null) };
        var realtime = new FakeRealtime();
        using var viewModel = new DevicesViewModel(api, realtime,
            new HashSet<string> { "devices.read", "devices.manage" });
        await viewModel.InitializeAsync();
        var card = Assert.Single(viewModel.Devices);
        var attempted = new DateTimeOffset(2026, 8, 31, 17, 30, 0, TimeSpan.Zero);
        var retry = attempted.AddSeconds(4);

        viewModel.ConnectCommand.Execute(card);
        await WaitUntilAsync(() => !card.IsBusy);
        realtime.Emit(new DeviceStatusChangedEvent(DeviceId, "Okuyucu", "Faulted", "Reconnecting",
            attempted, attempted, "Bağlantı kurulamadı", "TCP_CONNECT_FAILED", attempted, retry));

        Assert.Contains("Okuyucu", card.OperationMessage, StringComparison.Ordinal);
        Assert.Contains("10.0.0.2:9000", card.OperationMessage, StringComparison.Ordinal);
        Assert.Contains("31.08.2026 17:30:00", card.OperationMessage, StringComparison.Ordinal);
        Assert.Contains("31.08.2026 17:30:04", card.OperationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception occurred", card.OperationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(card.IsBusy);
    }

    [Fact]
    public async Task CardLoadingStateDoesNotBlockAnotherDeviceCommand()
    {
        var firstId = DeviceId;
        var secondId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var firstCompletion = new TaskCompletionSource<DeviceActionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi
        {
            Items = [FakeApi.Item(firstId), FakeApi.Item(secondId)],
            Action = id => id == firstId
                ? firstCompletion.Task
                : Task.FromResult(new DeviceActionResponse(true, "Connected", "İkinci cihaz bağlandı.", null,
                    FakeApi.Item(secondId, "Connected")))
        };
        using var viewModel = new DevicesViewModel(api, new FakeRealtime(),
            new HashSet<string> { "devices.read", "devices.manage" });
        await viewModel.InitializeAsync();
        var first = viewModel.Devices[0];
        var second = viewModel.Devices[1];

        viewModel.ConnectCommand.Execute(first);
        Assert.True(first.IsBusy);
        viewModel.ConnectCommand.Execute(second);
        await WaitUntilAsync(() => !second.IsBusy);

        Assert.True(first.IsBusy);
        Assert.Equal("Connected", second.Status);
        firstCompletion.SetResult(new DeviceActionResponse(false, "Error", "Port kapalı", "COM_OPEN_FAILED", null));
        await WaitUntilAsync(() => !first.IsBusy);
    }

    [Fact]
    public void DevicesXamlLoadsOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { _ = new DevicesView(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>Silme iki adimlidir; ilk basis yalnizca isaretler, ikincisi siler ve karti listeden kaldirir.</summary>
    [Fact]
    public async Task DeleteIsTwoStepAndRemovesTheCardOnConfirmation()
    {
        var api = new FakeApi();
        using var viewModel = new DevicesViewModel(api, new FakeRealtime(), new HashSet<string> { "devices.manage" });
        await viewModel.InitializeAsync();
        var card = viewModel.Devices.Single();

        viewModel.DeleteCommand.Execute(card);
        Assert.True(card.IsDeleteArmed);
        Assert.Equal("Silmeyi Onayla", card.DeleteButtonText);
        Assert.Empty(api.Deleted);
        Assert.Single(viewModel.Devices);

        viewModel.CancelDeleteCommand.Execute(card);
        Assert.False(card.IsDeleteArmed);
        Assert.Equal("Sil", card.DeleteButtonText);

        viewModel.DeleteCommand.Execute(card);
        viewModel.DeleteCommand.Execute(card);
        await WaitUntilAsync(() => api.Deleted.Count == 1);
        Assert.Empty(viewModel.Devices);
        Assert.True(viewModel.ShowEmpty);
    }

    /// <summary>Bellek temizleme yalnizca SC403'te sunulur ve iki adimlidir; ikinci basis "clear-users" eylemini gonderir.</summary>
    [Fact]
    public async Task ClearingDeviceMemoryIsTwoStepAndOnlyForSc403()
    {
        var api = new FakeApi { Items = [FakeApi.Item() with { DeviceType = "SC403" }] };
        using var viewModel = new DevicesViewModel(api, new FakeRealtime(), new HashSet<string> { "devices.manage" });
        await viewModel.InitializeAsync();
        var card = viewModel.Devices.Single();
        Assert.True(card.SupportsClearUsers);
        Assert.False(new DeviceCardViewModel(FakeApi.Item()).SupportsClearUsers);

        viewModel.ClearUsersCommand.Execute(card);
        Assert.True(card.IsClearArmed);
        Assert.Equal("Temizlemeyi Onayla", card.ClearButtonText);
        Assert.Empty(api.Actions);

        viewModel.ClearUsersCommand.Execute(card);
        await WaitUntilAsync(() => api.Actions.Count == 1);
        Assert.Equal("clear-users", api.Actions.Single());
        Assert.False(card.IsClearArmed);
    }

    /// <summary>Sunucu reddederse (gecis kaydi var) kart kalir ve sunucu mesaji kartta gorunur.</summary>
    [Fact]
    public async Task ServerRejectionKeepsTheCardAndShowsTheReason()
    {
        var api = new FakeApi { DeleteError = "Okuyucu cihazının 12 geçiş kaydı var; kayıtlar korunur, cihaz silinemez. Bunun yerine pasifleştirin." };
        using var viewModel = new DevicesViewModel(api, new FakeRealtime(), new HashSet<string> { "devices.manage" });
        await viewModel.InitializeAsync();
        var card = viewModel.Devices.Single();

        viewModel.DeleteCommand.Execute(card);
        viewModel.DeleteCommand.Execute(card);
        await WaitUntilAsync(() => !card.IsDeleteArmed && card.OperationMessage is not null && card.OperationMessage.Contains("geçiş kaydı", StringComparison.Ordinal));

        Assert.Single(viewModel.Devices);
        Assert.Contains("pasifleştirin", card.OperationMessage, StringComparison.Ordinal);
    }

    private sealed class FakeApi : IDeviceApiClient
    {
        public bool SimulatorAllowed { get; init; } = true;
        public int ActionCalls { get; private set; }
        public DeviceActionResponse? ActionResult { get; init; }
        public IReadOnlyList<DeviceItem>? Items { get; init; }
        public Func<Guid, Task<DeviceActionResponse>>? Action { get; init; }
        public static DeviceItem Item(Guid? id = null, string status = "Disconnected") => new(id ?? DeviceId, "Okuyucu", "EthernetReader",
            "Ethernet", "10.0.0.2:9000", "10.0.0.2", 9000, null, null, true, false, false,
            "Ana giriş", "Entry", status, null, null, null, null, null, false);
        public Task<IReadOnlyList<DeviceItem>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items ?? [Item()]);
        public Task<DeviceCapabilities> CapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DeviceCapabilities(SimulatorAllowed));
        public List<string> Actions { get; } = [];
        public Task<DeviceActionResponse> ActionAsync(Guid id, string action, CancellationToken cancellationToken = default) { ActionCalls++; Actions.Add(action); return Action?.Invoke(id) ?? Task.FromResult(ActionResult ?? new DeviceActionResponse(true, "Connected", "Gerçek adapter sonucu", null, Item(status: "Connected"))); }
        public Task<IReadOnlyList<DeviceLogItem>> LogsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceLogItem>>([]);
        public Task<DeviceItem> CreateAsync(DeviceWriteModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DeviceItem> UpdateAsync(Guid id, DeviceWriteModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DeviceItem> DeactivateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public List<Guid> Deleted { get; } = [];
        public string? DeleteError { get; init; }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (DeleteError is not null) throw new System.Net.Http.HttpRequestException(DeleteError);
            Deleted.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRealtime : IDashboardRealtimeClient
    {
        public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived { add { } remove { } }
        public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
        public event EventHandler<RealtimeConnectionState>? StateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Emit(DeviceStatusChangedEvent value) => DeviceStatusChanged?.Invoke(this, value);
    }
}
