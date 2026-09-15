using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Cash;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// Hakediş ve ona bağlı kasa tahsilatı tek muhasebe işlemidir: ikisi birlikte
/// kesinleşmeli veya ikisi de geri alınmalıdır.
/// </summary>
public sealed class GrantChargeAtomicityTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.Zero));

    public GrantChargeAtomicityTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task TahsilatYazilamazsaHaklarDaKesinlesmez()
    {
        var (mealId, studentId) = await SeedAsync();
        var service = Service(new ThrowingBilling());
        var grant = Grant(mealId, studentId);
        var preview = await service.PreviewAsync(grant);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken), Guid.NewGuid()));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.MealEntitlements.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BasariliTahsilatAyniGunKasaOzetindeGorunur()
    {
        var (mealId, studentId) = await SeedAsync();
        var audit = Audit();
        var service = Service(new EfEntitlementBillingService(db, clock, audit));
        var grant = Grant(mealId, studentId);
        var preview = await service.PreviewAsync(grant);

        var result = await service.ApplyAsync(
            new ApplyEntitlementGrantRequest(grant, preview.PreviewToken), Guid.NewGuid());
        var summary = await new CashService(new EfCashRepository(db), clock)
            .GetDailyAsync(new DateOnly(2026, 9, 14));

        Assert.Equal(1, result.ChargedStudents);
        Assert.Equal(750m, result.ChargedTotal);
        Assert.Equal(750m, summary.TotalAmount);
        Assert.Contains(summary.ByIncomeType,
            row => row.IncomeTypeName == EntitlementIncomeType.Name && row.Amount == 750m);
    }

    private async Task<(Guid MealId, Guid StudentId)> SeedAsync()
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9700", FirstName = "Ece", LastName = "Su" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = 25_000 });
        await db.SaveChangesAsync();
        return (meal.Id, student.Id);
    }

    private MealEntitlementService Service(IEntitlementBillingService billing) => new(
        new EfMealEntitlementRepository(db, Audit()),
        new BusinessDayService(new NoClosures(), new WeekendPolicy()),
        billing);

    private AuditService Audit() => new(
        new EfAuditRepository(db, clock),
        new SystemAuditContext());

    private static EntitlementGrantRequest Grant(Guid mealId, Guid studentId) => new(
        new EntitlementTarget("Manual", [studentId]), mealId,
        new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 14),
        ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: 3);

    private sealed class ThrowingBilling : IEntitlementBillingService
    {
        public Task<EntitlementChargeResult> ChargeAsync(EntitlementChargeRequest request, Guid actorId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Tahsilat yazılamadı (benzetim).");

        public Task<int> RefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> UndoRefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
