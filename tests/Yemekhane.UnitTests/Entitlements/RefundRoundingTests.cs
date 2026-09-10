using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// Kismi iadede kurus kaybolmamalidir: iade + kalan HER ZAMAN tam olarak tahsilati verir.
///
/// <para>
/// Bugun bu bir HATA DEGIL, korunan bir DEGISMEZDIR. Tahsilat her zaman
/// "birim fiyat x gun" oldugu icin bolme tam cikar. Testler bunu kilitler ki
/// fiyatlandirma degisip bolunemeyen bir tutar olustugunda (indirim, elle duzeltme,
/// farkli birim fiyat) kurus sessizce kaybolmasin.
/// </para>
/// <para>
/// Uygulama cikarma kullanir (iadeyi yuvarla, kalani cikar); carpip yuvarlama
/// bolunemeyen tutarlarda bir kurus kaydirirdi.
/// </para>
/// </summary>
public sealed class RefundRoundingTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public RefundRoundingTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// Iade + kalan tam olarak tahsilati vermeli; hicbir kurus kaybolmamali.
    /// </summary>
    [Theory]
    [InlineData(6, 1)]
    [InlineData(6, 5)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(7, 3)]
    public async Task RefundPlusKeptAlwaysEqualsTheOriginalCharge(int totalDays, int cancelDays)
    {
        // Kurus cinsinden tek sayili birim fiyat; tahsilat = 1667 x gun.
        var (meal, student) = await SeedAsync(1667);
        var service = CreateService();
        await GrantAsync(service, meal, student, totalDays);
        var original = await ActiveTotalAsync();

        var ids = await FirstEntitlementIdsAsync(student, cancelDays);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));

        var kept = await ActiveTotalAsync();
        var voided = await VoidedTotalAsync();
        // Void edilen tutar = iade + yeniden yazilan kalan. Kurus kayarsa
        // kept + refund toplami orijinalden SAPAR.
        var refund = voided - kept;
        Assert.Equal(original, kept + refund);
    }

    /// <summary>
    /// Kalan tutar orantidan en fazla bir kurus sapabilir.
    /// </summary>
    [Fact]
    public async Task TheKeptAmountNeverExceedsTheProportionalShare()
    {
        var (meal, student) = await SeedAsync(1667);
        var service = CreateService();
        await GrantAsync(service, meal, student, 6);
        var original = await ActiveTotalAsync();

        var ids = await FirstEntitlementIdsAsync(student, 1);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));

        var kept = await ActiveTotalAsync();
        var proportional = original * 5 / 6;
        Assert.True(Math.Abs(kept - proportional) <= 0.01m,
            $"Kalan tutar orantıdan {Math.Abs(kept - proportional):N4} TL saptı (kalan {kept}, orantı {proportional}).");
    }

    /// <summary>Tum gunler iptal edilirse kasada HIC para kalmaz.</summary>
    [Fact]
    public async Task CancellingEveryDayLeavesNothingInTheCash()
    {
        var (meal, student) = await SeedAsync(1667);
        var service = CreateService();
        await GrantAsync(service, meal, student, 6);

        var ids = await FirstEntitlementIdsAsync(student, 6);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));

        Assert.Equal(0m, await ActiveTotalAsync());
    }

    // --- yardimcilar ---

    private async Task<(Guid Meal, Guid Student)> SeedAsync(long priceCents)
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9500", FirstName = "Kuruş", LastName = "Test" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = priceCents });
        await db.SaveChangesAsync();
        return (meal.Id, student.Id);
    }

    private static async Task GrantAsync(MealEntitlementService service, Guid mealTypeId, Guid studentId, int dayCount)
    {
        var startsOn = new DateOnly(2026, 10, 1);
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            startsOn, startsOn, ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: dayCount);
        var preview = await service.PreviewAsync(grant);
        await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
    }

    private async Task<IReadOnlyCollection<Guid>> FirstEntitlementIdsAsync(Guid studentId, int count) =>
        await db.MealEntitlements.AsNoTracking().Where(x => x.StudentId == studentId)
            .OrderBy(x => x.EntitlementDate).Take(count).Select(x => x.Id).ToListAsync();

    private async Task<decimal> ActiveTotalAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync()).Sum(x => x.Amount);

    private async Task<decimal> VoidedTotalAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => x.IsVoided).ToListAsync()).Sum(x => x.Amount);

    private MealEntitlementService CreateService()
    {
        var audit = new AuditService(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());
        return new MealEntitlementService(new EfMealEntitlementRepository(db),
            new BusinessDayService(new NoClosures(), new WeekendPolicy()),
            new EfEntitlementBillingService(db, TimeProvider.System, audit));
    }

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
