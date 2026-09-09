using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "Veli soruyor: cocugun kac ogun hakki kaldi, yuklemesi ne zaman bitiyor?"
///
/// Bu bilgi hicbir ekranda yoktu; kullanici tarih araligini genisletip satirlari tek tek
/// saymak zorunda kaliyordu. Donem ozeti kalan ogunu, SON GUNU ve yenileme gununu verir.
///
/// "Kalan" YALNIZCA bugun ve sonrasidir: gecmiste kullanilmayan hak yanmistir, veliye
/// "kalan" diye soylenirse yanlis olur.
/// </summary>
public sealed class EntitlementPeriodTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Today = new(2026, 9, 20);
    private Guid studentId;
    private Guid lunchId;
    private Guid breakfastId;

    public EntitlementPeriodTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var schoolClass = new SchoolClass { Name = "5A", SearchName = "5a" };
        var student = new Student
        {
            StudentNo = "5001", FirstName = "Ceylin Mihra", LastName = "Yılmaz",
            SearchName = "ceylin mihra yilmaz", ClassId = schoolClass.Id, IsActive = true
        };
        var lunch = new MealType { Name = "Öğle Yemeği" };
        var breakfast = new MealType { Name = "Kahvaltı" };
        db.AddRange(schoolClass, student, lunch, breakfast);
        db.Add(new Parent { StudentId = student.Id, Name = "Veli", NormalizedPhone = "5551112233", IsPrimary = true });
        db.SaveChanges();
        studentId = student.Id; lunchId = lunch.Id; breakfastId = breakfast.Id;
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private EfMealEntitlementRepository Repository() => new(db);

    /// <summary>16 Eylul'den itibaren 20 gunluk hak yazar (hafta sonu dahil, sadelik icin).</summary>
    private void Grant(Guid mealTypeId, DateOnly from, int days, int consumedDays = 0, string status = "Active")
    {
        for (var index = 0; index < days; index++)
            db.Add(new MealEntitlement
            {
                StudentId = studentId, MealTypeId = mealTypeId, EntitlementDate = from.AddDays(index),
                Quantity = 1, ConsumedQuantity = index < consumedDays ? 1 : 0, Status = status
            });
        db.SaveChanges();
    }

    [Fact]
    public async Task ThePeriodReportsTheLastDayAndTheRenewalDay()
    {
        // 16 Eylul + 20 gun => son gun 5 Ekim, yenileme 6 Ekim.
        Grant(lunchId, new DateOnly(2026, 9, 16), 20);

        var period = Assert.Single(await Repository().PeriodsAsync(studentId, Today, default));

        Assert.Equal(new DateOnly(2026, 9, 16), period.FirstDate);
        Assert.Equal(new DateOnly(2026, 10, 5), period.LastDate);
        Assert.Equal(new DateOnly(2026, 10, 6), period.RenewFrom);
        Assert.False(period.IsExpired);
    }

    /// <summary>Kalan, BUGUNDEN itibaren sayilir; gecmisteki kullanilmamis gunler yanmistir.</summary>
    [Fact]
    public async Task RemainingCountsOnlyTodayOnwardAndBurntDaysAreReportedSeparately()
    {
        // 16-19 Eylul gecmis (4 gun), 20 Eylul bugun. Ilk 2 gun kullanilmis.
        Grant(lunchId, new DateOnly(2026, 9, 16), 20, consumedDays: 2);

        var period = Assert.Single(await Repository().PeriodsAsync(studentId, Today, default));

        Assert.Equal(16, period.RemainingQuantity);   // 20 Eylul - 5 Ekim
        Assert.Equal(2, period.ExpiredQuantity);      // 18-19 Eylul kullanilmadan gecti
        Assert.Equal(2, period.ConsumedQuantity);
        Assert.Equal(20, period.TotalQuantity);
    }

    /// <summary>Suresi gecmis donemde DaysLeft NEGATIFTIR; "yenileme gecikti" uyarisi bundan cikar.</summary>
    [Fact]
    public async Task AnExpiredPeriodReportsNegativeDaysLeft()
    {
        Grant(lunchId, new DateOnly(2026, 9, 1), 5);   // son gun 5 Eylul, bugun 20 Eylul

        var period = Assert.Single(await Repository().PeriodsAsync(studentId, Today, default));

        Assert.True(period.IsExpired);
        Assert.Equal(-15, period.DaysLeft);
        Assert.Equal(0, period.RemainingQuantity);
    }

    /// <summary>Her ogun AYRI donemdir: ogle ve kahvalti ayri satir, ayri bitis gunu.</summary>
    [Fact]
    public async Task EachMealIsItsOwnPeriod()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 20);
        Grant(breakfastId, new DateOnly(2026, 9, 16), 5);

        var periods = await Repository().PeriodsAsync(studentId, Today, default);

        Assert.Equal(2, periods.Count);
        Assert.Equal(new DateOnly(2026, 10, 5), periods.Single(x => x.MealName == "Öğle Yemeği").LastDate);
        Assert.Equal(new DateOnly(2026, 9, 20), periods.Single(x => x.MealName == "Kahvaltı").LastDate);
    }

    /// <summary>Iptal edilmis haklar donemi hic olusturmaz; kalan diye gosterilirse yanlis olur.</summary>
    [Fact]
    public async Task CancelledEntitlementsAreExcluded()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 20, status: "Cancelled");

        Assert.Empty(await Repository().PeriodsAsync(studentId, Today, default));
    }

    [Fact]
    public async Task AStudentWithNoEntitlementsHasNoPeriods() =>
        Assert.Empty(await Repository().PeriodsAsync(studentId, Today, default));

    /// <summary>Ozet ogrenciyi TANITIR: veli telefondayken ad, sinif ve telefon gorunur.</summary>
    [Fact]
    public async Task ThePeriodCarriesTheStudentIdentity()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 20);

        var period = Assert.Single(await Repository().PeriodsAsync(studentId, Today, default));

        Assert.Equal("5001", period.StudentNo);
        Assert.Equal("Ceylin Mihra Yılmaz", period.StudentName);
        Assert.Equal("5A", period.ClassName);
        Assert.Equal("5551112233", period.ParentPhone);
    }

    // --- Bitisi yaklasanlar ---

    /// <summary>Esigin ICINDE bitecekler listelenir.</summary>
    [Fact]
    public async Task ExpiringListsPeriodsEndingWithinTheThreshold()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 10);   // son gun 25 Eylul => 5 gun kaldi

        var rows = await Repository().ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default);

        var row = Assert.Single(rows);
        Assert.Equal(5, row.DaysLeft);
    }

    /// <summary>Esigin DISINDA bitecekler listelenmez; her donem uyari uretmemeli.</summary>
    [Fact]
    public async Task ExpiringSkipsPeriodsBeyondTheThreshold()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 60);   // cok ilerideki bir bitis

        Assert.Empty(await Repository().ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default));
    }

    /// <summary>Suresi COKTAN GECMIS olanlar esikten bagimsiz HER ZAMAN girer.</summary>
    [Fact]
    public async Task ExpiringAlwaysIncludesAlreadyExpiredPeriods()
    {
        Grant(lunchId, new DateOnly(2026, 9, 1), 5);   // 5 Eylul'de bitmis

        var rows = await Repository().ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 0), Today, default);

        Assert.True(Assert.Single(rows).IsExpired);
    }

    /// <summary>Pasife alinmis ogrencinin yenilenecek hakki yoktur; listeyi kirletmemeli.</summary>
    [Fact]
    public async Task ExpiringSkipsInactiveStudents()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 10);
        var student = await db.Students.SingleAsync(x => x.Id == studentId);
        student.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Empty(await Repository().ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default));
    }

    /// <summary>Arama ad ve sinif uzerinde calisir; Turkce i/I ayrimina takilmamali.</summary>
    [Fact]
    public async Task ExpiringCanBeSearchedByName()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 10);

        Assert.Single(await Repository().ExpiringAsync(new ExpiringEntitlementQuery(Search: "ceylin"), Today, default));
        Assert.Empty(await Repository().ExpiringAsync(new ExpiringEntitlementQuery(Search: "bulunmayan"), Today, default));
    }

    /// <summary>Esik siralamasi: en acil (en az gun kalan) basta gelir.</summary>
    [Fact]
    public async Task ExpiringIsOrderedByUrgency()
    {
        Grant(lunchId, new DateOnly(2026, 9, 16), 10);      // 25 Eylul => 5 gun
        Grant(breakfastId, new DateOnly(2026, 9, 16), 6);   // 21 Eylul => 1 gun

        var rows = await Repository().ExpiringAsync(new ExpiringEntitlementQuery(WithinDays: 10), Today, default);

        Assert.Equal([1, 5], rows.Select(x => x.DaysLeft));
    }
}
