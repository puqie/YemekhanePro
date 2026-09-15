using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Cash;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

public sealed class LegacyEntitlementCashBackfillTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 9, 14, 8, 30, 0, TimeSpan.Zero));

    public LegacyEntitlementCashBackfillTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>()
            .UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task EksikEskiHizliHaklarBugununKasasinaYazilir()
    {
        var (student, meal) = await SeedBaseAsync(priceCents: 25_000);
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 10), quantity: 1);
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 11), quantity: 2);
        await db.SaveChangesAsync();

        var result = await Service().RunAsync();
        var summary = await new CashService(new EfCashRepository(db), clock)
            .GetDailyAsync(new DateOnly(2026, 9, 14));
        var rows = await db.Set<IncomeTransaction>().AsNoTracking().OrderBy(x => x.Amount).ToListAsync();

        Assert.Equal(2, result.ChargedEntitlements);
        Assert.Equal(750m, result.Total);
        Assert.Equal(750m, summary.TotalAmount);
        Assert.Contains(summary.ByIncomeType,
            x => x.IncomeTypeName == EntitlementIncomeType.Name && x.Amount == 750m);
        Assert.Equal([250m, 500m], rows.Select(x => x.Amount).ToArray());
        Assert.All(rows, x =>
        {
            Assert.Equal(clock.GetUtcNow(), x.TransactionAt);
            Assert.Equal(meal.Id, x.MealTypeId);
            Assert.Equal(1, x.EntitlementDayCount);
            Assert.Contains("geçmiş kayıt telafisi", x.Description);
        });
    }

    [Fact]
    public async Task TekrarCalismaAyniGeliriIkinciKezYazmaz()
    {
        var (student, meal) = await SeedBaseAsync(priceCents: 10_000);
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 8));
        await db.SaveChangesAsync();

        var first = await Service().RunAsync();
        db.ChangeTracker.Clear();
        var second = await Service().RunAsync();

        Assert.Equal(1, first.ChargedEntitlements);
        Assert.Equal(0, second.ChargedEntitlements);
        Assert.Single(await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync());
        Assert.Single(await db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == LegacyEntitlementCashBackfill.AuditAction).ToListAsync());
    }

    [Fact]
    public async Task IlkCalismadanSonraBilerekUcretsizVerilenYeniHakDahaSonraUcretlendirilmez()
    {
        var (student, meal) = await SeedBaseAsync(priceCents: 12_500);
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 8));
        await db.SaveChangesAsync();
        await Service().RunAsync();

        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 15),
            createdAt: clock.GetUtcNow().AddMinutes(1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rerun = await Service().RunAsync();

        Assert.Equal(0, rerun.ChargedEntitlements);
        Assert.Single(await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync());
        var cutoff = await db.Set<SystemSetting>().AsNoTracking()
            .SingleAsync(x => x.Key == LegacyEntitlementCashBackfill.CutoffSettingKey);
        Assert.Equal(clock.GetUtcNow().ToUniversalTime().ToString("O"), cutoff.Value);
    }

    [Fact]
    public async Task MevcutTahsilatinKapsadigiGunlerCiftYazilmazYalnizEksikGunTamamlanir()
    {
        var (student, meal) = await SeedBaseAsync(priceCents: 20_000);
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 10),
            createdAt: clock.GetUtcNow().AddDays(-4));
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 11),
            createdAt: clock.GetUtcNow().AddDays(-3));
        var type = new IncomeType { Name = EntitlementIncomeType.Name };
        db.Add(type);
        await db.SaveChangesAsync();
        db.Add(new IncomeTransaction
        {
            OperationId = Guid.NewGuid(), StudentId = student.Id, IncomeTypeId = type.Id,
            TransactionAt = clock.GetUtcNow().AddDays(-2), Amount = 200m,
            Description = "Yemek hakedişi: Öğle, 10.09.2026-11.09.2026 (1 gün)",
            CreatedBy = Guid.NewGuid(), MealTypeId = meal.Id,
            EntitlementStartsOn = new DateOnly(2026, 9, 10),
            EntitlementEndsOn = new DateOnly(2026, 9, 11), EntitlementDayCount = 1
        });
        await db.SaveChangesAsync();

        var result = await Service().RunAsync();
        var rows = await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync();

        Assert.Equal(1, result.ChargedEntitlements);
        Assert.Equal(200m, result.Total);
        Assert.Equal(2, rows.Count);
        Assert.Equal(400m, rows.Sum(x => x.Amount));
    }

    [Fact]
    public async Task YalnizAktifWpfHizliHaklariVePozitifFiyatiOlanlariIsler()
    {
        var (student, meal) = await SeedBaseAsync(priceCents: 15_000);
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 1), source: "Manual");
        AddEntitlement(student.Id, meal.Id, new DateOnly(2026, 9, 2), status: "Cancelled");
        await db.SaveChangesAsync();

        var result = await Service().RunAsync();

        Assert.Equal(0, result.ChargedEntitlements);
        Assert.Empty(await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync());
    }

    private LegacyEntitlementCashBackfill Service() => new(
        db, clock,
        new AuditService(new EfAuditRepository(db, clock), new SystemAuditContext()),
        NullLogger<LegacyEntitlementCashBackfill>.Instance);

    private async Task<(Student Student, MealType Meal)> SeedBaseAsync(long priceCents)
    {
        var student = new Student
        {
            StudentNo = "9800", FirstName = "Ece", LastName = "Su",
            CreatedAt = clock.GetUtcNow().AddDays(-30)
        };
        var meal = new MealType { Name = "Öğle", CreatedAt = clock.GetUtcNow().AddDays(-30) };
        db.AddRange(student, meal);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = priceCents });
        db.Add(new StudentCard
        {
            StudentId = student.Id, CardNumber = "CARD-9800", IsActive = true,
            ValidFrom = clock.GetUtcNow().AddDays(-30), CreatedAt = clock.GetUtcNow().AddDays(-30)
        });
        await db.SaveChangesAsync();
        return (student, meal);
    }

    private void AddEntitlement(Guid studentId, Guid mealTypeId, DateOnly date, int quantity = 1,
        string source = LegacyEntitlementCashBackfill.EligibleSource, string status = "Active",
        DateTimeOffset? createdAt = null) => db.Add(new MealEntitlement
    {
        StudentId = studentId,
        MealTypeId = mealTypeId,
        EntitlementDate = date,
        Quantity = quantity,
        Status = status,
        Source = source,
        CreatedAt = createdAt ?? clock.GetUtcNow().AddDays(-5)
    });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
