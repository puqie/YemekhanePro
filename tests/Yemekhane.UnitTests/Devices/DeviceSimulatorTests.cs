using Yemekhane.Application.Access;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Simulators;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.UnitTests.Realtime;

namespace Yemekhane.UnitTests.Devices;

public sealed class DeviceSimulatorTests
{
    private static readonly DateTimeOffset TestTime = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CardReaderPublishesCardUnknownPayloadAndDoubleReadInOrder()
    {
        await using var reader = CreateReader();
        await reader.ConnectAsync(CancellationToken.None);
        await using var enumerator = reader.ReadCardsAsync(CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);

        reader.ScanCard("CARD-1");
        reader.ScanUnknownCard("UNMAPPED-PAYLOAD");
        reader.ScanCardTwice("CARD-2");

        var cards = new List<CardReadEvent>();
        for (var index = 0; index < 4; index++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            cards.Add(enumerator.Current);
        }

        Assert.Equal(["CARD-1", "UNMAPPED-PAYLOAD", "CARD-2", "CARD-2"],
            cards.Select(card => card.CardNumber));
        Assert.All(cards, card => Assert.Equal(TestTime, card.Timestamp));
    }

    [Fact]
    public async Task CardReaderRemoteDisconnectEndsStreamWithErrorAndCanReconnect()
    {
        await using var reader = CreateReader();
        await reader.ConnectAsync(CancellationToken.None);
        await using var firstStream = reader.ReadCardsAsync(CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);

        var pendingRead = firstStream.MoveNextAsync().AsTask();
        reader.RemoteDisconnect();

        var exception = await Assert.ThrowsAsync<DeviceConnectionException>(() => pendingRead);
        Assert.Equal("SIMULATOR_REMOTE_DISCONNECT", exception.ErrorCode);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);

        await reader.ConnectAsync(CancellationToken.None);
        reader.ScanCard("AFTER-RECONNECT");
        await using var secondStream = reader.ReadCardsAsync(CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await secondStream.MoveNextAsync());
        Assert.Equal("AFTER-RECONNECT", secondStream.Current.CardNumber);
    }

    [Fact]
    public async Task OfflineAndConnectionFailureAreDeterministicallyControlled()
    {
        await using var reader = CreateReader();
        reader.GoOffline();

        var offline = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));
        Assert.Equal("SIMULATOR_OFFLINE", offline.ErrorCode);

        reader.GoOnline();
        reader.FailNextConnection();
        var failure = await Assert.ThrowsAsync<DeviceConnectionException>(
            () => reader.ConnectAsync(CancellationToken.None));
        Assert.Equal("SIMULATOR_CONNECT_FAILED", failure.ErrorCode);

        await reader.ConnectAsync(CancellationToken.None);
        Assert.Equal(DeviceConnectionState.Connected, reader.ConnectionState);
    }

    [Fact]
    public async Task CardStreamCancellationAndLifecycleAreIdempotent()
    {
        await using var reader = CreateReader();
        await reader.ConnectAsync(CancellationToken.None);
        await reader.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = reader.ReadCardsAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await reader.DisconnectAsync(CancellationToken.None);
        await reader.DisconnectAsync(CancellationToken.None);
        Assert.Equal(DeviceConnectionState.Disconnected, reader.ConnectionState);
    }

    [Fact]
    public async Task TurnstileRecordsAllowDenyAndFailureResults()
    {
        await using var turnstile = CreateTurnstile();
        await turnstile.ConnectAsync(CancellationToken.None);
        turnstile.EnqueueCommandBehavior(SimulatorCommandBehavior.Succeed);
        turnstile.EnqueueCommandBehavior(SimulatorCommandBehavior.Succeed);
        turnstile.EnqueueCommandBehavior(SimulatorCommandBehavior.Fail);

        var allow = await turnstile.GrantAccessAsync(TurnstileDirection.Entry, CancellationToken.None);
        var deny = await turnstile.DenyAccessAsync(TurnstileDirection.Exit, CancellationToken.None);
        var failure = await turnstile.GrantAccessAsync(TurnstileDirection.Exit, CancellationToken.None);

        Assert.True(allow.Succeeded);
        Assert.True(deny.Succeeded);
        Assert.False(failure.Succeeded);
        Assert.Equal("SIMULATOR_COMMAND_FAILED", failure.ErrorCode);
        Assert.Equal(
            [SimulatorTurnstileCommand.Grant, SimulatorTurnstileCommand.Deny, SimulatorTurnstileCommand.Grant],
            turnstile.CommandHistory.Select(entry => entry.Command));
        Assert.Equal([1L, 2L, 3L], turnstile.CommandHistory.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task TurnstileTimeoutHonorsCancellationAndIsRecorded()
    {
        await using var turnstile = CreateTurnstile();
        await turnstile.ConnectAsync(CancellationToken.None);
        turnstile.EnqueueCommandBehavior(SimulatorCommandBehavior.Timeout);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => turnstile.GrantAccessAsync(TurnstileDirection.Entry, timeout.Token));

        var command = Assert.Single(turnstile.CommandHistory);
        Assert.Equal(SimulatorCommandBehavior.Timeout, command.Behavior);
        Assert.Null(command.Result);
        Assert.NotNull(command.CompletedAt);
    }

    [Fact]
    public async Task DeviceManagerReconnectsSimulatorAfterRemoteDisconnect()
    {
        var registry = new TurnstileRegistry();
        await using var manager = new DeviceManager(deviceRegistry: new DeviceRegistry(), turnstileRegistry: registry);
        var turnstile = CreateTurnstile();
        Assert.True(manager.Register(turnstile, new DeviceRegistrationOptions(AutoConnect: false)));

        await manager.ConnectAsync(turnstile.Id, CancellationToken.None);
        turnstile.RemoteDisconnect();
        var disconnected = await manager.TestAsync(turnstile.Id, CancellationToken.None);
        await manager.ReconnectAsync(turnstile.Id, CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Disconnected, disconnected.State);
        Assert.Equal(DeviceConnectionState.Connected, turnstile.ConnectionState);
        Assert.True(registry.Supports(turnstile.Id, DeviceCapability.DenyAccess));
    }

    [Theory]
    [InlineData("ALLOW", SimulatorTurnstileCommand.Grant)]
    [InlineData("DENY", SimulatorTurnstileCommand.Deny)]
    public async Task TurnstileServiceExecutesDecisionAgainstRegisteredSimulator(
        string decision, SimulatorTurnstileCommand expectedCommand)
    {
        var registry = new TurnstileRegistry();
        await using var manager = new DeviceManager(turnstileRegistry: registry);
        var turnstile = CreateTurnstile();
        manager.Register(turnstile, new DeviceRegistrationOptions(AutoConnect: false));
        await manager.ConnectAsync(turnstile.Id, CancellationToken.None);
        var events = new RecordingEventStore();
        var service = new TurnstileService(new FixedDecisionGateway(decision), registry, events,
            new FixedTimeProvider(TestTime), new RecordingRealtimeEventPublisher());
        var request = new AccessCheckRequest("CARD-1", turnstile.Id, Guid.NewGuid(), TestTime);

        var result = await service.ProcessCardReadAsync(request, cancellationToken: CancellationToken.None);

        Assert.Equal(HardwareCommandOutcome.Succeeded, result.HardwareOutcome);
        Assert.Equal(expectedCommand, Assert.Single(turnstile.CommandHistory).Command);
        Assert.Equal("SUCCEEDED", Assert.Single(events.Events).Result);
    }

    [Theory]
    [InlineData(SimulatorCommandBehavior.Fail, HardwareCommandOutcome.CompensatedRetryRequired)]
    [InlineData(SimulatorCommandBehavior.Timeout, HardwareCommandOutcome.TimedOut)]
    public async Task TurnstileServiceHandlesSimulatorCommandFailureModes(
        SimulatorCommandBehavior behavior, HardwareCommandOutcome expectedOutcome)
    {
        var registry = new TurnstileRegistry();
        await using var turnstile = CreateTurnstile();
        await turnstile.ConnectAsync(CancellationToken.None);
        registry.Register(turnstile);
        turnstile.EnqueueCommandBehavior(behavior);
        var events = new RecordingEventStore { Compensate = true };
        var service = new TurnstileService(new FixedDecisionGateway("ALLOW"), registry, events,
            new FixedTimeProvider(TestTime), new RecordingRealtimeEventPublisher());
        var request = new AccessCheckRequest("CARD-1", turnstile.Id, Guid.NewGuid(), TestTime);

        var result = await service.ProcessCardReadAsync(request, TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(expectedOutcome, result.HardwareOutcome);
        Assert.Equal("REVIEW_REQUIRED", Assert.Single(events.Events).Result);
        Assert.Single(turnstile.CommandHistory);
    }

    [Fact]
    public void RealEndpointsCannotAccidentallyConstructSimulators()
    {
        Assert.Throws<ArgumentException>(() =>
            new SimulatorCardReader(Guid.NewGuid(), "reader", new DeviceEndpoint("COM")));
        Assert.Throws<ArgumentException>(() =>
            new SimulatorTurnstile(Guid.NewGuid(), "turnstile", new DeviceEndpoint("Ethernet")));
    }

    private static SimulatorCardReader CreateReader() => new(Guid.NewGuid(), "Simulator reader",
        new DeviceEndpoint("Simulator"), new FixedTimeProvider(TestTime));

    private static SimulatorTurnstile CreateTurnstile() => new(Guid.NewGuid(), "Simulator turnstile",
        new DeviceEndpoint("Simulator"), new FixedTimeProvider(TestTime));

    private sealed class FixedTimeProvider(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
    }

    private sealed class FixedDecisionGateway(string decision) : IAccessDecisionGateway
    {
        public Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccessDecision(decision, "Simulator decision", Guid.NewGuid(),
                "Simulator user", request.DeviceId, request.MealTypeId, request.Timestamp, Guid.NewGuid()));
        }
    }

    private sealed class RecordingEventStore : ITurnstileEventStore
    {
        public List<TurnstileEventData> Events { get; } = [];
        public bool Compensate { get; init; }

        public Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent,
            bool compensateConsumption, CancellationToken cancellationToken)
        {
            Events.Add(turnstileEvent);
            return Task.FromResult(new TurnstileEventWriteResult(compensateConsumption && Compensate));
        }
    }
}
