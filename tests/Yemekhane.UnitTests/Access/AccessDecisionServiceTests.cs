using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Calendar;
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

    /// <summary>
    /// Kartin ustunde "0008247129" yazar, okul boyle girer; SC403 ise 8247129 bildirir.
    /// Iki bicim ayni kart sayilmali, yoksa elle girilen kart turnikede "Kart tanimsiz" olur.
    /// </summary>
    [Fact]
    public async Task CardStoredWithLeadingZerosMatchesTheDeviceReading()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "111", FirstName = "Test", LastName = "Test" };
        var card = new StudentCard { StudentId = student.Id, CardNumber = "0008247129", ValidFrom = DateTimeOffset.UtcNow };
        var meal = new MealType { Name = "Öğle" }; var device = new Device { Name = "Yemekhane Turnikesi", DeviceType = "SC403", ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Connected" };
        var timestamp = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));
        var right = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = new(2026, 9, 14), Quantity = 1, Status = "Active" };
        context.AddRange(student, card, meal, device, right); await context.SaveChangesAsync();
        var service = new AccessDecisionService(new EfAccessDecisionRepository(context), new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), new RecordingRealtimeEventPublisher());

        var decision = await service.CheckAccessAsync(new AccessCheckRequest("8247129", device.Id, meal.Id, timestamp, ReaderSource: "SC403"));

        Assert.Equal("ALLOW", decision.Decision);
        Assert.Equal(student.Id, decision.StudentId);
        var unknown = await service.CheckAccessAsync(new AccessCheckRequest("999", device.Id, meal.Id, timestamp));
        Assert.Equal("Kart tanımsız", unknown.Reason);
    }

    /// <summary>
    /// Grup tatili (gezi) yalnizca UYELERINI kapatir; ayni siniftaki uye olmayan ogrenci gecer.
    /// Uyenin hakki devredilmisse (tatil aktarimi Status=Transferred birakir) bugune aktif
    /// hakki yoktur ve ret sebebi "Bugün tatil"dir -- kapsam yalnizca sinifa bakilarak
    /// degerlendirilse bu sebep cikmaz, "Öğün ücreti tanımlı değil" cikardi. Bilerek
    /// verilmis AKTIF hak ise grup tatilinde de gecerlidir: uye ama hakli ogrenci gecer.
    /// </summary>
    [Fact]
    public async Task GroupScopedHolidayClosesTheDayForItsMembersOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();

        var schoolClass = new SchoolClass { Name = "5A" };
        var traveller = new Student { StudentNo = "6811", FirstName = "Gezici", LastName = "Ogrenci", ClassId = schoolClass.Id };
        var stayer = new Student { StudentNo = "6812", FirstName = "Kalan", LastName = "Ogrenci", ClassId = schoolClass.Id };
        var keeper = new Student { StudentNo = "6813", FirstName = "Hakli", LastName = "Uye", ClassId = schoolClass.Id };
        var travellerCard = new StudentCard { StudentId = traveller.Id, CardNumber = "8222704", ValidFrom = DateTimeOffset.UtcNow };
        var stayerCard = new StudentCard { StudentId = stayer.Id, CardNumber = "8222705", ValidFrom = DateTimeOffset.UtcNow };
        var keeperCard = new StudentCard { StudentId = keeper.Id, CardNumber = "8222706", ValidFrom = DateTimeOffset.UtcNow };
        var group = new StudentGroup { Name = "Gezi Grubu", GroupType = "Manual" };
        var meal = new MealType { Name = "Ogle" };
        var device = new Device { Name = "SF300-1", DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Connected" };
        var date = new DateOnly(2026, 9, 3);
        var timestamp = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(3));
        context.AddRange(schoolClass, traveller, stayer, keeper, travellerCard, stayerCard, keeperCard, group, meal, device);
        context.AddRange(
            new StudentGroupMember { GroupId = group.Id, StudentId = traveller.Id },
            new StudentGroupMember { GroupId = group.Id, StudentId = keeper.Id });
        context.AddRange(
            // Gezicinin hakki tatil aktarimiyla devredilmis: bugune AKTIF hakki yok.
            new MealEntitlement { StudentId = traveller.Id, MealTypeId = meal.Id, EntitlementDate = date, Quantity = 1, Status = "Transferred" },
            new MealEntitlement { StudentId = stayer.Id, MealTypeId = meal.Id, EntitlementDate = date, Quantity = 1, Status = "Active" },
            new MealEntitlement { StudentId = keeper.Id, MealTypeId = meal.Id, EntitlementDate = date, Quantity = 1, Status = "Active" });
        await context.SaveChangesAsync();

        await new HolidayService(new EfHolidayRepository(context)).CreateAsync(
            new(date, "5A gezi", "School", null, "Delete", [new("Group", group.Id)]));

        var service = new AccessDecisionService(new EfAccessDecisionRepository(context),
            new BusinessDayService(new EfHolidayRepository(context), new WeekendPolicy()),
            new RecordingRealtimeEventPublisher());

        var travellerDecision = await service.CheckAccessAsync(
            new AccessCheckRequest(travellerCard.CardNumber, device.Id, meal.Id, timestamp));
        var stayerDecision = await service.CheckAccessAsync(
            new AccessCheckRequest(stayerCard.CardNumber, device.Id, meal.Id, timestamp));
        var keeperDecision = await service.CheckAccessAsync(
            new AccessCheckRequest(keeperCard.CardNumber, device.Id, meal.Id, timestamp));

        Assert.Equal("DENY", travellerDecision.Decision);
        Assert.Equal("Bugün tatil", travellerDecision.Reason);
        Assert.Equal("ALLOW", stayerDecision.Decision);
        Assert.Equal("ALLOW", keeperDecision.Decision);
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

    /// <summary>
    /// Takvimde KAPALI gune bilerek verilmis AKTIF hak gecerlidir. Saha: "tatil olmasina
    /// ragmen gun ekledim, o gun hakkim var!" -- operator Cumartesi'ye hak yukledi, turnike
    /// "Bugün tatil" diye reddetti. Karar once takvime bakiyordu; artik bugune aktif hak
    /// varsa takvim/hafta sonu kontrolu atlanir, cunku o gunu acan operatorun kendisidir.
    /// </summary>
    [Fact]
    public async Task AnActiveRightOnAClosedDayIsHonoured()
    {
        await using var scenario = await AccessScenario.CreateAsync(closedDates: [new DateOnly(2026, 9, 14)]);

        var decision = await scenario.CheckAsync();

        Assert.Equal("ALLOW", decision.Decision);
        Assert.Single(await scenario.Context.MealUsages.ToListAsync());
    }

    /// <summary>Kapali gunde AKTIF hak yoksa ret sebebi hala "Bugün tatil"dir; hak da bakiye de dusulmez.</summary>
    [Fact]
    public async Task AClosedDayWithoutAnActiveRightIsStillDeniedAsHoliday()
    {
        await using var scenario = await AccessScenario.CreateAsync(closedDates: [new DateOnly(2026, 9, 14)],
            entitlement: right => right.Status = "Transferred");

        var decision = await scenario.CheckAsync();

        await scenario.AssertDeniedAsync(decision, "Bugün tatil");
    }

    /// <summary>
    /// Hafta sonu hakki yalnizca "Cumartesi/Pazar dahil" isaretlenince verilir; verildiyse
    /// hafta sonu kurali (WeekendPolicy) onu engellememeli. 12 Eylul 2026 Cumartesidir.
    /// </summary>
    [Fact]
    public async Task AWeekendRightGrantedOnPurposeIsHonoured()
    {
        await using var scenario = await AccessScenario.CreateAsync(day: new DateOnly(2026, 9, 12));

        var decision = await scenario.CheckAsync();

        Assert.Equal("ALLOW", decision.Decision);
        Assert.Equal("Geçiş onaylandı", decision.Reason);
    }

    /// <summary>Hafta sonu hak YOKSA "Bugün tatil": bakiye yolu hafta sonu acilmaz.</summary>
    [Fact]
    public async Task AWeekendWithoutARightIsDeniedAsHoliday()
    {
        await using var scenario = await AccessScenario.CreateAsync(day: new DateOnly(2026, 9, 12),
            entitlement: right => right.Status = "Cancelled");

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

        private readonly SqliteConnection connection;

        private AccessScenario(SqliteConnection connection, YemekhaneDbContext context,
            AccessDecisionService service, StudentCard card, Device device, MealType meal, MealEntitlement entitlement,
            DateTimeOffset timestamp)
        {
            this.connection = connection;
            Context = context; Service = service; Card = card; Device = device; Meal = meal; Entitlement = entitlement;
            Timestamp = timestamp;
        }

        public YemekhaneDbContext Context { get; }
        public AccessDecisionService Service { get; }
        public StudentCard Card { get; }
        public Device Device { get; }
        public MealType Meal { get; }
        public MealEntitlement Entitlement { get; }
        /// <summary>Senaryo gununun ogle saati (Istanbul); hafta sonu senaryolari icin gun secilebilir.</summary>
        public DateTimeOffset Timestamp { get; }

        public static async Task<AccessScenario> CreateAsync(
            Action<StudentCard>? card = null,
            Action<Student>? student = null,
            Action<Device>? device = null,
            Action<MealEntitlement>? entitlement = null,
            IReadOnlyCollection<DateOnly>? closedDates = null,
            bool onLeave = false,
            DateOnly? day = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = CreateContext(connection);
            await context.Database.MigrateAsync();
            var dayValue = day ?? Day;
            var timestamp = new DateTimeOffset(dayValue.Year, dayValue.Month, dayValue.Day, 12, 0, 0, TimeSpan.FromHours(3));

            var studentValue = new Student { StudentNo = "6811", FirstName = "Ayşe", LastName = "Yılmaz", IsActive = true };
            student?.Invoke(studentValue);
            var cardValue = new StudentCard { StudentId = studentValue.Id, CardNumber = "8222704", ValidFrom = DateTimeOffset.UtcNow, IsActive = true };
            card?.Invoke(cardValue);
            var deviceValue = new Device { Name = "SF300-1", DeviceType = "SF300", ConnectionType = "Ethernet",
                Direction = "Entry", ConnectionStatus = "Connected", IsActive = true };
            device?.Invoke(deviceValue);
            var mealValue = new MealType { Name = "Öğle" };
            var entitlementValue = new MealEntitlement { StudentId = studentValue.Id, MealTypeId = mealValue.Id,
                EntitlementDate = dayValue, Quantity = 1, Status = "Active" };
            entitlement?.Invoke(entitlementValue);

            context.AddRange(studentValue, cardValue, mealValue, deviceValue, entitlementValue);
            // Ogun ucreti ACIKCA 0 ₺ tanimlanir: bu senaryolarin konusu hakedis/kart/cihaz,
            // ucret tanimsizligi degil. Satir hic olmasaydi ret sebebi "Öğün ücreti tanımlı
            // değil" olur ve asil olculen dal golgelenirdi.
            context.Add(new MealTypePrice { MealTypeId = mealValue.Id, PriceCents = 0 });
            if (onLeave)
                context.Add(new StudentLeave { StudentId = studentValue.Id, StartsOn = dayValue, EndsOn = dayValue,
                    LeaveType = "Sağlık", EntitlementBehavior = "Keep" });
            await context.SaveChangesAsync();

            var service = new AccessDecisionService(new EfAccessDecisionRepository(context),
                new BusinessDayService(new ClosedCalendar(closedDates ?? []), new WeekendPolicy()),
                new RecordingRealtimeEventPublisher());
            return new AccessScenario(connection, context, service, cardValue, deviceValue, mealValue, entitlementValue, timestamp);
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
