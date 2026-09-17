using Yemekhane.Application.Access;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.UnitTests.Realtime;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// SAHA: "Izin verildi" yazildi, hak dusuruldu ama kol donmedi; cocuk 3 saniye sonra yeniden
/// okutunca "Bu ogun daha once kullanilmis" diye reddedildi ve kapida kaldi.
///
/// <para>
/// Kural: acma komutu DOGRULANAMADIYSA (zaman asimi / belirsiz hata, iade yok) ayni kartin
/// pencere icindeki yeniden okutmasi yeni karar ALMAZ, hak dusurmez; ayni islem adina kapiyi
/// yeniden dener. Cihaz "acildi" dediyse (SUCCEEDED) bu yola girilmez: kart turnike ustunden
/// geri verilip ikinci kisi gecirilemez.
/// </para>
/// </summary>
public sealed class TurnstileRetryTests
{
    private static readonly Guid MealTypeId = Guid.NewGuid();

    [Fact]
    public async Task TimedOutGrantIsRetriedOnTheNextReadWithoutANewDecision()
    {
        var fixture = CreateFixture();
        fixture.Turnstile.Grant = _ => new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var first = await fixture.Service.ProcessCardReadAsync(fixture.Request, TimeSpan.FromMilliseconds(20));
        Assert.Equal(HardwareCommandOutcome.TimedOut, first.HardwareOutcome);
        Assert.Equal(1, fixture.Registry.Count);

        fixture.Turnstile.Grant = _ => Task.FromResult(new DeviceCommandResult(true, "Açıldı"));
        var second = await fixture.Service.ProcessCardReadAsync(fixture.Request with { OperationId = Guid.NewGuid() });

        Assert.Equal(HardwareCommandOutcome.Succeeded, second.HardwareOutcome);
        // Ikinci okutma YENI KARAR almadi: hak ikinci kez dusmedi.
        Assert.Equal(1, fixture.Gateway.CallCount);
        Assert.Null(second.AccessDecision);
        Assert.Equal(2, fixture.Turnstile.GrantCalls);
        // Basari, ILK islemin adina yazilir: inceleme kaydi kapanir.
        var confirmed = fixture.Events.Events.Last();
        Assert.Equal("SUCCEEDED", confirmed.Result);
        Assert.Equal(first.AccessDecision!.OperationId, confirmed.OperationId);
        Assert.Equal(0, fixture.Registry.Count);
        Assert.Contains("yeniden", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmedGrantIsNotRetriedTheNextReadGoesThroughTheDecision()
    {
        var fixture = CreateFixture();

        var first = await fixture.Service.ProcessCardReadAsync(fixture.Request);
        var second = await fixture.Service.ProcessCardReadAsync(fixture.Request with { OperationId = Guid.NewGuid() });

        Assert.Equal(HardwareCommandOutcome.Succeeded, first.HardwareOutcome);
        Assert.Equal(0, fixture.Registry.Count);
        // Normal yol: ikinci okutma karar aldi (gercek sistemde "daha once kullanilmis" ile reddedilir).
        Assert.Equal(2, fixture.Gateway.CallCount);
        Assert.NotNull(second.AccessDecision);
    }

    [Fact]
    public async Task DefinitiveFailureWithRefundDoesNotArmARetry()
    {
        var fixture = CreateFixture();
        fixture.Events.CompensationResult = true;
        fixture.Turnstile.Grant = _ => Task.FromResult(new DeviceCommandResult(false, "Röle sürülemedi", "ZK_FAIL"));

        var first = await fixture.Service.ProcessCardReadAsync(fixture.Request);

        // Hak IADE edildi: bir sonraki okutma normal karar yolundan gecer, yeniden deneme kaydi gereksiz.
        Assert.Equal(HardwareCommandOutcome.CompensatedRetryRequired, first.HardwareOutcome);
        Assert.Equal(0, fixture.Registry.Count);
    }

    [Fact]
    public async Task ExpiredRetryWindowFallsBackToTheDecision()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.FromHours(3)));
        var fixture = CreateFixture(clock);
        fixture.Turnstile.Grant = _ => new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await fixture.Service.ProcessCardReadAsync(fixture.Request, TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, fixture.Registry.Count);

        clock.Now += UnconfirmedGrantRegistry.Window + TimeSpan.FromSeconds(1);
        fixture.Turnstile.Grant = _ => Task.FromResult(new DeviceCommandResult(true, "Açıldı"));
        var second = await fixture.Service.ProcessCardReadAsync(fixture.Request with { OperationId = Guid.NewGuid() });

        // Pencere gecti: cocuk cikip gitmis olabilir; yeniden okutma normal karara doner.
        Assert.Equal(2, fixture.Gateway.CallCount);
        Assert.NotNull(second.AccessDecision);
    }

    [Fact]
    public async Task ARetryThatFailsAgainKeepsTheCardArmedForOneMoreRead()
    {
        var fixture = CreateFixture();
        fixture.Turnstile.Grant = _ => new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await fixture.Service.ProcessCardReadAsync(fixture.Request, TimeSpan.FromMilliseconds(20));

        var second = await fixture.Service.ProcessCardReadAsync(fixture.Request with { OperationId = Guid.NewGuid() },
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(HardwareCommandOutcome.TimedOut, second.HardwareOutcome);
        Assert.Equal(1, fixture.Gateway.CallCount);
        Assert.Equal(1, fixture.Registry.Count);
        Assert.Equal("REVIEW_REQUIRED", fixture.Events.Events.Last().Result);
    }

    private static Fixture CreateFixture(TimeProvider? clock = null)
    {
        var turnstile = new FakeTurnstile(Guid.NewGuid());
        var gateway = new FakeAccessGateway();
        var events = new FakeTurnstileEventStore();
        var publisher = new RecordingRealtimeEventPublisher();
        var registry = new TurnstileRegistry();
        registry.Register(turnstile, new HashSet<DeviceCapability> { DeviceCapability.GrantAccess, DeviceCapability.DenyAccess });
        var unconfirmed = new UnconfirmedGrantRegistry(clock);
        var service = new TurnstileService(gateway, registry, events, clock ?? TimeProvider.System, publisher,
            unconfirmedGrants: unconfirmed);
        var request = new AccessCheckRequest("8247129", turnstile.Id, MealTypeId, DateTimeOffset.UtcNow, OperationId: Guid.NewGuid());
        return new Fixture(service, request, turnstile, gateway, events, unconfirmed);
    }

    private sealed record Fixture(TurnstileService Service, AccessCheckRequest Request, FakeTurnstile Turnstile,
        FakeAccessGateway Gateway, FakeTurnstileEventStore Events, UnconfirmedGrantRegistry Registry);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeAccessGateway : IAccessDecisionGateway
    {
        public int CallCount { get; private set; }

        public Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AccessDecision("ALLOW", "Geçiş onaylandı", Guid.NewGuid(), "Test Öğrenci",
                request.DeviceId, request.MealTypeId, request.Timestamp, request.OperationId ?? Guid.NewGuid()));
        }
    }

    private sealed class FakeTurnstile(Guid id) : ITurnstile
    {
        public Func<CancellationToken, Task<DeviceCommandResult>> Grant { get; set; } =
            _ => Task.FromResult(new DeviceCommandResult(true, "Açıldı"));
        public int GrantCalls { get; private set; }
        public Guid Id { get; } = id;
        public string Name => "Fake turnike";
        public DeviceEndpoint Endpoint { get; } = new("Fake");
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken)
        { GrantCalls++; return Grant(cancellationToken); }
        public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "Kilitli"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTurnstileEventStore : ITurnstileEventStore
    {
        public List<TurnstileEventData> Events { get; } = [];
        public bool CompensationResult { get; set; }

        public Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent, bool compensateConsumption,
            CancellationToken cancellationToken)
        {
            Events.Add(turnstileEvent);
            return Task.FromResult(new TurnstileEventWriteResult(compensateConsumption && CompensationResult));
        }
    }
}
