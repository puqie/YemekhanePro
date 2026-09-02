using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Realtime;

namespace Yemekhane.UnitTests.Balances;

/// <summary>
/// Gecis kararinda bakiye yolu: hakedis yokken ogun ucreti bakiyeden duser (BalanceUsed);
/// yetmezse InsufficientBalance; ucretsiz ogunde bakiye kurali devreye girmez; ayni OperationId
/// ikinci kez dusmez; suresi dolmus yukleme harcanamaz; hakedis varsa bakiye hic dokunulmaz.
/// </summary>
public sealed class BalanceAccessDecisionTests
{
    private static readonly DateOnly Day = new(2026, 9, 14);
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task HakedisYokBakiyeYeterliyseIzinVerilirVeUcretDuser()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000);

        var decision = await scenario.CheckAsync();

        Assert.Equal("ALLOW", decision.Decision);
        Assert.Equal(BalanceAccessReasons.BalanceUsed, decision.Reason);
        Assert.Equal(scenario.StudentId, decision.StudentId);
        var log = Assert.Single(await scenario.Context.AccessLogs.AsNoTracking().ToListAsync());
        Assert.Equal("ALLOW", log.Decision);
        var deduction = Assert.Single(await scenario.Context.StudentBalanceEntries.AsNoTracking()
            .Where(x => x.Kind == StudentBalanceEntryKinds.Deduction).ToListAsync());
        Assert.Equal(-7_500, deduction.AmountCents);
        Assert.Equal(StudentBalanceReferenceTypes.AccessLog, deduction.ReferenceType);
        Assert.Equal(log.Id, deduction.ReferenceId);
        Assert.Contains("Öğle", deduction.Note);
        Assert.Empty(await scenario.Context.MealUsages.ToListAsync());
        Assert.Equal(42_500, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
        var realtime = Assert.Single(scenario.Publisher.AccessDecisions);
        Assert.Equal("ALLOW", realtime.Decision);
    }

    [Fact]
    public async Task BakiyeYetersizseReddedilirVeDusumOlmaz()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 5_000);

        var decision = await scenario.CheckAsync();

        Assert.Equal("DENY", decision.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, decision.Reason);
        Assert.Single(await scenario.Context.StudentBalanceEntries.ToListAsync());
        var log = Assert.Single(await scenario.Context.AccessLogs.AsNoTracking().ToListAsync());
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, log.Reason);
    }

    [Fact]
    public async Task HicYuklemeYokVeUcretliOgundeBakiyeYetersizDenir()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: null);

        var decision = await scenario.CheckAsync();

        Assert.Equal("DENY", decision.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, decision.Reason);
    }

    [Fact]
    public async Task UcretSifirOgundeBakiyeKuraliDevreyeGirmez()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 0, topUpCents: 50_000);

        var decision = await scenario.CheckAsync();

        Assert.Equal("DENY", decision.Decision);
        Assert.Equal("Bugün yemek hakkı bulunmuyor", decision.Reason);
        Assert.Equal(50_000, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
    }

    [Fact]
    public async Task HakedisVarsaBakiyeyeDokunulmaz()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000, entitlement: true);

        var decision = await scenario.CheckAsync();

        Assert.Equal("ALLOW", decision.Decision);
        Assert.Equal("Geçiş onaylandı", decision.Reason);
        Assert.Single(await scenario.Context.MealUsages.ToListAsync());
        Assert.Equal(50_000, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
    }

    [Fact]
    public async Task AyniOperationIdIkinciKezDusmezAyniYanitDoner()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000);
        var operationId = Guid.NewGuid();

        var first = await scenario.CheckAsync(operationId);
        var replay = await scenario.CheckAsync(operationId);
        // Depo katmani da tek basina idempotent: kaybeden dal ayni cagriyi tekrarlasa bile ikinci dusum yok.
        var repositoryReplay = await scenario.Repository.TryDeductBalanceAndLogAsync(7_500,
            scenario.Request(operationId), first, default);

        Assert.Equal("ALLOW", first.Decision);
        Assert.Equal("ALLOW", replay.Decision);
        Assert.Equal(BalanceAccessReasons.BalanceUsed, replay.Reason);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.True(repositoryReplay);
        Assert.Single(await scenario.Context.AccessLogs.ToListAsync());
        Assert.Single(await scenario.Context.StudentBalanceEntries.Where(x => x.Kind == StudentBalanceEntryKinds.Deduction).ToListAsync());
        Assert.Equal(42_500, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
    }

    [Fact]
    public async Task FarkliOlaylarBakiyeBitenceReddedilir()
    {
        // 150 ₺ bakiye, 75 ₺ ogun: iki gecis izinli, ucuncusu bakiye yetersiz.
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 15_000);

        var first = await scenario.CheckAsync(Guid.NewGuid());
        var second = await scenario.CheckAsync(Guid.NewGuid(), Timestamp.AddMinutes(1));
        var third = await scenario.CheckAsync(Guid.NewGuid(), Timestamp.AddMinutes(2));

        Assert.Equal("ALLOW", first.Decision);
        Assert.Equal("ALLOW", second.Decision);
        Assert.Equal("DENY", third.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, third.Reason);
        Assert.Equal(0, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
    }

    [Fact]
    public async Task SuresiDolmusYuklemeHarcanamaz()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000, expiresOn: Day.AddDays(-1));

        var decision = await scenario.CheckAsync();

        Assert.Equal("DENY", decision.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, decision.Reason);
        Assert.Equal(50_000, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
    }

    [Fact]
    public async Task BitisGunuOlanYuklemeOGunHalaGecerlidir()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000, expiresOn: Day);

        var decision = await scenario.CheckAsync();

        Assert.Equal("ALLOW", decision.Decision);
        Assert.Equal(BalanceAccessReasons.BalanceUsed, decision.Reason);
    }

    [Fact]
    public async Task OnbellekEskiyseDepoBakiyeyiKilitAltindaYenidenSayar()
    {
        // Anlik goruntu onbellege alinmisken bakiye baska bir yoldan sifirlanirsa (ornek: iptal),
        // servis ALLOW'a niyetlense de depo yeniden sayar ve reddeder.
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000);
        await scenario.WarmSnapshotAsync();
        scenario.Context.Add(new StudentBalanceEntry { StudentId = scenario.StudentId, AmountCents = -50_000,
            Kind = StudentBalanceEntryKinds.Refund, OccurredAt = Timestamp.AddMinutes(-1) });
        await scenario.SaveWithoutInvalidationAsync();

        var decision = await scenario.CheckAsync();

        Assert.Equal("DENY", decision.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, decision.Reason);
        Assert.Empty(await scenario.Context.StudentBalanceEntries.Where(x => x.Kind == StudentBalanceEntryKinds.Deduction).ToListAsync());
    }

    [Fact]
    public async Task TurnikeBasarisizOlursaBakiyeIadeEdilir()
    {
        await using var scenario = await Scenario.CreateAsync(priceCents: 7_500, topUpCents: 50_000);
        var decision = await scenario.CheckAsync();
        Assert.Equal("ALLOW", decision.Decision);

        var store = new EfTurnstileEventStore(scenario.Context);
        var result = await store.RecordAsync(new TurnstileEventData(scenario.DeviceId, decision.OperationId, Timestamp.AddSeconds(5), "OPEN", "TIMEOUT"), compensateConsumption: true, default);
        // Ikinci telafi iki kez iade etmez.
        var again = await store.RecordAsync(new TurnstileEventData(scenario.DeviceId, decision.OperationId, Timestamp.AddSeconds(6), "OPEN", "TIMEOUT"), compensateConsumption: true, default);

        Assert.True(result.ConsumptionCompensated);
        Assert.False(again.ConsumptionCompensated);
        Assert.Equal(50_000, await scenario.Context.StudentBalanceEntries.SumAsync(x => x.AmountCents));
        var log = Assert.Single(await scenario.Context.AccessLogs.AsNoTracking().ToListAsync());
        Assert.Equal("ERROR", log.Decision);
        Assert.Contains("bakiye iade", log.Reason);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly AccessSnapshotCache cache;
        private readonly StudentCard card;

        private Scenario(SqliteConnection connection, YemekhaneDbContext context, AccessSnapshotCache cache,
            EfAccessDecisionRepository repository, AccessDecisionService service, RecordingRealtimeEventPublisher publisher,
            StudentCard card, Guid studentId, Guid deviceId, Guid mealTypeId)
        {
            this.connection = connection; this.cache = cache; this.card = card;
            Context = context; Repository = repository; Service = service; Publisher = publisher;
            StudentId = studentId; DeviceId = deviceId; MealTypeId = mealTypeId;
        }

        public YemekhaneDbContext Context { get; }
        public EfAccessDecisionRepository Repository { get; }
        public AccessDecisionService Service { get; }
        public RecordingRealtimeEventPublisher Publisher { get; }
        public Guid StudentId { get; }
        public Guid DeviceId { get; }
        public Guid MealTypeId { get; }

        public static async Task<Scenario> CreateAsync(long priceCents, long? topUpCents, bool entitlement = false, DateOnly? expiresOn = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync();

            var student = new Student { StudentNo = "5001", FirstName = "Ada", LastName = "Akgün", IsActive = true };
            var cardValue = new StudentCard { StudentId = student.Id, CardNumber = "8350001", ValidFrom = DateTimeOffset.UtcNow, IsActive = true };
            var meal = new MealType { Name = "Öğle Yemeği" };
            var device = new Device { Name = "Turnike", DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Connected", IsActive = true };
            context.AddRange(student, cardValue, meal, device);
            if (priceCents > 0) context.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = priceCents });
            if (topUpCents is { } cents)
                context.Add(new StudentBalanceEntry { StudentId = student.Id, AmountCents = cents, Kind = StudentBalanceEntryKinds.TopUp,
                    ReferenceType = StudentBalanceReferenceTypes.IncomeTransaction, ReferenceId = Guid.NewGuid(),
                    OccurredAt = Timestamp.AddDays(-3), ExpiresOn = expiresOn });
            if (entitlement)
                context.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = Day, Quantity = 1, Status = "Active" });
            await context.SaveChangesAsync();

            var metrics = new AccessPerformanceMetrics();
            var cache = new AccessSnapshotCache(TimeProvider.System, metrics);
            var repository = new EfAccessDecisionRepository(context, cache, cache, metrics);
            var publisher = new RecordingRealtimeEventPublisher();
            var service = new AccessDecisionService(repository, new BusinessDayService(new OpenCalendar(), new WeekendPolicy()), publisher);
            return new Scenario(connection, context, cache, repository, service, publisher, cardValue, student.Id, device.Id, meal.Id);
        }

        public AccessCheckRequest Request(Guid? operationId = null, DateTimeOffset? at = null) =>
            new(card.CardNumber, DeviceId, MealTypeId, at ?? Timestamp, OperationId: operationId);

        public Task<AccessDecision> CheckAsync(Guid? operationId = null, DateTimeOffset? at = null) =>
            Service.CheckAccessAsync(Request(operationId, at));

        public Task WarmSnapshotAsync() => Repository.GetSnapshotAsync(card.CardNumber, DeviceId, MealTypeId, Day, default);

        /// <summary>Onbellegi bilerek eski birakir: SaveChanges'ten sonra kayit geri konur.</summary>
        public async Task SaveWithoutInvalidationAsync()
        {
            var stale = await Repository.GetSnapshotAsync(card.CardNumber, DeviceId, MealTypeId, Day, default);
            await Context.SaveChangesAsync();
            cache.Set(card.CardNumber, DeviceId, MealTypeId, Day, stale);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class OpenCalendar : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
