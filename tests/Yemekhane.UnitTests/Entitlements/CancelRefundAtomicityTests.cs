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
/// Iptal ve iade BIRLIKTE olur ya da HIC olmaz.
///
/// <para>
/// Once ikisi AYRI transaction'lardaydi: <c>CancelBulkAsync</c> kendi transaction'ini
/// commit ediyor, sonra <c>RefundAsync</c> ayri bir <c>SaveChangesAsync</c> yapiyordu.
/// Iade adimi cokerse (surec kapanmasi, SQLite BUSY, API timeout) hak iptal edilmis ama
/// para kasada kalmis oluyordu.
/// </para>
/// <para>
/// Daha kotusu DUZELTILEMEZ olmasiydi: operator listeyi yenileyip tekrar denedigi
/// zaman haklar zaten "Cancelled" oldugu icin <c>CancelBulkAsync</c>
/// <c>EntityConflictException</c> firlatiyor ve iade BIR DAHA TETIKLENEMIYORDU.
/// </para>
/// </summary>
public sealed class CancelRefundAtomicityTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private readonly Guid actor = Guid.NewGuid();

    public CancelRefundAtomicityTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// Iade coktugunde IPTAL DE geri alinmalidir: hak "Active" kalir, para kasada kalir,
    /// operator ayni islemi sorunsuz tekrarlayabilir.
    /// </summary>
    [Fact]
    public async Task IadeCokerseIptalDeGeriAlinir()
    {
        var (meal, student) = await SeedAsync();
        await GrantAsync(CreateService(), meal, student, dayCount: 5);
        var ids = await EntitlementIdsAsync(student);

        var failing = CreateService(new ThrowingBilling());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count), actor));

        db.ChangeTracker.Clear();
        // Haklar HALA aktif olmali: yarim kalmis bir iptal birakilmaz.
        Assert.Equal(5, await db.MealEntitlements.CountAsync(x => x.Status == "Active"));
        Assert.Equal(1_250m, await CashTotalAsync());
    }

    /// <summary>
    /// Iade coktukten sonra ayni islem TEKRAR DENENEBILMELIDIR. Once haklar "Cancelled"
    /// kaldigi icin ikinci deneme EntityConflictException aliyor ve iade bir daha
    /// tetiklenemiyordu.
    /// </summary>
    [Fact]
    public async Task IadeCoktuktenSonraAyniIslemTekrarDenenebilir()
    {
        var (meal, student) = await SeedAsync();
        await GrantAsync(CreateService(), meal, student, dayCount: 5);
        var ids = await EntitlementIdsAsync(student);

        var failing = CreateService(new ThrowingBilling());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count), actor));
        db.ChangeTracker.Clear();

        // Ikinci deneme calisan servisle: hem iptal hem iade tamamlanmali.
        await CreateService().CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count), actor);

        db.ChangeTracker.Clear();
        Assert.Equal(5, await db.MealEntitlements.CountAsync(x => x.Status == "Cancelled"));
        Assert.Equal(0m, await CashTotalAsync());
    }

    private async Task<(Guid Meal, Guid Student)> SeedAsync()
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9600", FirstName = "Mert", LastName = "Su" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = 25_000 }); // 250 TL/gun
        await db.SaveChangesAsync();
        return (meal.Id, student.Id);
    }

    private static async Task GrantAsync(MealEntitlementService service, Guid mealTypeId, Guid studentId, int dayCount)
    {
        var startsOn = new DateOnly(2026, 11, 2);
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            startsOn, startsOn, ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: dayCount);
        var preview = await service.PreviewAsync(grant);
        await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
    }

    private async Task<IReadOnlyCollection<Guid>> EntitlementIdsAsync(Guid studentId) =>
        await db.MealEntitlements.AsNoTracking().Where(x => x.StudentId == studentId)
            .Select(x => x.Id).ToListAsync();

    private async Task<decimal> CashTotalAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync())
        .Sum(x => x.Amount);

    private MealEntitlementService CreateService(IEntitlementBillingService? billing = null)
    {
        var audit = new AuditService(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());
        return new MealEntitlementService(new EfMealEntitlementRepository(db),
            new BusinessDayService(new NoClosures(), new WeekendPolicy()),
            billing ?? new EfEntitlementBillingService(db, TimeProvider.System, audit));
    }

    /// <summary>Iade adiminda coken servis: surec kapanmasi / SQLite BUSY benzeri.</summary>
    private sealed class ThrowingBilling : IEntitlementBillingService
    {
        public Task<EntitlementChargeResult> ChargeAsync(EntitlementChargeRequest request, Guid actorId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EntitlementChargeResult(0, 0m, 0));
        public Task<int> RefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("İade yazılamadı (benzetim).");
        public Task<int> UndoRefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
