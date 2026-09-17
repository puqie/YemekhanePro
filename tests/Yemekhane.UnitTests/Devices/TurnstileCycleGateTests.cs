using System.Diagnostics;
using Yemekhane.Application.Access;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.UnitTests.Realtime;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// SAHA: turnikenin bir gecisten sonra ~5 sn mesgul suresi var; o surede gelen okutmaya program
/// "Izin verildi" deyip hakki dusuruyor, cihaz komutu onayliyor ama kol donmuyor -- hak yaniyor.
///
/// <para>
/// Kural: son acma komutundan sonra dongu dolmadan gelen okutma KARAR ALINMADAN bekletilir; hak
/// ancak turnike hazir olunca duser. Zamanlama bildirmeyen turnikede (sahte/eski) bekleme yoktur.
/// </para>
/// </summary>
public sealed class TurnstileCycleGateTests
{
    private static readonly Guid MealTypeId = Guid.NewGuid();

    [Fact]
    public async Task WaitsOutTheRemainingCycleAfterACommand()
    {
        var gate = new TurnstileCycleGate();
        var device = Guid.NewGuid();
        gate.MarkCommand(device);

        var watch = Stopwatch.StartNew();
        await gate.WaitUntilReadyAsync(device, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(watch.ElapsedMilliseconds >= 150, $"Bekleme cok kisa: {watch.ElapsedMilliseconds} ms.");
        // Windows sistem saati ~15 ms cozunurluklu: zamanlayici dolunca kalan sure tam sifir olmayabilir.
        Assert.True(gate.Remaining(device, TimeSpan.FromMilliseconds(200)) < TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task DoesNotWaitWithoutAPriorCommandOrWhenTheCycleElapsed()
    {
        var gate = new TurnstileCycleGate();
        var fresh = Guid.NewGuid();
        var watch = Stopwatch.StartNew();
        await gate.WaitUntilReadyAsync(fresh, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(watch.ElapsedMilliseconds < 100, "Komut gonderilmemis cihazda bekleme olmamali.");

        var used = Guid.NewGuid();
        gate.MarkCommand(used);
        await Task.Delay(120);
        Assert.Equal(TimeSpan.Zero, gate.Remaining(used, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.Zero, gate.Remaining(used, TimeSpan.Zero));
    }

    [Fact]
    public async Task TheSecondReadIsHeldUntilTheTurnstileIsReadyBeforeAnyDecision()
    {
        var fixture = CreateFixture(TimeSpan.FromMilliseconds(250));

        var first = await fixture.Service.ProcessCardReadAsync(fixture.Request("1111"));
        var second = await fixture.Service.ProcessCardReadAsync(fixture.Request("2222"));

        Assert.Equal(HardwareCommandOutcome.Succeeded, first.HardwareOutcome);
        Assert.Equal(HardwareCommandOutcome.Succeeded, second.HardwareOutcome);
        Assert.Equal(2, fixture.Gateway.CallCount);
        // Ikinci KARAR, ilk acma komutundan en az dongu suresi sonra alindi: hak turnike hazirken dustu.
        var held = fixture.Gateway.DecisionTimes[1] - fixture.Turnstile.GrantTimes[0];
        Assert.True(held >= TimeSpan.FromMilliseconds(200), $"Ikinci karar dongu dolmadan alindi: {held.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public async Task ATurnstileWithoutTimingIsNotHeld()
    {
        var fixture = CreateFixture(cycle: null);

        await fixture.Service.ProcessCardReadAsync(fixture.Request("1111"));
        await fixture.Service.ProcessCardReadAsync(fixture.Request("2222"));

        var gap = fixture.Gateway.DecisionTimes[1] - fixture.Turnstile.GrantTimes[0];
        Assert.True(gap < TimeSpan.FromMilliseconds(150), $"Zamanlama bildirmeyen turnikede bekleme olmamali: {gap.TotalMilliseconds:0} ms.");
    }

    private static Fixture CreateFixture(TimeSpan? cycle)
    {
        ITurnstile turnstile = cycle is { } value ? new TimedTurnstile(Guid.NewGuid(), value) : new PlainTurnstile(Guid.NewGuid());
        var gateway = new RecordingGateway();
        var registry = new TurnstileRegistry();
        registry.Register(turnstile, new HashSet<DeviceCapability> { DeviceCapability.GrantAccess, DeviceCapability.DenyAccess });
        var service = new TurnstileService(gateway, registry, new NullEventStore(), TimeProvider.System,
            new RecordingRealtimeEventPublisher(), cycleGate: new TurnstileCycleGate());
        return new Fixture(service, gateway, (ITimedFake)turnstile, turnstile.Id);
    }

    private sealed record Fixture(TurnstileService Service, RecordingGateway Gateway, ITimedFake Turnstile, Guid DeviceId)
    {
        public AccessCheckRequest Request(string card) => new(card, DeviceId, MealTypeId, DateTimeOffset.UtcNow, OperationId: Guid.NewGuid());
    }

    private interface ITimedFake { List<DateTimeOffset> GrantTimes { get; } }

    private sealed class RecordingGateway : IAccessDecisionGateway
    {
        public int CallCount { get; private set; }
        public List<DateTimeOffset> DecisionTimes { get; } = [];

        public Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            DecisionTimes.Add(DateTimeOffset.UtcNow);
            return Task.FromResult(new AccessDecision("ALLOW", "Geçiş onaylandı", Guid.NewGuid(), "Test",
                request.DeviceId, request.MealTypeId, request.Timestamp, request.OperationId ?? Guid.NewGuid()));
        }
    }

    private class PlainTurnstile(Guid id) : ITurnstile, ITimedFake
    {
        public List<DateTimeOffset> GrantTimes { get; } = [];
        public Guid Id { get; } = id;
        public string Name => "Fake turnike";
        public DeviceEndpoint Endpoint { get; } = new("Fake");
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken)
        { GrantTimes.Add(DateTimeOffset.UtcNow); return Task.FromResult(new DeviceCommandResult(true, "Açıldı")); }
        public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "Kilitli"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimedTurnstile(Guid id, TimeSpan cycle) : PlainTurnstile(id), ITurnstileTiming
    {
        public TimeSpan MinimumCommandInterval => cycle;
    }

    private sealed class NullEventStore : ITurnstileEventStore
    {
        public Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent, bool compensateConsumption,
            CancellationToken cancellationToken) => Task.FromResult(new TurnstileEventWriteResult(false));
    }
}
