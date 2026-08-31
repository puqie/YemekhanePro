using Yemekhane.Application.Devices;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kart yukleme durumu ekrani. Operatorun gormesi gereken sey "kac kart yuklendi" degil,
/// "hangi cihazda ne eksik" oldugundan sorunlu cihazlar one cikarilir.
/// </summary>
public sealed class DeviceCardsViewModelTests
{
    [Fact]
    public async Task SummaryLoadsAndHighlightsDevicesNeedingAttention()
    {
        var api = new FakeApi
        {
            Summaries =
            [
                new(Guid.NewGuid(), "Ana Giriş", Loaded: 120, Pending: 0, Failed: 0),
                new(Guid.NewGuid(), "Arka Kapı", Loaded: 100, Pending: 18, Failed: 2)
            ]
        };
        using var vm = new DeviceCardsViewModel(api);

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Devices.Count);
        // Mudahale gereken cihaz basa alinir: operator hangi turnikede eksik oldugunu aramamalidir.
        Assert.Equal("Arka Kapı", vm.Devices[0].DeviceName);
        Assert.True(vm.Devices[0].NeedsAttention);
        Assert.True(vm.Devices[0].HasFailures);
        Assert.False(vm.Devices.Single(x => x.DeviceName == "Ana Giriş").NeedsAttention);
        Assert.Equal(20, vm.TotalOutstanding);
        Assert.True(vm.HasOutstanding);
    }

    [Fact]
    public async Task FullySyncedFleetReportsNothingOutstanding()
    {
        var api = new FakeApi
        {
            Summaries = [new(Guid.NewGuid(), "Ana Giriş", Loaded: 120, Pending: 0, Failed: 0)]
        };
        using var vm = new DeviceCardsViewModel(api);

        await vm.InitializeAsync();

        Assert.False(vm.HasOutstanding);
        Assert.Equal(0, vm.TotalOutstanding);
    }

    [Fact]
    public async Task SelectingADeviceLoadsItsPendingQueue()
    {
        var deviceId = Guid.NewGuid();
        var api = new FakeApi
        {
            Summaries = [new(deviceId, "Arka Kapı", Loaded: 5, Pending: 2, Failed: 0)],
            Pending =
            [
                new(Guid.NewGuid(), Guid.NewGuid(), "0012345678", "Ayşe Yılmaz", IsRemoval: false, AttemptCount: 3),
                new(Guid.NewGuid(), Guid.NewGuid(), "0087654321", "Mehmet Demir", IsRemoval: true, AttemptCount: 0)
            ]
        };
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();

        await vm.SelectDeviceAsync(vm.Devices[0]);

        Assert.Equal(2, vm.PendingCards.Count);
        Assert.Equal("Ayşe Yılmaz", vm.PendingCards[0].StudentName);
        Assert.Equal("Yükleniyor", vm.PendingCards[0].ActionText);
        Assert.Equal("Siliniyor", vm.PendingCards[1].ActionText);
        Assert.Equal(deviceId, api.RequestedDeviceId);
    }

    [Fact]
    public async Task PushNowRefreshesSummaryAfterwards()
    {
        var api = new FakeApi
        {
            Summaries = [new(Guid.NewGuid(), "Ana Giriş", Loaded: 1, Pending: 5, Failed: 0)]
        };
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();
        api.Summaries = [new(api.Summaries[0].DeviceId, "Ana Giriş", Loaded: 6, Pending: 0, Failed: 0)];

        await vm.PushNowAsync();

        Assert.Equal(1, api.PushCount);
        Assert.False(vm.HasOutstanding);
    }

    [Fact]
    public async Task OfflineApiShowsAMessageInsteadOfCrashing()
    {
        var api = new FakeApi { Throw = new System.Net.Http.HttpRequestException("offline") };
        using var vm = new DeviceCardsViewModel(api);

        await vm.InitializeAsync();

        Assert.True(vm.HasError);
        Assert.Contains("alınamadı", vm.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task ExpiredSessionAsksForLoginRatherThanShowingAGenericError()
    {
        var api = new FakeApi { Throw = new LoginRequiredException() };
        using var vm = new DeviceCardsViewModel(api);

        await vm.InitializeAsync();

        Assert.True(vm.HasError);
        Assert.Contains("oturum", vm.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeApi : IDeviceCardsApiClient
    {
        public IReadOnlyList<DeviceCardSummary> Summaries { get; set; } = [];
        public IReadOnlyList<PendingDeviceCard> Pending { get; set; } = [];
        public Exception? Throw { get; init; }
        public int PushCount { get; private set; }
        public Guid? RequestedDeviceId { get; private set; }

        public Task<IReadOnlyList<DeviceCardSummary>> GetSummaryAsync(CancellationToken cancellationToken = default)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(Summaries);
        }

        public Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit,
            CancellationToken cancellationToken = default)
        {
            if (Throw is not null) throw Throw;
            RequestedDeviceId = deviceId;
            return Task.FromResult(Pending);
        }

        public Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceCardStatusRow>>([]);

        public Task ResyncCardAsync(Guid cardId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PushNowAsync(CancellationToken cancellationToken = default)
        {
            PushCount++;
            return Task.CompletedTask;
        }
    }
}
