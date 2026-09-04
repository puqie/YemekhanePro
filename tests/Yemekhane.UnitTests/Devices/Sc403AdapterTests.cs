using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Devices.ZkTeco;

namespace Yemekhane.UnitTests.Devices;

public sealed class Sc403AdapterTests
{
    private static DeviceEndpoint Endpoint(string ip = "192.168.1.201", int port = Sc403Adapter.DefaultPort) =>
        new("Ethernet", IpAddress: ip, IpPort: port);

    private static Sc403Adapter Create(FakeZkTecoSdk sdk, int retries = 0) =>
        new(Guid.NewGuid(), "SC403 Giris", Endpoint(), sdk, TimeSpan.FromSeconds(1), retries);

    private static async Task<Sc403Adapter> ConnectedAsync(FakeZkTecoSdk sdk, int retries = 0)
    {
        var adapter = Create(sdk, retries);
        await adapter.ConnectAsync(CancellationToken.None);
        return adapter;
    }

    [Fact]
    public async Task ConnectPublishesCapabilitiesReportedByDevice()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = Create(sdk);

        var info = await adapter.ConnectAsync(CancellationToken.None);

        Assert.Equal("SC403", info.Model);
        Assert.Equal(DeviceConnectionState.Connected, adapter.ConnectionState);
        Assert.Contains(DeviceCapability.ReadCard, adapter.Capabilities);
    }

    /// <summary>
    /// Cihaz yalnizca bildirdigi yetenekleri destekler. Bildirmedigi bir komut yine de gonderilirse
    /// cihaz hata verir ama yazilim bunu "denendi" sayar; onceden reddetmek dogru davranistir.
    /// </summary>
    [Fact]
    public async Task CommandIsRejectedWhenDeviceDidNotReportCapability()
    {
        var sdk = new FakeZkTecoSdk
        {
            DeviceInfo = new DeviceInfo("SC403", "SN", "1.0",
                new HashSet<DeviceCapability> { DeviceCapability.DeviceInfo, DeviceCapability.Status })
        };
        await using var adapter = await ConnectedAsync(sdk);

        await Assert.ThrowsAsync<DeviceCapabilityException>(
            () => adapter.SendCardAsync("1234567", "STU-1", CancellationToken.None));
        Assert.DoesNotContain(sdk.Calls, call => call.StartsWith("SetCardNumberAsync", StringComparison.Ordinal));
    }

    /// <summary>Baglanti kurulmadan komut gonderilemez; aksi halde sessizce kaybolur.</summary>
    [Fact]
    public async Task CommandFailsWhenNotConnected()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = Create(sdk);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.SendCardAsync("1234567", "STU-1", CancellationToken.None));

        Assert.Equal(ZkTecoErrorCodes.Disconnected, exception.ErrorCode);
    }

    /// <summary>
    /// SDK baglamasi yapilandirilmadiginda adaptor sessizce basarili olmamalidir: kart hic
    /// yazilmadigi halde "yuklendi" isaretlenirse ogrenci turnikeden gecemez.
    /// </summary>
    [Fact]
    public async Task MissingSdkBindingIsReportedInsteadOfSilentSuccess()
    {
        await using var adapter = new Sc403Adapter(Guid.NewGuid(), "SC403", Endpoint());

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.ConnectAsync(CancellationToken.None));

        Assert.Equal(ZkTecoErrorCodes.NotConfigured, exception.ErrorCode);
        Assert.True(DeviceErrorCodes.IsDisconnected(exception.ErrorCode));
    }

    [Fact]
    public async Task ConnectFaultsWhenDeviceInfoResponseIsInvalid()
    {
        var sdk = new FakeZkTecoSdk { DeviceInfo = null };
        await using var adapter = Create(sdk);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.ConnectAsync(CancellationToken.None));

        Assert.Equal(ZkTecoErrorCodes.HandshakeInvalidResponse, exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Faulted, adapter.ConnectionState);
    }

    [Fact]
    public async Task ConnectFaultsWhenSdkReportsNoSession()
    {
        var sdk = new FakeZkTecoSdk { RefuseConnection = true };
        await using var adapter = Create(sdk);

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.ConnectAsync(CancellationToken.None));

        Assert.Equal(ZkTecoErrorCodes.ConnectFailed, exception.ErrorCode);
    }

    /// <summary>Gecici hata yeniden denenir; kalici hata denenmez.</summary>
    [Fact]
    public async Task TransientFailureIsRetried()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = await ConnectedAsync(sdk, retries: 2);
        // Hata baglantidan SONRA kurgulanir; aksi halde handshake tuketirdi.
        sdk.FailNext(new ZkTecoProtocolException("mesgul", isTransient: true, "ZK_BUSY"));

        var result = await adapter.SendCardAsync("1234567", "STU-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, sdk.Calls.Count(call => call.StartsWith("SetCardNumberAsync", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PermanentFailureIsNotRetried()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = await ConnectedAsync(sdk, retries: 2);
        sdk.FailNext(new ZkTecoProtocolException("gecersiz", isTransient: false, "ZK_INVALID_CARD"));

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => adapter.SendCardAsync("1234567", "STU-1", CancellationToken.None));

        Assert.Equal("ZK_INVALID_CARD", exception.ErrorCode);
        Assert.Equal(1, sdk.Calls.Count(call => call.StartsWith("SetCardNumberAsync", StringComparison.Ordinal)));
        Assert.True(DeviceErrorCodes.IsPermanent(exception.ErrorCode));
    }

    [Fact]
    public async Task RealTimeCardEventsAreStreamed()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = await ConnectedAsync(sdk);
        sdk.PushCard(new CardReadEvent("0008573921", DateTimeOffset.UtcNow, "SC403"));
        sdk.CompleteCards();

        var cards = new List<CardReadEvent>();
        await foreach (var card in adapter.ReadCardsAsync(CancellationToken.None)) cards.Add(card);

        Assert.Equal("0008573921", Assert.Single(cards).CardNumber);
    }

    /// <summary>Bos kart numarasi tasiyan bir olay gecerli sayilamaz.</summary>
    [Fact]
    public async Task InvalidCardEventIsRejected()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = await ConnectedAsync(sdk);
        sdk.PushCard(new CardReadEvent("   ", DateTimeOffset.UtcNow, "SC403"));

        await Assert.ThrowsAsync<DeviceConnectionException>(async () =>
        {
            await foreach (var _ in adapter.ReadCardsAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task StatusReportsDisconnectedWhenSessionDropped()
    {
        var sdk = new FakeZkTecoSdk();
        await using var adapter = await ConnectedAsync(sdk);
        await sdk.DisconnectAsync(CancellationToken.None);

        var status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Disconnected, status.State);
        Assert.Equal(DeviceConnectionState.Disconnected, adapter.ConnectionState);
    }

    [Fact]
    public async Task DisposeClosesSdkSession()
    {
        var sdk = new FakeZkTecoSdk();
        var adapter = await ConnectedAsync(sdk);

        await adapter.DisposeAsync();

        Assert.True(sdk.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.ConnectAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("COM")]
    [InlineData("Simulator")]
    public void NonEthernetEndpointIsRejected(string connectionType) =>
        Assert.Throws<ArgumentException>(() => new Sc403Adapter(Guid.NewGuid(), "SC403",
            new DeviceEndpoint(connectionType, IpAddress: "192.168.1.201", IpPort: 4370)));

    [Fact]
    public void MissingPortIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sc403Adapter(Guid.NewGuid(), "SC403",
            new DeviceEndpoint("Ethernet", IpAddress: "192.168.1.201")));

    // ---- Turnike suren surum -------------------------------------------------------------

    private static Sc403AccessController Controller(FakeZkTecoSdk sdk, OzakTurnstileProfile? profile = null) =>
        new(Guid.NewGuid(), "SC403 + 720E", Endpoint(), profile, sdk, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Kuru kontakli turnikede reddetme ayri bir komut degildir: role kapatilmaz, turnike kilitli
    /// kalir. Cihaza komut gonderilmemelidir.
    /// </summary>
    [Fact]
    public async Task DenyAccessLeavesTurnstileLockedWithoutDeviceCommand()
    {
        var sdk = new FakeZkTecoSdk();
        await using var controller = Controller(sdk);
        await controller.ConnectAsync(CancellationToken.None);
        var before = sdk.Calls.Count;

        var result = await controller.DenyAccessAsync(TurnstileDirection.Entry, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(before, sdk.Calls.Count);
    }

    /// <summary>
    /// Fiziksel olarak surulemeyen yon icin komut BASARISIZ donmelidir. Basarili saymak, hic
    /// donmemis bir turnikeyi acilmis gibi kaydeder ve ogrencinin yemek hakkini yakar.
    /// </summary>
    [Fact]
    public async Task UnsupportedDirectionFailsInsteadOfReportingSuccess()
    {
        var sdk = new FakeZkTecoSdk();
        await using var controller = Controller(sdk, new OzakTurnstileProfile(SupportsBidirectional: false));
        await controller.ConnectAsync(CancellationToken.None);

        var result = await controller.GrantAccessAsync(TurnstileDirection.Exit, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ZK_DIRECTION_UNSUPPORTED", result.ErrorCode);
    }

    /// <summary>
    /// Kapi rolesini suren SDK cagrisi uretici dokumaninda yer almadigindan, komut sessizce
    /// basarili sayilmak yerine cihaz dogrulamasi gerektigini bildirmelidir (donanim dok. §08).
    ///
    /// Sonuc ATILMAZ, BASARISIZ olarak DONDURULUR. TurnstileService yalnizca basarisiz donen
    /// sonucta tuketilen yemek hakkini iade eder (compensateConsumption: isAllowed); atilan bir
    /// istisna genel catch bloguna duser ve orada iade YAPILMAZ. Yani burada istisna atmak,
    /// turnike hic donmedigi halde ogrencinin hakkini yakardi.
    /// </summary>
    [Fact]
    public async Task RelayDriveReportsValidationRequiredAsFailedResultSoCreditIsRefunded()
    {
        var sdk = new FakeZkTecoSdk();
        await using var controller = Controller(sdk);
        await controller.ConnectAsync(CancellationToken.None);

        var result = await controller.GrantAccessAsync(TurnstileDirection.Entry, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ZkTecoErrorCodes.ValidationRequired, result.ErrorCode);
        Assert.True(DeviceErrorCodes.IsPermanent(result.ErrorCode));
    }

    [Fact]
    public async Task ControllerIsResolvableAsTurnstile()
    {
        var sdk = new FakeZkTecoSdk();
        await using var controller = Controller(sdk);
        await controller.ConnectAsync(CancellationToken.None);

        var registry = new TurnstileRegistry();
        registry.Register(controller);

        Assert.True(registry.TryResolve(controller.Id, out var resolved));
        Assert.Same(controller, resolved);
        Assert.True(registry.Supports(controller.Id, DeviceCapability.GrantAccess));
    }
}
