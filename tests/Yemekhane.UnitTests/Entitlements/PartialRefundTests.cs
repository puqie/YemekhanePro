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
/// KISMI iptal KISMI iade uretmelidir: yenen ogunlerin parasi okulda kalir.
///
/// <para>
/// Iade, iptal edilen gun sayisina ORANTILI degildi. Kod <c>EntitlementDate</c> alanini
/// okuyor ama HIC KULLANMIYORDU; yalnizca ogrencinin EN SON hakedis tahsilatini bulup
/// tamamini void ediyordu. 20 gunun 10'u yenmis, kalan 10'u iptal edilmisse 20 gunun
/// TAMAMI iade ediliyor ve okul yenen 10 ogunun parasini kaybediyordu.
/// </para>
/// <para>
/// Ayrica eslestirme yalnizca "ogrenci + aciklama oneki + en son tarih" uzerindendi;
/// ogun turu ve tarih araligi kontrolu yoktu. Ogle hakki iptal edilince Kahvalti'nin
/// tahsilati void edilebiliyordu -- cift yonlu hata.
/// </para>
/// <para>
/// Mevcut EntitlementBillingTests bunu goremiyordu: tek ogrenci + tek tahsilat + tek
/// hakedis kuruyor; o dunyada "en son tahsilati bul" her zaman dogru cevabi verir.
/// </para>
/// </summary>
public sealed class PartialRefundTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private readonly Guid actor = Guid.NewGuid();

    public PartialRefundTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// 10 gunun 4'u iptal edilirse yalnizca 4 gunun bedeli iade edilir; kalan 6 gun
    /// okulda kalir.
    /// </summary>
    [Fact]
    public async Task KismiIptalYalnizcaIptalEdilenGunleriIadeEder()
    {
        var (meal, student) = await SeedAsync(priceCents: 10_000); // 100 TL/gun
        var service = CreateService();
        await GrantAsync(service, meal, student, dayCount: 10);
        Assert.Equal(1_000m, await CashTotalAsync());

        var cancelled = await FirstEntitlementIdsAsync(student, count: 4);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(cancelled, cancelled.Count), actor);

        // 10 gun x 100 TL = 1.000; 4 gun iade -> 600 TL kalmali.
        Assert.Equal(600m, await CashTotalAsync());
    }

    /// <summary>
    /// Tum gunler iptal edilirse tahsilatin tamami iade edilir; kasada bakiye kalmaz.
    /// </summary>
    [Fact]
    public async Task TumGunlerIptalEdilinceTamIadeYapilir()
    {
        var (meal, student) = await SeedAsync(priceCents: 25_000); // 250 TL/gun
        var service = CreateService();
        await GrantAsync(service, meal, student, dayCount: 5);
        Assert.Equal(1_250m, await CashTotalAsync());

        var all = await FirstEntitlementIdsAsync(student, count: 5);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(all, all.Count), actor);

        Assert.Equal(0m, await CashTotalAsync());
    }

    /// <summary>
    /// Ogun turu ONEMLI: Ogle iptal edilince Kahvalti'nin tahsilatina DOKUNULMAMALIDIR.
    /// Eskiden "en son tahsilat" secildigi icin yanlis ogunun parasi iade ediliyordu.
    /// </summary>
    [Fact]
    public async Task BaskaOgununTahsilatinaDokunulmaz()
    {
        var lunch = new MealType { Name = "Öğle" };
        var breakfast = new MealType { Name = "Kahvaltı" };
        var student = new Student { StudentNo = "9500", FirstName = "Efe", LastName = "Kara" };
        db.AddRange(lunch, breakfast, student);
        await db.SaveChangesAsync();
        db.AddRange(new MealTypePrice { MealTypeId = lunch.Id, PriceCents = 20_000 },      // 200 TL
                    new MealTypePrice { MealTypeId = breakfast.Id, PriceCents = 5_000 });  // 50 TL
        await db.SaveChangesAsync();
        var service = CreateService();

        // AYNI gunlerde iki ogun: tarih araligi ayirt etmez, yalnizca OGUN ayirt eder.
        // Araliklar farkli olsaydi test ogun eslestirmesini olcmezdi (mutasyonla
        // dogrulandi: ogun kontrolu kaldirildiginda test yine geciyordu).
        // Kahvalti ONCE yazilir: iade dongusu tahsilatlari tarihe gore geziyor, bu yuzden
        // "once yazilan" tahsilat once denenir. Ogle once yazilsaydi dogru sonuc TESADUFEN
        // cikardi ve test ogun eslestirmesini olcmezdi (mutasyonla dogrulandi).
        await GrantAsync(service, breakfast.Id, student.Id, dayCount: 5);  // 5 x 50 = 250 TL
        await GrantAsync(service, lunch.Id, student.Id, dayCount: 5);      // 5 x 200 = 1.000 TL, AYNI gunler
        Assert.Equal(1_250m, await CashTotalAsync());

        // Ogle haklarinin TAMAMI iptal ediliyor.
        var lunchIds = await db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == student.Id && x.MealTypeId == lunch.Id)
            .Select(x => x.Id).ToListAsync();
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(lunchIds, lunchIds.Count), actor);

        // Yalnizca Ogle'nin 1.000 TL'si iade edilmeli; Kahvalti'nin 250 TL'si KALMALI.
        Assert.Equal(250m, await CashTotalAsync());
        // Kahvalti tahsilati void EDILMEMIS olmali.
        var breakfastCharge = await db.Set<IncomeTransaction>().AsNoTracking()
            .SingleAsync(x => x.MealTypeId == breakfast.Id);
        Assert.False(breakfastCharge.IsVoided);
    }

    private async Task<(Guid Meal, Guid Student)> SeedAsync(long priceCents)
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9400", FirstName = "Nil", LastName = "Ay" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = priceCents });
        await db.SaveChangesAsync();
        return (meal.Id, student.Id);
    }

    private static Task GrantAsync(MealEntitlementService service, Guid mealTypeId, Guid studentId, int dayCount,
        DateOnly? startsOn = null) =>
        ApplyAsync(service, mealTypeId, studentId, dayCount, startsOn ?? new DateOnly(2026, 10, 1));

    private static async Task ApplyAsync(MealEntitlementService service, Guid mealTypeId, Guid studentId,
        int dayCount, DateOnly startsOn)
    {
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            startsOn, startsOn, ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: dayCount);
        var preview = await service.PreviewAsync(grant);
        await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
    }

    private async Task<IReadOnlyCollection<Guid>> FirstEntitlementIdsAsync(Guid studentId, int count) =>
        await db.MealEntitlements.AsNoTracking().Where(x => x.StudentId == studentId)
            .OrderBy(x => x.EntitlementDate).Take(count).Select(x => x.Id).ToListAsync();

    private async Task<decimal> CashTotalAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync())
        .Sum(x => x.Amount);

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
