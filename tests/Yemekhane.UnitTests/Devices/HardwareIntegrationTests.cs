using Yemekhane.Application.Access;
using Yemekhane.Application.Realtime;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Devices.ZkTeco;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Fiziksel ekipmanin (ZKTeco SC403 + OZAK 720 E + 125 kHz proximity kart) yazilim tarafindaki
/// karsiliklarini dogrular.
/// </summary>
public sealed class HardwareIntegrationTests
{
    // ---- OZAK 720 E fiziksel profili ------------------------------------------------------

    /// <summary>
    /// 720 E kuru kontakla surulur; ag protokolu yoktur. Profilin bir cihaz olarak modellenmesi,
    /// donanim dokumantasyonu §08 ile celisen uydurma bir protokol anlamina gelirdi.
    /// </summary>
    [Fact]
    public void OzakProfileIsNotModelledAsNetworkDevice()
    {
        Assert.False(typeof(IDevice).IsAssignableFrom(typeof(OzakTurnstileProfile)));
        Assert.Equal("Kuru kontak veya TTL/CMOS", OzakTurnstileProfile.ControlInterface);
        Assert.Equal(5, OzakTurnstileProfile.MinControlVoltage);
        Assert.Equal(48, OzakTurnstileProfile.MaxControlVoltage);
    }

    /// <summary>Tek yonlu kurulumda cikis surulemez; cift yonlu bildirilirse surulebilir.</summary>
    [Fact]
    public void DirectionSupportFollowsInstalledConfiguration()
    {
        var oneWay = new OzakTurnstileProfile(SupportsBidirectional: false);
        Assert.True(oneWay.CanDrive(TurnstileDirection.Entry));
        Assert.False(oneWay.CanDrive(TurnstileDirection.Exit));

        var twoWay = new OzakTurnstileProfile(SupportsBidirectional: true);
        Assert.True(twoWay.CanDrive(TurnstileDirection.Exit));
    }

    /// <summary>
    /// Asiri uzun darbe, kontagin turnike bir sonraki gecise hazir olduktan sonra da kapali
    /// kalmasina, yani tek okutmayla birden fazla kisinin gecmesine yol acar.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(10_000)]
    public void OutOfRangeRelayPulseIsRejected(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OzakTurnstileProfile(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void RelayPulseBoundsAreAccepted()
    {
        Assert.Equal(OzakTurnstileProfile.MinRelayPulse,
            new OzakTurnstileProfile(OzakTurnstileProfile.MinRelayPulse).RelayPulse);
        Assert.Equal(OzakTurnstileProfile.MaxRelayPulse,
            new OzakTurnstileProfile(OzakTurnstileProfile.MaxRelayPulse).RelayPulse);
    }

    // ---- 125 kHz proximity kart numarasi --------------------------------------------------

    /// <summary>
    /// Ayni fiziksel kart, firmware surumune gore basta sifirli veya sifirsiz gelebilir; esitlik
    /// karsilastirmasi normalize edilmis deger uzerinden yapilmalidir.
    /// </summary>
    [Theory]
    [InlineData("0008573921", "8573921")]
    [InlineData("8573921", "  8573921  ")]
    [InlineData("0000", "0")]
    public void SameCardIsRecognisedAcrossZeroPadding(string left, string right) =>
        Assert.True(ZkTecoCardNumber.AreEquivalent(left, right));

    [Fact]
    public void DifferentCardsAreNotConflated() =>
        Assert.False(ZkTecoCardNumber.AreEquivalent("8573921", "8573922"));

    /// <summary>Tamamen sifir olan deger bos dizeye indirgenmemelidir.</summary>
    [Fact]
    public void AllZeroCardNormalisesToSingleZero() =>
        Assert.Equal("0", ZkTecoCardNumber.Normalize("0000"));

    [Theory]
    [InlineData("123456789012345678901")]
    [InlineData("1234-5678")]
    [InlineData("kart no")]
    public void MalformedCardNumberIsRejected(string cardNumber) =>
        Assert.Throws<ZkTecoProtocolException>(() => ZkTecoCardNumber.Validate(cardNumber));

    [Fact]
    public void MifareHexCardNumberIsPreserved() =>
        Assert.Equal("A1B2C3", ZkTecoCardNumber.Normalize("a1b2c3"));

    // ---- Cihaz bagimsiz hata siniflandirmasi ----------------------------------------------

    /// <summary>
    /// Siniflandirma saticiya gore yapilirsa her yeni cihaz ailesi sessizce disarida kalir:
    /// kopmus bir SC403 kart kart denenmeye devam eder.
    /// </summary>
    [Theory]
    [InlineData("SF300_DISCONNECTED")]
    [InlineData("ZK_DISCONNECTED")]
    [InlineData("ZK_CONNECT_TIMEOUT")]
    [InlineData("ZK_SDK_NOT_CONFIGURED")]
    [InlineData("DISCONNECTED")]
    public void DisconnectionIsRecognisedForEveryVendor(string errorCode) =>
        Assert.True(DeviceErrorCodes.IsDisconnected(errorCode));

    [Theory]
    [InlineData("SF300_INVALID_CARD")]
    [InlineData("ZK_INVALID_CARD")]
    [InlineData("ZK_MEMORY_FULL")]
    [InlineData("ZK_DEVICE_VALIDATION_REQUIRED")]
    public void PermanentFailureIsRecognisedForEveryVendor(string errorCode) =>
        Assert.True(DeviceErrorCodes.IsPermanent(errorCode));

    /// <summary>Gecici hatalar kalici sayilmamalidir; aksi halde kart bir daha hic denenmez.</summary>
    [Theory]
    [InlineData("ZK_TIMEOUT")]
    [InlineData("ZK_BUSY")]
    [InlineData("SF300_BUSY")]
    [InlineData(null)]
    [InlineData("")]
    public void TransientFailureIsNotClassifiedAsPermanent(string? errorCode)
    {
        Assert.False(DeviceErrorCodes.IsPermanent(errorCode));
        Assert.False(DeviceErrorCodes.IsDisconnected(errorCode));
    }

    /// <summary>
    /// Son ek karsilastirmasi kelime sinirina saygi duymalidir; aksi halde "ZK_NOT_DISCONNECTED"
    /// gibi bir kod yanlislikla kopma sayilir.
    /// </summary>
    [Theory]
    [InlineData("ZKDISCONNECTED")]
    [InlineData("ZK_RECONNECTED")]
    public void SuffixMatchRespectsWordBoundary(string errorCode) =>
        Assert.False(DeviceErrorCodes.IsDisconnected(errorCode));

    // ---- Fabrika baglantisi ---------------------------------------------------------------

    private static DeviceAdapterConfiguration Configuration(bool hasTurnstile) => new(
        Guid.NewGuid(), "SC403 Giris", "SC403", "Ethernet", null, null, "192.168.1.201",
        Sc403Adapter.DefaultPort, hasTurnstile);

    /// <summary>Turnike bagli degilse cihaz yalnizca kart okuyucudur.</summary>
    [Fact]
    public async Task FactoryCreatesCardReaderWhenNoTurnstileAttached()
    {
        var factory = new DeviceAdapterFactory(isDevelopment: false);

        await using var device = factory.Create(Configuration(hasTurnstile: false));

        Assert.IsType<Sc403Adapter>(device, exactMatch: true);
        Assert.IsNotAssignableFrom<ITurnstile>(device);
    }

    /// <summary>Kapi rolesine turnike bagliysa cihaz turnike de surebilmelidir.</summary>
    [Fact]
    public async Task FactoryCreatesTurnstileDriverWhenTurnstileAttached()
    {
        var factory = new DeviceAdapterFactory(isDevelopment: false);

        await using var device = factory.Create(Configuration(hasTurnstile: true));

        var controller = Assert.IsType<Sc403AccessController>(device);
        Assert.Equal(OzakTurnstileProfile.DefaultRelayPulse, controller.TurnstileProfile.RelayPulse);
    }

    /// <summary>Her cihaz kendi SDK oturumunu almalidir; oturum paylasmak yanitlari karistirir.</summary>
    [Fact]
    public async Task EachDeviceReceivesItsOwnSdkSession()
    {
        var created = new List<IZkTecoSdk>();
        var factory = new DeviceAdapterFactory(isDevelopment: false, _ => null, _ =>
        {
            var sdk = new FakeZkTecoSdk();
            created.Add(sdk);
            return sdk;
        });

        await using var first = factory.Create(Configuration(hasTurnstile: false));
        await using var second = factory.Create(Configuration(hasTurnstile: false));

        Assert.Equal(2, created.Count);
        Assert.NotSame(created[0], created[1]);
    }

    [Fact]
    public void DeviceCapacityMatchesManufacturerSpecification()
    {
        Assert.Equal(30_000, Sc403Adapter.MaxCardCapacity);
        Assert.Equal(50_000, Sc403Adapter.MaxTransactionCapacity);
    }

    // ---- Uctan uca: yemek hakki iadesi ----------------------------------------------------

    /// <summary>
    /// Turnike fiziksel olarak acilamadiginda tuketilen yemek hakki IADE EDILMELIDIR.
    ///
    /// TurnstileService iadeyi yalnizca komut BASARISIZ SONUC dondurdugunde ister
    /// (compensateConsumption: isAllowed). Adaptor bunun yerine istisna atarsa akis genel catch
    /// bloguna duser ve orada iade istenmez; ogrenci turnikeden gecemedigi halde hakkini kaybeder.
    /// Bu test gercek Sc403AccessController ile gercek TurnstileService'i birlikte kosar.
    /// </summary>
    [Fact]
    public async Task UndrivableTurnstileRefundsConsumedMealCredit()
    {
        var sdk = new FakeZkTecoSdk();
        await using var controller = new Sc403AccessController(Guid.NewGuid(), "SC403 + 720E",
            new DeviceEndpoint("Ethernet", IpAddress: "192.168.1.201", IpPort: Sc403Adapter.DefaultPort),
            new OzakTurnstileProfile(), sdk, TimeSpan.FromSeconds(1));
        await controller.ConnectAsync(CancellationToken.None);

        var events = new CompensationRecordingEventStore { CompensationResult = true };
        var registry = new TurnstileRegistry();
        registry.Register(controller);
        var service = new TurnstileService(new AllowAccessGateway(), registry, events,
            TimeProvider.System, new NullRealtimePublisher());

        var result = await service.ProcessCardReadAsync(new AccessCheckRequest(
            "8573921", controller.Id, Guid.NewGuid(), DateTimeOffset.UtcNow));

        Assert.True(events.CompensationRequested);
        Assert.Equal(HardwareCommandOutcome.CompensatedRetryRequired, result.HardwareOutcome);
        Assert.Equal("REVIEW_REQUIRED", Assert.Single(events.Events).Result);
    }

    private sealed class CompensationRecordingEventStore : ITurnstileEventStore
    {
        public List<TurnstileEventData> Events { get; } = [];
        public bool CompensationRequested { get; private set; }
        public bool CompensationResult { get; set; }

        public Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent,
            bool compensateConsumption, CancellationToken cancellationToken)
        {
            Events.Add(turnstileEvent);
            if (compensateConsumption) CompensationRequested = true;
            return Task.FromResult(new TurnstileEventWriteResult(compensateConsumption && CompensationResult));
        }
    }

    private sealed class AllowAccessGateway : IAccessDecisionGateway
    {
        public Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessDecision("ALLOW", "Hak mevcut", Guid.NewGuid(), "Test Ogrenci",
                request.DeviceId, request.MealTypeId, request.Timestamp, Guid.NewGuid()));
    }

    private sealed class NullRealtimePublisher : IRealtimeEventPublisher
    {
        public ValueTask PublishAsync(AccessDecisionCommittedEvent realtimeEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PublishAsync(TurnstileResultEvent realtimeEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PublishAsync(DeviceStatusChangedEvent realtimeEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PublishAsync(NotificationEvent realtimeEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
