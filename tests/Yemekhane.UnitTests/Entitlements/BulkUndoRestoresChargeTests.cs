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
/// IADE GERI ALINABILMELIDIR: haklar geri gelirse para da geri gelmeli.
///
/// <para>
/// Toplu iptal artik tahsilati iade ediyor. Ama "Geri Al" yalnizca HAKLARI geri
/// yukluyordu; tahsilat void KALIYORDU. 200 ogrenci x 5 gun x 60 TL ile okul
/// 60.000 TL'lik yemegi BEDAVA vermis oluyordu ve ekranda hicbir uyari yoktu --
/// gecmis "Geri alindi" diyordu.
/// </para>
/// <para>
/// Kismi iadede yazilan telafi kaydi da silinir; silinmezse para IKI KEZ sayilirdi.
/// </para>
/// </summary>
public sealed class BulkUndoRestoresChargeTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public BulkUndoRestoresChargeTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>Tam iade geri alininca tahsilat yeniden gecerli olur.</summary>
    [Fact]
    public async Task UndoingAFullRefundMakesTheChargeValidAgain()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);
        var ids = await IdsAsync(student, 3);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));
        Assert.Equal(0m, await CashAsync());

        await Billing().UndoRefundAsync(ids, Guid.NewGuid());

        Assert.Equal(180m, await CashAsync());
    }

    /// <summary>
    /// KISMI iade geri alininca telafi kaydi SILINIR: kalmasaydi para iki kez sayilirdi.
    /// </summary>
    [Fact]
    public async Task UndoingAPartialRefundRemovesTheCompensationRow()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);
        var ids = await IdsAsync(student, 1);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));
        // Bir gun iade: 120 TL kalan kaydi yazildi.
        Assert.Equal(120m, await CashAsync());

        await Billing().UndoRefundAsync(ids, Guid.NewGuid());

        // Telafi kaydi silinip orijinal 180 TL geri gelir; 300 TL DEGIL.
        Assert.Equal(180m, await CashAsync());
        Assert.Equal(1, await db.Set<IncomeTransaction>().CountAsync(x => !x.IsVoided));
    }

    /// <summary>Iade edilecek bir sey yoksa geri alma da bir sey yapmaz.</summary>
    [Fact]
    public async Task UndoingWithoutARefundChangesNothing()
    {
        var (meal, student) = await SeedAsync(6000);
        var service = CreateService();
        await GrantAsync(service, meal, student, days: 3);
        var ids = await IdsAsync(student, 3);

        Assert.Equal(0, await Billing().UndoRefundAsync(ids, Guid.NewGuid()));
        Assert.Equal(180m, await CashAsync());
    }

    // --- yardimcilar ---

    private async Task<(Guid Meal, Guid Student)> SeedAsync(long priceCents)
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9700", FirstName = "Geri", LastName = "Al" };
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

    private async Task<IReadOnlyCollection<Guid>> IdsAsync(Guid studentId, int count) =>
        await db.MealEntitlements.AsNoTracking().Where(x => x.StudentId == studentId)
            .OrderBy(x => x.EntitlementDate).Take(count).Select(x => x.Id).ToListAsync();

    private async Task<decimal> CashAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync()).Sum(x => x.Amount);

    private AuditService Audit() => new(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());
    private EfEntitlementBillingService Billing() => new(db, TimeProvider.System, Audit());

    private MealEntitlementService CreateService() =>
        new(new EfMealEntitlementRepository(db),
            new BusinessDayService(new NoClosures(), new WeekendPolicy()), Billing());

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
