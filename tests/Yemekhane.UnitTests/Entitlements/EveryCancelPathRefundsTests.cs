using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Leaves;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Leaves;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// HAKKI IPTAL EDEN HER YOL TAHSILATI DA GERI ALMALIDIR.
///
/// <para>
/// Hakki iptal eden dort ayri yol vardi ve YALNIZCA BIRI parayi iade ediyordu. Sonuc,
/// ayni isin HANGI EKRANDAN yapildigina gore degisiyordu:
/// </para>
/// <list type="bullet">
/// <item>Hakedisler ekrani, coklu secim -&gt; iade EDIYORDU (dogru olan)</item>
/// <item>Hakedisler ekrani, tek iptal -&gt; iade ETMIYORDU</item>
/// <item>Toplu Islem sihirbazi -&gt; iade ETMIYORDU</item>
/// <item>Izin kaydi ("Haklari iptal et") -&gt; iade ETMIYORDU</item>
/// </list>
/// <para>
/// En pahalisi toplu islemdi: 200 ogrencinin 5 gunluk tatilinde ogun basi 60 TL ile
/// 60.000 TL kasada kalirdi. Izin yolunda ise durum kaliciydi -- haklar "Cancelled"
/// oldugu icin iade sonradan da tetiklenemiyordu.
/// </para>
/// <para>
/// AKTARIM (Transferred) iade ETMEZ: hak baska gune tasinir, ogrenci onu kullanacaktir.
/// </para>
/// </summary>
public sealed class EveryCancelPathRefundsTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public EveryCancelPathRefundsTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>TEK hak iptali de parayi iade eder (once etmiyordu).</summary>
    [Fact]
    public async Task CancellingASingleEntitlementRefundsItsCharge()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);
        Assert.Equal(180m, await CashAsync());

        var id = (await FirstIdsAsync(student, 1)).Single();
        await service.CancelAsync(id);

        // Uc gunun biri iptal: 60 TL geri, 120 TL kalir.
        Assert.Equal(120m, await CashAsync());
    }

    /// <summary>IZIN kaydi haklari iptal ederse parayi da iade eder (once etmiyordu).</summary>
    [Fact]
    public async Task ALeaveThatCancelsEntitlementsAlsoRefundsTheCash()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);
        Assert.Equal(180m, await CashAsync());

        // Hafta sonu atlandigi icin 3 gunluk hak 1, 2 ve 5 Ekim'e duser (3-4 Ekim hafta sonu).
        await LeaveRepository().CreateAndApplyAsync(new CreateLeaveRequest(student,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), "Rapor", null, "Cancel", Guid.NewGuid()), default);

        Assert.Equal(0m, await CashAsync());
    }

    /// <summary>Izin haklari AKTARIYORSA para iade EDILMEZ: ogrenci hakkini kullanacaktir.</summary>
    [Fact]
    public async Task ALeaveThatTransfersEntitlementsKeepsTheCash()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);

        // Hafta sonu atlandigi icin 3 gunluk hak 1, 2 ve 5 Ekim'e duser (3-4 Ekim hafta sonu).
        await LeaveRepository().CreateAndApplyAsync(new CreateLeaveRequest(student,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), "Gezi", null, "NextBusinessDay", Guid.NewGuid()), default);

        Assert.Equal(180m, await CashAsync());
    }

    /// <summary>Iade, iptal ile AYNI transaction icinde olmali: yarim kalmis durum kalmamali.</summary>
    [Fact]
    public async Task TheRefundAndTheCancellationLandTogether()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);

        var ids = await FirstIdsAsync(student, 3);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));

        // Haklar iptal VE kasa bos: ikisi birlikte.
        Assert.Equal(0m, await CashAsync());
        Assert.Equal(3, await db.MealEntitlements.CountAsync(x => x.Status == "Cancelled"));
    }

    // --- yardimcilar ---

    private async Task<(Guid Meal, Guid Student)> SeedAsync(long priceCents)
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9600", FirstName = "İade", LastName = "Test" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = priceCents });
        await db.SaveChangesAsync();
        return (meal.Id, student.Id);
    }

    private static async Task GrantAsync(MealEntitlementService service, Guid mealTypeId, Guid studentId, int days)
    {
        var startsOn = new DateOnly(2026, 10, 1);
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            startsOn, startsOn, ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: days);
        var preview = await service.PreviewAsync(grant);
        await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
    }

    private async Task<IReadOnlyCollection<Guid>> FirstIdsAsync(Guid studentId, int count) =>
        await db.MealEntitlements.AsNoTracking().Where(x => x.StudentId == studentId)
            .OrderBy(x => x.EntitlementDate).Take(count).Select(x => x.Id).ToListAsync();

    private async Task<decimal> CashAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync()).Sum(x => x.Amount);

    private AuditService Audit() => new(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());

    private MealEntitlementService CreateService() =>
        new(new EfMealEntitlementRepository(db),
            new BusinessDayService(new NoClosures(), new WeekendPolicy()),
            new EfEntitlementBillingService(db, TimeProvider.System, Audit()));

    private EfLeaveRepository LeaveRepository() =>
        new(db, new BusinessDayService(new NoClosures(), new WeekendPolicy()), Audit(),
            new EfEntitlementBillingService(db, TimeProvider.System, Audit()));

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
