using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.BulkOperations;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "YAKMA" IADE ETMEZ; "IPTAL" EDER.
///
/// <para>
/// Toplu Islem sihirbazi kullaniciya iki ayri secenek sunar ve etiketleri farki acikca
/// soyler: "Hakları iptal et" (para iade edilir) ile "Hakları yak (iade yok)".
/// </para>
/// <para>
/// Iade eklendiginde kosul yalnizca "aktarim degilse" idi ve IKISINI de kapsiyordu:
/// kullanici acikca "iade yok" secse bile para geri veriliyordu. Okul cezai bir kesinti
/// uygulamak isterken kararinin TERSI oluyordu.
/// </para>
/// </summary>
public sealed class ForfeitDoesNotRefundTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Day = new(2026, 10, 1);
    private Guid studentId, mealId;

    public ForfeitDoesNotRefundTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>"Hakları yak" secilirse para kasada KALIR.</summary>
    [Fact]
    public async Task ForfeitKeepsTheCash()
    {
        await SeedAsync();

        await ApplyAsync("Forfeit");

        Assert.Equal(180m, await CashAsync());
        Assert.True(await db.MealEntitlements.AnyAsync(x => x.Status == "Forfeited"), "Hak yakılmadı.");
    }

    /// <summary>"Hakları iptal et" secilirse para IADE EDILIR.</summary>
    [Fact]
    public async Task CancelRefundsTheCash()
    {
        await SeedAsync();

        await ApplyAsync("Delete");

        // Toplu islem YALNIZCA secilen gunu kapsar (3 gunluk hakkin 1 gunu):
        // 60 TL iade edilir, 120 TL kalir. Onemli olan IADE EDILMIS OLMASIDIR.
        Assert.Equal(120m, await CashAsync());
        Assert.True(await db.MealEntitlements.AnyAsync(x => x.Status == "Cancelled"), "Hak iptal edilmedi.");
    }

    /// <summary>
    /// Yakma sonrasi GERI ALMA da parayi bozmaz: iade yapilmadigi icin geri alinacak
    /// void tahsilat yoktur ve tutar oldugu gibi kalir.
    /// </summary>
    [Fact]
    public async Task UndoingAForfeitLeavesTheCashUntouched()
    {
        await SeedAsync();
        var operationId = await ApplyAsync("Forfeit");

        await BulkService().UndoAsync(operationId, Guid.NewGuid());

        Assert.Equal(180m, await CashAsync());
        Assert.True(await db.MealEntitlements.AllAsync(x => x.Status == "Active"), "Haklar geri gelmedi.");
    }

    // --- yardimcilar ---

    private async Task SeedAsync()
    {
        var meal = new MealType { Name = "Öğle" };
        var schoolClass = new SchoolClass { Name = "5A", SearchName = "5a" };
        var student = new Student
        {
            StudentNo = "9800", FirstName = "Yakma", LastName = "Test",
            SearchName = "yakma test", ClassId = schoolClass.Id, IsActive = true
        };
        db.AddRange(meal, schoolClass, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = 6000 });
        await db.SaveChangesAsync();
        studentId = student.Id; mealId = meal.Id;

        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealId,
            Day, Day, ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: 3);
        var service = new MealEntitlementService(new EfMealEntitlementRepository(db),
            new BusinessDayService(new NoClosures(), new WeekendPolicy()), Billing());
        var preview = await service.PreviewAsync(grant);
        await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
        Assert.Equal(180m, await CashAsync());
    }

    /// <summary>Verilen davranisla toplu islemi uygular ve islem kimligini doner.</summary>
    private async Task<Guid> ApplyAsync(string behaviour)
    {
        var request = new BulkCalendarOperationRequest(Guid.NewGuid().ToString("N"),
            new BulkOperationScope("Manual", StudentIds: [studentId]), Day, Day,
            [], mealId, "CancelEntitlements", behaviour, null, "test");
        var service = BulkService();
        var preview = await service.PreviewAsync(request);
        var result = await service.ApplyAsync(new(request, preview.PreviewToken), Guid.NewGuid());
        return result.OperationId;
    }
    private async Task<decimal> CashAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync()).Sum(x => x.Amount);

    private AuditService Audit() => new(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());
    private EfEntitlementBillingService Billing() => new(db, TimeProvider.System, Audit());
    private EfBulkOperationRepository Repository() => new(db, Audit(), TimeProvider.System, Billing());

    private BulkOperationService BulkService() => new(Repository(),
        new BusinessDayService(new NoClosures(), new WeekendPolicy()),
        new BulkPreviewTokenProtector(), TimeProvider.System);

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
