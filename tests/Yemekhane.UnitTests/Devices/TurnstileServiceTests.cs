using Yemekhane.Application.Access;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.UnitTests.Realtime;

namespace Yemekhane.UnitTests.Devices;

public sealed class TurnstileServiceTests
{
    private static readonly Guid MealTypeId = Guid.NewGuid();

    [Fact]
    public async Task AllowDecisionGrantsAccess()
    {
        var fixture = CreateFixture("ALLOW");

        var result = await fixture.Service.ProcessCardReadAsync(fixture.Request);

        Assert.Equal(HardwareCommandOutcome.Succeeded, result.HardwareOutcome);
        Assert.Equal(1, fixture.Turnstile.GrantCalls);
        Assert.Equal(0, fixture.Turnstile.DenyCalls);
        Assert.Equal("SUCCEEDED", Assert.Single(fixture.Events.Events).Result);
        var realtimeEvent = Assert.Single(fixture.Publisher.TurnstileResults);
        Assert.Equal(result.AccessDecision!.OperationId, realtimeEvent.OperationId);
        Assert.Equal(fixture.Request.DeviceId, realtimeEvent.DeviceId);
        Assert.Equal("GRANT", realtimeEvent.Command);
        Assert.Equal("SUCCEEDED", realtimeEvent.Result);
    }

    [Fact]
    public async Task DenyDecisionSendsDenyWhenSupported()
    {
        var fixture = CreateFixture("DENY");

        var result = await fixture.Service.ProcessCardReadAsync(fixture.Request);

        Assert.Equal(HardwareCommandOutcome.Succeeded, result.HardwareOutcome);
        Assert.Equal(0, fixture.Turnstile.GrantCalls);
        Assert.Equal(1, fixture.Turnstile.DenyCalls);
        Assert.Contains("Erişim reddedildi", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectedDeviceDoesNotRequestOrGrantDecision()
    {
        var fixture = CreateFixture("ALLOW", DeviceConnectionState.Disconnected);

        var result = await fixture.Service.ProcessCardReadAsync(fixture.Request);

        Assert.Equal(HardwareCommandOutcome.Disconnected, result.HardwareOutcome);
        Assert.Null(result.AccessDecision);
        Assert.Equal(0, fixture.Gateway.CallCount);
        Assert.Equal(0, fixture.Turnstile.GrantCalls);
    }

    [Fact]
    public async Task GrantTimeoutNeverReportsSuccessAndCreatesReviewEvent()
    {
        var fixture = CreateFixture("ALLOW");
        fixture.Turnstile.Grant = _ => new TaskCompletionSource<DeviceCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

        var result = await fixture.Service.ProcessCardReadAsync(fixture.Request, TimeSpan.FromMilliseconds(20));

        Assert.Equal(HardwareCommandOutcome.TimedOut, result.HardwareOutcome);
        Assert.Equal("REVIEW_REQUIRED", Assert.Single(fixture.Events.Events).Result);
        Assert.False(fixture.Events.CompensationRequested);
    }

    [Fact]
    public async Task DefinitiveGrantFailureRequestsSafeCompensationAndRetry()
    {
        var fixture = CreateFixture("ALLOW");
        fixture.Turnstile.Grant = _ => Task.FromResult(new DeviceCommandResult(false, "Röle yanıt vermedi", "RELAY_FAILED"));
        fixture.Events.CompensationResult = true;

        var result = await fixture.Service.ProcessCardReadAsync(fixture.Request);

        Assert.Equal(HardwareCommandOutcome.CompensatedRetryRequired, result.HardwareOutcome);
        Assert.False(result.CommandResult!.Succeeded);
        Assert.True(fixture.Events.CompensationRequested);
        Assert.Contains("iade edildi", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportExceptionIsAmbiguousAndRequiresReviewWithoutCompensation()
    {
        var fixture = CreateFixture("ALLOW");
        fixture.Turnstile.Grant = _ => throw new IOException("socket closed after write");

        var result = await fixture.Service.ProcessCardReadAsync(fixture.Request);

        Assert.Equal(HardwareCommandOutcome.ReviewRequired, result.HardwareOutcome);
        Assert.False(fixture.Events.CompensationRequested);
        Assert.Equal("REVIEW_REQUIRED", Assert.Single(fixture.Events.Events).Result);
        Assert.DoesNotContain("Exception occurred", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedEventPersistenceDoesNotPublishTurnstileResult()
    {
        var fixture = CreateFixture("ALLOW");
        fixture.Events.ThrowOnRecord = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ProcessCardReadAsync(fixture.Request));

        Assert.Empty(fixture.Publisher.TurnstileResults);
    }

    private static Fixture CreateFixture(string decision,
        DeviceConnectionState state = DeviceConnectionState.Connected)
    {
        var turnstile = new FakeTurnstile(Guid.NewGuid(), state);
        var gateway = new FakeAccessGateway(decision);
        var events = new FakeTurnstileEventStore();
        var publisher = new RecordingRealtimeEventPublisher();
        var registry = new TurnstileRegistry();
        registry.Register(turnstile, new HashSet<DeviceCapability>
        {
            DeviceCapability.GrantAccess,
            DeviceCapability.DenyAccess
        });
        var service = new TurnstileService(gateway, registry, events, TimeProvider.System, publisher);
        var request = new AccessCheckRequest("1234", turnstile.Id, MealTypeId, DateTimeOffset.UtcNow);
        return new Fixture(service, request, turnstile, gateway, events, publisher);
    }

    private sealed record Fixture(TurnstileService Service, AccessCheckRequest Request,
        FakeTurnstile Turnstile, FakeAccessGateway Gateway, FakeTurnstileEventStore Events,
        RecordingRealtimeEventPublisher Publisher);

    private sealed class FakeAccessGateway(string decision) : IAccessDecisionGateway
    {
        public int CallCount { get; private set; }

        public Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AccessDecision(decision,
                decision == "ALLOW" ? "Geçiş onaylandı" : "Yemek hakkı yok", Guid.NewGuid(), "Test Öğrenci",
                request.DeviceId, request.MealTypeId, request.Timestamp, Guid.NewGuid()));
        }
    }

    private sealed class FakeTurnstile(Guid id, DeviceConnectionState state) : ITurnstile
    {
        public Func<CancellationToken, Task<DeviceCommandResult>> Grant { get; set; } =
            _ => Task.FromResult(new DeviceCommandResult(true, "Açıldı"));
        public Func<CancellationToken, Task<DeviceCommandResult>> Deny { get; set; } =
            _ => Task.FromResult(new DeviceCommandResult(true, "Kilitli"));
        public int GrantCalls { get; private set; }
        public int DenyCalls { get; private set; }
        public Guid Id { get; } = id;
        public string Name => "Fake turnike";
        public DeviceEndpoint Endpoint { get; } = new("Fake");
        public DeviceConnectionState ConnectionState { get; } = state;
        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction,
            CancellationToken cancellationToken)
        {
            GrantCalls++;
            return Grant(cancellationToken);
        }

        public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction,
            CancellationToken cancellationToken)
        {
            DenyCalls++;
            return Deny(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTurnstileEventStore : ITurnstileEventStore
    {
        public List<TurnstileEventData> Events { get; } = [];
        public bool CompensationRequested { get; private set; }
        public bool CompensationResult { get; set; }
        public bool ThrowOnRecord { get; set; }

        public Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent,
            bool compensateConsumption, CancellationToken cancellationToken)
        {
            if (ThrowOnRecord) throw new InvalidOperationException("database failed");
            Events.Add(turnstileEvent);
            CompensationRequested = compensateConsumption;
            return Task.FromResult(new TurnstileEventWriteResult(compensateConsumption && CompensationResult));
        }
    }
}
