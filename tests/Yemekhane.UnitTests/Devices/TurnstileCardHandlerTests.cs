using Microsoft.Extensions.Logging.Abstractions;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Access;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Realtime;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Okutma → ogun secimi → TurnstileService. Gercek TurnstileService ile kosar; yalnizca karar
/// kapisi, turnike ve depo sahtedir.
/// </summary>
public sealed class TurnstileCardHandlerTests
{
    // 09:15 UTC = 12:15 Istanbul: ogle penceresi.
    private static readonly DateTimeOffset LunchTimeUtc = new(2026, 9, 8, 9, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task SwipeInsideLunchWindowChargesLunchAndOpensTheTurnstile()
    {
        var harness = new Harness(LunchTimeUtc);
        var breakfast = harness.AddMeal("Kahvaltı", "07:00", "09:00");
        var lunch = harness.AddMeal("Öğle", "11:30", "13:30");
        harness.AddMeal("Akşam", "12:00", "14:00", active: false);

        var result = await harness.Handler.HandleAsync(harness.DeviceId,
            new CardReadEvent("8247129", LunchTimeUtc, "SC403"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(HardwareCommandOutcome.Succeeded, result!.HardwareOutcome);
        var request = Assert.Single(harness.Gateway.Requests);
        Assert.Equal(lunch.Id, request.MealTypeId);
        Assert.NotEqual(breakfast.Id, request.MealTypeId);
        Assert.Equal("8247129", request.CardNumber);
        Assert.Equal(harness.DeviceId, request.DeviceId);
        Assert.Equal("Entry", request.Direction);
        Assert.Equal("SC403", request.ReaderSource);
        Assert.Equal(LunchTimeUtc, request.Timestamp);
        Assert.NotNull(request.OperationId);
        Assert.Equal(1, harness.Turnstile.Grants);
        Assert.Equal(0, harness.Turnstile.Denies);
    }

    /// <summary>
    /// Birden cok ogun varken hicbiri saati kapsamiyorsa hak DUSMEZ ve turnike kapali kalir;
    /// ama okutma GORUNUR bir red olarak kaydedilir ve canli yayinlanir (once sessizce dusuyordu).
    /// </summary>
    [Fact]
    public async Task NoMealWindowRecordsAVisibleDenialWithoutChargingOrOpening()
    {
        var harness = new Harness(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)); // 15:00 Istanbul
        harness.AddMeal("Öğle", "11:30", "13:30");
        harness.AddMeal("Akşam", "18:00", "20:00");

        var result = await harness.Handler.HandleAsync(harness.DeviceId,
            new CardReadEvent("8247129", LunchTimeUtc, "SC403"), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(harness.Gateway.Requests);
        Assert.Equal(0, harness.Turnstile.Grants);
        var (request, denied) = Assert.Single(harness.Decisions.Denied);
        Assert.Equal("8247129", request.CardNumber);
        Assert.Equal(Guid.Empty, request.MealTypeId);
        Assert.Equal("Entry", request.Direction);
        Assert.Equal("DENY", denied.Decision);
        Assert.Equal(TurnstileCardHandler.NoMealWindowReason, denied.Reason);
        var published = Assert.Single(harness.Publisher.Committed);
        Assert.Equal(denied.OperationId, published.OperationId);
        Assert.Equal("DENY", published.Decision);
    }

    /// <summary>Tek ogunlu okulda saat sorulmaz: 11:10'da okutulan kart o ogunu duser (sahadaki sikayet).</summary>
    [Fact]
    public async Task SingleMealOutsideItsHoursIsStillCharged()
    {
        var harness = new Harness(new DateTimeOffset(2026, 9, 8, 8, 10, 0, TimeSpan.Zero)); // 11:10 Istanbul
        var lunch = harness.AddMeal("Öğle", "11:30", "12:30");

        var result = await harness.Handler.HandleAsync(harness.DeviceId,
            new CardReadEvent("8247129", LunchTimeUtc, "SC403"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(lunch.Id, Assert.Single(harness.Gateway.Requests).MealTypeId);
        Assert.Equal(1, harness.Turnstile.Grants);
        Assert.Empty(harness.Decisions.Denied);
    }

    /// <summary>Tek ogunlu okul saat girmemis olabilir; o ogun her saat gecerlidir.</summary>
    [Fact]
    public async Task SingleMealWithoutHoursIsUsedAtAnyTime()
    {
        var harness = new Harness(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var meal = harness.AddMeal("Yemek");

        await harness.Handler.HandleAsync(harness.DeviceId,
            new CardReadEvent("1", LunchTimeUtc, "SC403"), CancellationToken.None);

        Assert.Equal(meal.Id, Assert.Single(harness.Gateway.Requests).MealTypeId);
    }

    [Fact]
    public async Task ExitDeviceSendsExitDirection()
    {
        var harness = new Harness(LunchTimeUtc) { Direction = "Exit" };
        harness.AddMeal("Öğle", "11:30", "13:30");

        await harness.Handler.HandleAsync(harness.DeviceId,
            new CardReadEvent("1", LunchTimeUtc, "SC403"), CancellationToken.None);

        Assert.Equal("Exit", Assert.Single(harness.Gateway.Requests).Direction);
    }

    [Fact]
    public async Task DeniedDecisionPulsesDenyNotGrant()
    {
        var harness = new Harness(LunchTimeUtc);
        harness.Gateway.Decision = "DENY";
        harness.AddMeal("Öğle", "11:30", "13:30");

        var result = await harness.Handler.HandleAsync(harness.DeviceId,
            new CardReadEvent("1", LunchTimeUtc, "SC403"), CancellationToken.None);

        Assert.Equal("DENY", result!.AccessDecision!.Decision);
        Assert.Equal(0, harness.Turnstile.Grants);
        Assert.Equal(1, harness.Turnstile.Denies);
    }

    private sealed class Harness
    {
        public Harness(DateTimeOffset nowUtc)
        {
            var clock = new FixedClock(nowUtc);
            var registry = new TurnstileRegistry();
            registry.Register(Turnstile);
            DeviceId = Turnstile.Id;
            var service = new TurnstileService(Gateway, registry, new NullEventStore(), clock, Publisher);
            Handler = new TurnstileCardHandler(service, Meals, new FixedDirectory(this), Decisions, Publisher, clock,
                NullLogger<TurnstileCardHandler>.Instance);
        }

        public Guid DeviceId { get; }
        public FakeDecisions Decisions { get; } = new();
        public RecordingPublisher Publisher { get; } = new();
        public string Direction { get; init; } = "Entry";
        public FakeMealTypes Meals { get; } = new();
        public RecordingGateway Gateway { get; } = new();
        public FakeTurnstile Turnstile { get; } = new();
        public TurnstileCardHandler Handler { get; }

        public MealTypeDetails AddMeal(string name, string? starts = null, string? ends = null, bool active = true)
        {
            var meal = new MealTypeDetails(Guid.NewGuid(), name,
                starts is null ? null : TimeOnly.Parse(starts, System.Globalization.CultureInfo.InvariantCulture),
                ends is null ? null : TimeOnly.Parse(ends, System.Globalization.CultureInfo.InvariantCulture), active);
            Meals.Items.Add(meal);
            return meal;
        }

        private sealed class FixedDirectory(Harness harness) : ITurnstileDeviceDirectory
        {
            public Task<string?> GetDirectionAsync(Guid deviceId, CancellationToken cancellationToken) =>
                Task.FromResult<string?>(deviceId == harness.DeviceId ? harness.Direction : null);
        }
    }

    private sealed class FakeMealTypes : IMealTypeRepository
    {
        public List<MealTypeDetails> Items { get; } = [];

        public Task<IReadOnlyList<MealTypeDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>(Items.Where(meal => includeInactive || meal.IsActive).ToList());

        public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MealTypeDetails> AddAsync(SaveMealTypeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MealTypeDetails?> UpdateAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingGateway : IAccessDecisionGateway
    {
        public string Decision { get; set; } = "ALLOW";
        public List<AccessCheckRequest> Requests { get; } = [];

        public Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AccessDecision(Decision, "test", Guid.NewGuid(), "Test Öğrenci",
                request.DeviceId, request.MealTypeId, request.Timestamp, request.OperationId ?? Guid.NewGuid()));
        }
    }

    private sealed class FakeTurnstile : ITurnstile, IDeviceCapabilityProvider
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Yemekhane Girişi";
        public DeviceEndpoint Endpoint { get; } = new("Ethernet", IpAddress: "169.254.198.201", IpPort: 4370);
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public IReadOnlySet<DeviceCapability> Capabilities { get; } =
            new HashSet<DeviceCapability> { DeviceCapability.GrantAccess, DeviceCapability.DenyAccess };
        public int Grants { get; private set; }
        public int Denies { get; private set; }

        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceInfo("SC403", null, null, Capabilities));

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceStatus(ConnectionState, DateTimeOffset.UtcNow));

        public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken)
        {
            Grants++;
            return Task.FromResult(new DeviceCommandResult(true, "Röle sürüldü."));
        }

        public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken)
        {
            Denies++;
            return Task.FromResult(new DeviceCommandResult(true, "Reddedildi."));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullEventStore : ITurnstileEventStore
    {
        public Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent, bool compensateConsumption,
            CancellationToken cancellationToken) => Task.FromResult(new TurnstileEventWriteResult(false));
    }

    private sealed class FakeDecisions : IAccessDecisionRepository
    {
        public List<(AccessCheckRequest Request, AccessDecision Decision)> Denied { get; } = [];

        public Task<AccessSnapshot> GetSnapshotAsync(string cardNumber, Guid deviceId, Guid mealTypeId, DateOnly calendarDate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryConsumeAndLogAsync(Guid entitlementId, AccessCheckRequest request, AccessDecision decision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task LogDeniedAsync(AccessCheckRequest request, AccessDecision decision, CancellationToken cancellationToken)
        {
            Denied.Add((request, decision));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPublisher : IRealtimeEventPublisher
    {
        public List<AccessDecisionCommittedEvent> Committed { get; } = [];

        public ValueTask PublishAsync(AccessDecisionCommittedEvent realtimeEvent, CancellationToken cancellationToken = default)
        {
            Committed.Add(realtimeEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(TurnstileResultEvent realtimeEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask PublishAsync(DeviceStatusChangedEvent realtimeEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask PublishAsync(NotificationEvent realtimeEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
