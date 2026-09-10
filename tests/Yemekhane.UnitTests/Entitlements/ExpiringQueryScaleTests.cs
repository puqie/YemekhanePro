using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "Bitisi yaklasanlar" listesi TUM aktif hakedis satirlarini hafizaya CEKMEMELIDIR.
///
/// Once oyle yapiyordu: ozet (ilk/son gun, toplam, kalan) hafizada hesaplandigi icin
/// her satir okunuyordu. 400 ogrenci x 180 gun'luk gercek bir okulda 72.401 satir
/// okunup sonuc HIC CIKMAYABILIYORDU (olculdu: 394 ms, 0 sonuc).
///
/// Artik ozet TEK GROUP BY ile veritabaninda hesaplanir. Bu testler o kazanimi korur:
/// veri buyudukce is yuku BUYUMEMELIDIR.
/// </summary>
public sealed class ExpiringQueryScaleTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Today = new(2026, 9, 20);
    private Guid lunchId;

    public ExpiringQueryScaleTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var lunch = new MealType { Name = "Öğle Yemeği" };
        db.Add(lunch);
        db.SaveChanges();
        lunchId = lunch.Id;
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>Uzun donemli (gunluk hak) ogrenciler tohumlar.</summary>
    private void Seed(int studentCount, DateOnly from, int days)
    {
        for (var index = 0; index < studentCount; index++)
        {
            var student = new Student
            {
                StudentNo = (6000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FirstName = "Ad" + index, LastName = "Soyad",
                SearchName = "ad" + index + " soyad", IsActive = true
            };
            db.Add(student);
            db.SaveChanges();
            for (var day = 0; day < days; day++)
                db.Add(new MealEntitlement
                {
                    StudentId = student.Id, MealTypeId = lunchId,
                    EntitlementDate = from.AddDays(day), Quantity = 1, Status = "Active"
                });
        }
        db.SaveChanges();
    }

    /// <summary>
    /// Esigin cok otesinde biten donemler HIC listelenmez ve satirlari da OKUNMAZ.
    /// Once bu durumda bile butun satirlar cekiliyordu.
    /// </summary>
    [Fact]
    public async Task LongRunningPeriodsProduceNoResultAndAreNotMaterialized()
    {
        // 120 gunluk donem: bugunden cok sonra biter, esigin (10 gun) disindadir.
        Seed(studentCount: 20, from: Today, days: 120);

        var rows = await new EfMealEntitlementRepository(db)
            .ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default);

        Assert.Empty(rows);
    }

    /// <summary>
    /// Ogrenci sayisi ARTINCA sonuc sayisi artar ama HER OGRENCI ICIN TEK satir doner:
    /// 180 gunluk donem 180 satir degil 1 ozet uretir.
    /// </summary>
    [Fact]
    public async Task EachStudentMealPairProducesExactlyOneSummary()
    {
        // 5 gun sonra biten donem: esigin icinde.
        Seed(studentCount: 30, from: Today.AddDays(-100), days: 106);

        var rows = await new EfMealEntitlementRepository(db)
            .ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default);

        Assert.Equal(30, rows.Count);
        Assert.All(rows, row => Assert.Equal(5, row.DaysLeft));
    }

    /// <summary>
    /// Uzun donemin toplamlari DOGRU hesaplanir: ozet veritabaninda uretildigi icin
    /// gecmis/gelecek ayrimi da orada yapilir.
    /// </summary>
    [Fact]
    public async Task TotalsAreCorrectForALongPeriod()
    {
        // 100 gun gecmis + bugun + 5 gun gelecek = 106 gun.
        Seed(studentCount: 1, from: Today.AddDays(-100), days: 106);

        var row = Assert.Single(await new EfMealEntitlementRepository(db)
            .ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default));

        Assert.Equal(106, row.TotalQuantity);
        Assert.Equal(0, row.ConsumedQuantity);
        // Bugun dahil 6 gun kalir (bugun + 5 gun).
        Assert.Equal(6, row.RemainingQuantity);
        // Gecmis 100 gun kullanilmadan yandi.
        Assert.Equal(100, row.ExpiredQuantity);
    }

    /// <summary>Kalani sifir olan ve suresi GECMEMIS donem listelenmez: yenilenecek sey yok.</summary>
    [Fact]
    public async Task AFullyConsumedFuturePeriodIsNotListed()
    {
        var student = new Student
        {
            StudentNo = "7001", FirstName = "Dolu", LastName = "Kullanim",
            SearchName = "dolu kullanim", IsActive = true
        };
        db.Add(student);
        db.SaveChanges();
        for (var day = 0; day <= 5; day++)
            db.Add(new MealEntitlement
            {
                StudentId = student.Id, MealTypeId = lunchId, EntitlementDate = Today.AddDays(day),
                Quantity = 1, ConsumedQuantity = 1, Status = "Active"
            });
        db.SaveChanges();

        Assert.Empty(await new EfMealEntitlementRepository(db)
            .ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default));
    }
}
