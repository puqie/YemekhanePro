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
/// Ayni hakedis IKINCI kez verildiginde kasaya IKINCI kez ucret YAZILMAMALIDIR.
///
/// <para>
/// Hakedis tablosunda (ogrenci, tarih, ogun) tekil; ikinci verme yeni satir acmaz,
/// mevcut satiri gunceller (upsert). Ancak ucret <c>dates.Count</c> uzerinden, yani
/// "aralikta kac gun var" uzerinden hesaplaniyordu -- "kac YENI hak yaratildi" uzerinden
/// degil. Tekrar korumasi OperationId'e bagliydi ve masaustu her basarili islemden
/// sonra onu YENILIYOR; ikinci islem yeni kimlikle geldigi icin koruma devreye
/// girmiyordu.
/// </para>
/// <para>
/// Sonuc: memur "acaba yapmis miydim?" diye ayni araligi tekrar uygulayinca hakedis
/// tablosunda tek satir kaliyor ama kasada IKI tahsilat olusuyordu. 100 ogrenci x
/// 5.000 TL = 500.000 TL mukerrer gelir. Onizleme "0 yeni, 100 guncelleme" dese bile
/// toplam bedel yine tam tutari gosteriyordu.
/// </para>
/// <para>
/// Mevcut EntitlementBillingTests bunu goremiyordu: ChargeAsync'i DOGRUDAN cagiriyor,
/// MealEntitlementService'in ucretlendirme entegrasyonunu hic kurmuyordu.
/// </para>
/// </summary>
public sealed class EntitlementDoubleChargeTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public EntitlementDoubleChargeTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task AyniHakedisIkinciKezVerilinceKasayaTekrarYazilmaz()
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9001", FirstName = "Deniz", LastName = "Yıldız" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = 25_000 }); // 250 TL/gun
        await db.SaveChangesAsync();
        var service = CreateService();

        // 5 gunluk hak: 5 x 250 = 1.250 TL kasaya yazilmali.
        var first = await ApplyAsync(service, meal.Id, student.Id, dayCount: 5);
        var afterFirst = await ChargedTotalAsync();

        // Ayni islem tekrar: hicbir YENI hak yaratilmaz (hepsi guncelleme).
        var second = await ApplyAsync(service, meal.Id, student.Id, dayCount: 5);
        var afterSecond = await ChargedTotalAsync();

        Assert.Equal(5, first.CreatedCount);
        Assert.Equal(1_250m, afterFirst);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(5, second.UpdatedCount);
        // Ikinci islem yeni hak yaratmadi; kasa toplami DEGISMEMELIDIR.
        Assert.Equal(1_250m, afterSecond);
    }

    /// <summary>
    /// Kismen ortusen aralikta yalnizca YENI gunlerin bedeli yazilmalidir: 5 gunluk hak
    /// varken 8 gune cikarilirsa fark 3 gun kadar tahsil edilir, 8 gun kadar degil.
    /// </summary>
    [Fact]
    public async Task KismenOrtusenAralikYalnizcaYeniGunleriTahsilEder()
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9002", FirstName = "Kaan", LastName = "Aslan" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = 10_000 }); // 100 TL/gun
        await db.SaveChangesAsync();
        var service = CreateService();

        await ApplyAsync(service, meal.Id, student.Id, dayCount: 5);
        var afterFirst = await ChargedTotalAsync();
        var second = await ApplyAsync(service, meal.Id, student.Id, dayCount: 8);
        var afterSecond = await ChargedTotalAsync();

        Assert.Equal(500m, afterFirst);
        Assert.Equal(3, second.CreatedCount);
        Assert.Equal(5, second.UpdatedCount);
        // Yalnizca 3 yeni gun: 500 + 300 = 800.
        Assert.Equal(800m, afterSecond);
    }

    private async Task<BulkEntitlementResult> ApplyAsync(MealEntitlementService service, Guid mealTypeId,
        Guid studentId, int dayCount)
    {
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 14), ChargeToCash: true,
            // Masaustu her basarili islemden sonra yeni kimlik uretir; tekrar korumasi
            // bu yuzden ikinci islemi yakalayamaz. Gercek davranis taklit ediliyor.
            OperationId: Guid.NewGuid(), DayCount: dayCount);
        var preview = await service.PreviewAsync(grant);
        return await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
    }

    private async Task<decimal> ChargedTotalAsync() =>
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
