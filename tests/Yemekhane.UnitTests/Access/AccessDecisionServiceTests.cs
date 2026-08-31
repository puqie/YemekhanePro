using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Realtime;

namespace Yemekhane.UnitTests.Access;

public sealed class AccessDecisionServiceTests
{
    [Fact]
    public async Task FirstScanAllowsAndSecondScanWithStaleCacheDenies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "6811", FirstName = "Ayşe", LastName = "Yılmaz" };
        var card = new StudentCard { StudentId = student.Id, CardNumber = "8222704", ValidFrom = DateTimeOffset.UtcNow };
        var meal = new MealType { Name = "Öğle" }; var device = new Device { Name = "SF300-1", DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Connected" };
        var timestamp = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));
        var right = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = new(2026, 9, 14), Quantity = 1, Status = "Active" };
        context.AddRange(student, card, meal, device, right); await context.SaveChangesAsync();
        var publisher = new RecordingRealtimeEventPublisher();
        var service = new AccessDecisionService(new EfAccessDecisionRepository(context), new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), publisher);

        var first = await service.CheckAccessAsync(new AccessCheckRequest(card.CardNumber, device.Id, meal.Id, timestamp));
        var second = await service.CheckAccessAsync(new AccessCheckRequest(card.CardNumber, device.Id, meal.Id, timestamp.AddMilliseconds(1)));

        Assert.Equal("ALLOW", first.Decision);
        Assert.Equal("DENY", second.Decision);
        Assert.Equal("Bu öğün daha önce kullanılmış", second.Reason);
        Assert.Equal(2, await context.AccessLogs.CountAsync());
        Assert.Single(await context.MealUsages.ToListAsync());
        Assert.Collection(publisher.AccessDecisions,
            realtimeEvent =>
            {
                Assert.Equal(first.OperationId, realtimeEvent.OperationId);
                Assert.Equal("ALLOW", realtimeEvent.Decision);
                Assert.Equal(device.Id, realtimeEvent.DeviceId);
                Assert.Equal(timestamp, realtimeEvent.OccurredAt);
            },
            realtimeEvent =>
            {
                Assert.Equal(second.OperationId, realtimeEvent.OperationId);
                Assert.Equal("DENY", realtimeEvent.Decision);
            });
    }

    [Fact]
    public async Task FailedPersistenceDoesNotPublishAccessDecision()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var service = new AccessDecisionService(new FailingRepository(),
            new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAccessAsync(
            new AccessCheckRequest("missing", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)));

        Assert.Empty(publisher.AccessDecisions);
    }


    [Fact]
    public async Task InactiveCardIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync(card => card.IsActive = false);

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Kart pasif");
    }

    [Fact]
    public async Task InactiveStudentIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync(student: student => student.IsActive = false);

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Öğrenci pasif");
    }

    [Fact]
    public async Task InactiveDeviceIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync(device: device => device.IsActive = false);

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Cihaz pasif");
    }

    [Fact]
    public async Task ClosedCalendarDayIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync(closedDates: [new DateOnly(2026, 9, 14)]);

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Bugün tatil");
    }

    [Fact]
    public async Task StudentOnLeaveIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync(onLeave: true);

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Öğrenci bugün izinli");
    }

    [Fact]
    public async Task CancelledEntitlementIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync(entitlement: right => right.Status = "Cancelled");

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Bugün yemek hakkı bulunmuyor");
    }

    [Fact]
    public async Task UnknownCardIsDeniedAndEntitlementIsNotConsumed()
    {
        await using var scenario = await AccessScenario.CreateAsync();

        var decision = await scenario.CheckAsync(cardNumber: "tanimsiz-kart");

        await scenario.AssertDeniedAsync(decision, "Kart tanımsız");
    }

    /// <summary>
    /// Depo katmanındaki atomik "compare-and-swap" korumasını doğrudan doğrular.
    /// Servis katmanındaki ConsumedQuantity kontrolü hızlı yoldur; asıl yarış koruması buradadır,
    /// bu yüzden test depoyu paralel iki çağrıyla zorlar.
    /// </summary>
    [Fact]
    public async Task ConcurrentConsumeAllowsExactlyOneMealForSingleQuantityEntitlement()
    {
        await using var connection = new SqliteConnection($"Data Source=file:cas-race-{Guid.NewGuid():N}?mode=memory&cache=shared");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var setup = new YemekhaneDbContext(options);
        await setup.Database.MigrateAsync();
        var student = new Student { StudentNo = "6812", FirstName = "Deniz", LastName = "Kaya", IsActive = true };
        var meal = new MealType { Name = "Öğle" };
        var device = new Device { Name = "SF300-2", DeviceType = "SF300", ConnectionType = "Ethernet",
            Direction = "Entry", ConnectionStatus = "Connected", IsActive = true };
        var right = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
            EntitlementDate = new DateOnly(2026, 9, 14), Quantity = 1, Status = "Active" };
        setup.AddRange(student, meal, device, right);
        await setup.SaveChangesAsync();

        var timestamp = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));
        async Task<bool> ConsumeAsync(int offset)
        {
            await using var context = new YemekhaneDbContext(options);
            var repository = new EfAccessDecisionRepository(context);
            var request = new AccessCheckRequest("8222704", device.Id, meal.Id, timestamp.AddMilliseconds(offset));
            var decision = new AccessDecision("ALLOW", "Geçiş onaylandı", student.Id, "Deniz Kaya",
                device.Id, meal.Id, request.Timestamp, Guid.NewGuid());
            return await repository.TryConsumeAndLogAsync(right.Id, request, decision, default);
        }

        var results = await Task.WhenAll(ConsumeAsync(0), ConsumeAsync(1));

        Assert.Single(results, outcome => outcome);
        await using var verification = new YemekhaneDbContext(options);
        var stored = await verification.MealEntitlements.AsNoTracking().SingleAsync(x => x.Id == right.Id);
        Assert.Equal(1, stored.ConsumedQuantity);
        Assert.Single(await verification.MealUsages.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Her red dalını tek tek doğrulamak için ortak senaryo. Varsayılan hâli ALLOW üretir;
    /// testler yalnızca kendi ilgilendiği alanı bozar, böylece red gerekçesi tek değişkene bağlanır.
    /// </summary>
    private sealed class AccessScenario : IAsyncDisposable
    {
        private static readonly DateOnly Day = new(2026, 9, 14);
        private static readonly DateTimeOffset Timestamp = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

        private readonly SqliteConnection connection;

        private AccessScenario(SqliteConnection connection, YemekhaneDbContext context,
            AccessDecisionService service, StudentCard card, Device device, MealType meal, MealEntitlement entitlement)
        {
            this.connection = connection;
            Context = context; Service = service; Card = card; Device = device; Meal = meal; Entitlement = entitlement;
        }

        public YemekhaneDbContext Context { get; }
        public AccessDecisionService Service { get; }
        public StudentCard Card { get; }
        public Device Device { get; }
        public MealType Meal { get; }
        public MealEntitlement Entitlement { get; }

        public static async Task<AccessScenario> CreateAsync(
            Action<StudentCard>? card = null,
            Action<Student>? student = null,
            Action<Device>? device = null,
            Action<MealEntitlement>? entitlement = null,
            IReadOnlyCollection<DateOnly>? closedDates = null,
            bool onLeave = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = CreateContext(connection);
            await context.Database.MigrateAsync();

            var studentValue = new Student { StudentNo = "6811", FirstName = "Ayşe", LastName = "Yılmaz", IsActive = true };
            student?.Invoke(studentValue);
            var cardValue = new StudentCard { StudentId = studentValue.Id, CardNumber = "8222704", ValidFrom = DateTimeOffset.UtcNow, IsActive = true };
            card?.Invoke(cardValue);
            var deviceValue = new Device { Name = "SF300-1", DeviceType = "SF300", ConnectionType = "Ethernet",
                Direction = "Entry", ConnectionStatus = "Connected", IsActive = true };
            device?.Invoke(deviceValue);
            var mealValue = new MealType { Name = "Öğle" };
            var entitlementValue = new MealEntitlement { StudentId = studentValue.Id, MealTypeId = mealValue.Id,
                EntitlementDate = Day, Quantity = 1, Status = "Active" };
            entitlement?.Invoke(entitlementValue);

            context.AddRange(studentValue, cardValue, mealValue, deviceValue, entitlementValue);
            if (onLeave)
                context.Add(new StudentLeave { StudentId = studentValue.Id, StartsOn = Day, EndsOn = Day,
                    LeaveType = "Sağlık", EntitlementBehavior = "Keep" });
            await context.SaveChangesAsync();

            var service = new AccessDecisionService(new EfAccessDecisionRepository(context),
                new BusinessDayService(new ClosedCalendar(closedDates ?? []), new WeekendPolicy()),
                new RecordingRealtimeEventPublisher());
            return new AccessScenario(connection, context, service, cardValue, deviceValue, mealValue, entitlementValue);
        }

        public Task<AccessDecision> CheckAsync(string? cardNumber = null) =>
            Service.CheckAccessAsync(new AccessCheckRequest(cardNumber ?? Card.CardNumber, Device.Id, Meal.Id, Timestamp));

        /// <summary>Red kararını, gerekçesini ve hakkın tüketilmediğini birlikte doğrular.</summary>
        public async Task AssertDeniedAsync(AccessDecision decision, string expectedReason)
        {
            Assert.Equal("DENY", decision.Decision);
            Assert.Equal(expectedReason, decision.Reason);
            Assert.Empty(await Context.MealUsages.ToListAsync());
            var stored = await Context.MealEntitlements.AsNoTracking().SingleAsync(x => x.Id == Entitlement.Id);
            Assert.Equal(0, stored.ConsumedQuantity);
            var log = Assert.Single(await Context.AccessLogs.AsNoTracking().ToListAsync());
            Assert.Equal("DENY", log.Decision);
            Assert.Equal(expectedReason, log.Reason);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ClosedCalendar(IReadOnlyCollection<DateOnly> closed) : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(closed.Contains(calendarDate));
    }

    private sealed class OpenCalendar : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) => Task.FromResult(false);
    }
    private sealed class FailingRepository : IAccessDecisionRepository
    {
        public Task<AccessSnapshot> GetSnapshotAsync(string cardNumber, Guid deviceId, Guid mealTypeId,
            DateOnly calendarDate, CancellationToken cancellationToken) => Task.FromResult(
                new AccessSnapshot(false, false, null, null, null, false, false, null, 0, 0, null, false));

        public Task<bool> TryConsumeAndLogAsync(Guid entitlementId, AccessCheckRequest request,
            AccessDecision decision, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task LogDeniedAsync(AccessCheckRequest request, AccessDecision decision,
            CancellationToken cancellationToken) => throw new InvalidOperationException("database failed");
    }
    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
