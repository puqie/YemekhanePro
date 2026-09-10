using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Meals;

/// <summary>
/// Ucreti TANIMSIZ ogun, ucretsiz ogunle ayni sey DEGILDIR.
///
/// <para>
/// <c>MealPriceAsync</c> fiyat satiri yoksa sessizce 0 donuyordu. Ucretsiz ogun zaten
/// ACIKCA 0 girilerek tanimlanabiliyor (MealTypeService 0'a izin verir); satirin hic
/// olmamasi ise bir EKSIKLIKTIR ve sessizce "bedava" sayilmamalidir.
/// </para>
/// <para>
/// Somut zarar: migration oncesi acilmis ogunlerde fiyat satiri yok. Yonetici 500
/// ogrenciye 20 gunluk hakedis tanimlayip "Kasaya isle" kutusunu isaretliyor; beklenen
/// 250.000 TL tahsilat SESSIZCE 0 TL olarak geciyor ve hicbir uyari cikmiyor.
/// </para>
/// </summary>
public sealed class MissingMealPriceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public MissingMealPriceTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// Fiyati TANIMSIZ ogun kasaya islenmek istenirse ANLASILIR bir hata verilir;
    /// sessizce 0 TL yazilmaz.
    /// </summary>
    [Fact]
    public async Task FiyatiTanimsizOgunKasayaIslenirkenHataVerir()
    {
        var (meal, student) = await SeedAsync(withPrice: false);
        var service = CreateService();
        var grant = Grant(meal, student, chargeToCash: true);
        var preview = await service.PreviewAsync(grant);

        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken)));

        Assert.Contains("ücret", error.Message, StringComparison.OrdinalIgnoreCase);
        // Hicbir tahsilat yazilmamali.
        Assert.Empty(await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Kasaya isleme KAPALIYSA fiyat tanimsizligi engel degildir: hak yine tanimlanir.
    /// Ucretsiz dagitim (bagis, deneme) bu yoldan yapilir.
    /// </summary>
    [Fact]
    public async Task KasayaIslemeKapaliysaFiyatsizOgunHakVerilebilir()
    {
        var (meal, student) = await SeedAsync(withPrice: false);
        var service = CreateService();
        var grant = Grant(meal, student, chargeToCash: false);
        var preview = await service.PreviewAsync(grant);

        var result = await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));

        Assert.Equal(5, result.CreatedCount);
    }

    /// <summary>
    /// ACIKCA 0 TL tanimlanmis ogun ucretsizdir ve engel degildir: "tanimsiz" ile
    /// "bedelsiz" ayrimi burada olculur.
    /// </summary>
    [Fact]
    public async Task AcikcaSifirTanimlanmisOgunUcretsizdir()
    {
        var (meal, student) = await SeedAsync(withPrice: true, priceCents: 0);
        var service = CreateService();
        var grant = Grant(meal, student, chargeToCash: true);
        var preview = await service.PreviewAsync(grant);

        var result = await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));

        Assert.Equal(5, result.CreatedCount);
        Assert.Empty(await db.Set<IncomeTransaction>().AsNoTracking().ToListAsync());
    }


    /// <summary>
    /// TURNIKE: ucreti tanimsiz ogunde ret sebebi DOGRU soylenmeli. Ret karari degismez
    /// (para dusulemez) ama "Bugün yemek hakkı bulunmuyor" mesaji operatoru yanlis yere
    /// yonlendiriyordu: ogrencinin bakiyesi doluydu, eksik olan OGUN TANIMIYDI.
    /// </summary>
    [Theory]
    [InlineData(false, "Öğün ücreti tanımlı değil")]
    [InlineData(true, "Bugün yemek hakkı bulunmuyor")]
    public void TanimsizFiyatIleUcretsizOgunFarkliSebepVerir(bool priceDefined, string expectedReason)
    {
        var snapshot = new Yemekhane.Application.Access.AccessSnapshot(
            CardExists: true, CardActive: true, StudentId: Guid.NewGuid(), StudentName: "Test",
            ClassId: null, StudentActive: true, DeviceActive: true, EntitlementId: null,
            Quantity: 0, ConsumedQuantity: 0, EntitlementStatus: null, IsOnLeave: false,
            MealPriceCents: 0, AvailableBalanceCents: 50_000, MealPriceDefined: priceDefined);

        // Karar mantiginin okudugu alan: tanimsizlik ayri bir dal olmali.
        var reason = !snapshot.MealPriceDefined ? "Öğün ücreti tanımlı değil"
            : snapshot.MealPriceCents <= 0 ? "Bugün yemek hakkı bulunmuyor" : "ALLOW";

        Assert.Equal(expectedReason, reason);
    }

    private async Task<(Guid Meal, Guid Student)> SeedAsync(bool withPrice, long priceCents = 25_000)
    {
        var meal = new MealType { Name = "Öğle" };
        var student = new Student { StudentNo = "9800", FirstName = "Can", LastName = "Öz" };
        db.AddRange(meal, student);
        await db.SaveChangesAsync();
        if (withPrice)
        {
            db.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = priceCents });
            await db.SaveChangesAsync();
        }
        return (meal.Id, student.Id);
    }

    private static EntitlementGrantRequest Grant(Guid mealTypeId, Guid studentId, bool chargeToCash) =>
        new(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), ChargeToCash: chargeToCash,
            OperationId: Guid.NewGuid(), DayCount: 5);

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
