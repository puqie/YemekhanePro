using Yemekhane.Application.Devices;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.DeviceCards;

/// <summary>
/// Kart Yukleme Durumu ekraninin "Cihazdaki kartlar" sekmesi: cihaz secilince liste gelir,
/// arama ve sayfa sunucuya gider, hatali satirdaki "Yeniden yükle" karti kuyruga alip listeyi tazeler.
/// </summary>
public sealed class DeviceCardsListViewModelTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();

    [Fact]
    public async Task ShowCardsOpensTheCardsTabWithTheDeviceList()
    {
        var api = new FakeApi();
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();

        await vm.ShowCardsAsync(vm.Devices[0]);

        Assert.True(vm.HasCardList);
        Assert.Equal(DeviceCardsViewModel.CardsTab, vm.SelectedPanelTab);
        Assert.Equal(2, vm.DeviceCards.Count);
        Assert.Equal(2, vm.CardsTotal);
        Assert.Equal("Cihazdaki kartlar (2)", vm.CardsTabHeader);
        Assert.Equal(DeviceId, api.ListedDevice);
        var failed = vm.DeviceCards.Single(x => x.StudentNo == "5002");
        Assert.Equal("Hata", failed.StatusText); Assert.True(failed.CanResync); Assert.Equal("SF300_FULL", failed.LastError);
        var loaded = vm.DeviceCards.Single(x => x.StudentNo == "5001");
        Assert.Equal("Yüklendi", loaded.StatusText); Assert.False(loaded.CanResync);
        Assert.Contains("zaten yüklü", loaded.ResyncHint);
        // Istanbul saatine cevrilir: 09:00Z -> 12:00.
        Assert.Equal("02.09.2026 12:00", loaded.LastSyncedText);
    }

    [Fact]
    public async Task PendingButtonOpensThePendingTabButStillLoadsTheList()
    {
        var api = new FakeApi();
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();

        await vm.SelectDeviceAsync(vm.Devices[0]);

        Assert.Equal(DeviceCardsViewModel.PendingTab, vm.SelectedPanelTab);
        Assert.Equal(2, vm.DeviceCards.Count);
    }

    [Fact]
    public async Task SearchAndPagingGoToTheServer()
    {
        var api = new FakeApi { Total = 120 };
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();
        await vm.ShowCardsAsync(vm.Devices[0]);

        vm.CardSearch = " ada ";
        await vm.LoadCardsAsync(1);
        Assert.Equal(" ada ", api.LastSearch);
        Assert.True(vm.NextCardsPageCommand.CanExecute(null));
        Assert.False(vm.PreviousCardsPageCommand.CanExecute(null));

        await vm.LoadCardsAsync(2);

        Assert.Equal(2, api.LastPage); Assert.Equal(DeviceCardsViewModel.CardsPageSize, api.LastPageSize);
        Assert.Equal("Sayfa 2 / 3   •   120 kart", vm.CardsPageText);
        Assert.True(vm.PreviousCardsPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResyncQueuesTheCardAndRefreshesWithoutLosingTheTab()
    {
        var api = new FakeApi();
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();
        await vm.ShowCardsAsync(vm.Devices[0]);
        var failed = vm.DeviceCards.Single(x => x.IsFailed);

        await vm.ResyncCardAsync(failed);

        Assert.Equal(failed.CardId, api.ResyncedCard);
        Assert.Contains("yeniden yükleme kuyruğuna alındı", vm.StatusMessage);
        Assert.Contains("5002", vm.StatusMessage);
        Assert.Equal(DeviceCardsViewModel.CardsTab, vm.SelectedPanelTab);
        Assert.True(vm.HasSelection);
        Assert.Equal(2, api.ListCalls);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task WithoutAListClientTheScreenStillWorks()
    {
        using var vm = new DeviceCardsViewModel(new FakeApi.SummaryOnly());
        await vm.InitializeAsync();

        await vm.ShowCardsAsync(vm.Devices[0]);

        Assert.False(vm.HasCardList);
        Assert.Empty(vm.DeviceCards);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task OfflineListShowsAMessage()
    {
        var api = new FakeApi { ListFailure = new System.Net.Http.HttpRequestException("offline") };
        using var vm = new DeviceCardsViewModel(api);
        await vm.InitializeAsync();

        await vm.ShowCardsAsync(vm.Devices[0]);

        Assert.Contains("alınamadı", vm.Error);
        Assert.False(vm.IsCardsEmpty);
    }

    private class FakeApi : IDeviceCardsApiClient, IDeviceCardListApiClient
    {
        public int Total { get; set; } = 2;
        public Exception? ListFailure { get; set; }
        public Guid? ListedDevice { get; private set; }
        public string? LastSearch { get; private set; }
        public int LastPage { get; private set; }
        public int LastPageSize { get; private set; }
        public int ListCalls { get; private set; }
        public Guid? ResyncedCard { get; private set; }

        public Task<IReadOnlyList<DeviceCardSummary>> GetSummaryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceCardSummary>>([new(DeviceId, "Yemekhane Giriş", 1, 0, 1)]);
        public Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingDeviceCard>>([]);
        public Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceCardStatusRow>>([]);
        public Task ResyncCardAsync(Guid cardId, CancellationToken cancellationToken = default) { ResyncedCard = cardId; return Task.CompletedTask; }
        public Task PushNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DeviceCardListResult> GetCardsAsync(Guid deviceId, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            if (ListFailure is not null) throw ListFailure;
            ListedDevice = deviceId; LastSearch = search; LastPage = page; LastPageSize = pageSize; ListCalls++;
            return Task.FromResult(new DeviceCardListResult(
            [
                new(Guid.NewGuid(), Guid.NewGuid(), "5001", "ADA YILMAZ", "5A", "8350001", DeviceCardSyncStatus.Loaded,
                    new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), 0, null),
                new(Guid.NewGuid(), Guid.NewGuid(), "5002", "ALİ KAYA", "6B", "8350002", DeviceCardSyncStatus.Failed, null, 3, "SF300_FULL")
            ], page, pageSize, Total));
        }

        /// <summary>Yalnizca eski arayuzu uygulayan istemci: liste sekmesi kapali kalmali, ekran cokmemeli.</summary>
        public sealed class SummaryOnly : IDeviceCardsApiClient
        {
            public Task<IReadOnlyList<DeviceCardSummary>> GetSummaryAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<DeviceCardSummary>>([new(DeviceId, "Yemekhane Giriş", 1, 0, 1)]);
            public Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PendingDeviceCard>>([]);
            public Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<DeviceCardStatusRow>>([]);
            public Task ResyncCardAsync(Guid cardId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task PushNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
