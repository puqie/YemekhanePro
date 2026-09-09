using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "Hak verdim olmadi sandim, tekrar verdim, simdi iki hak gorunuyor."
///
/// Ayni ogrenciye AYNI GUN ve AYNI OGUN icin tekrar hak vermek IKINCI BIR HAK ACMAZ:
/// mevcut kayit guncellenir (veritabani da benzersiz kisitla garanti eder). Sayinin
/// ikiye cikmasi ancak GUN ya da OGUN farkliysa olur; o zaman da rakam dogrudur.
///
/// Genel Bakis'ta "HAK SAHİBİ" ogrenci sayisi, "HAKEDİŞ" ise HAK ADEDIDIR: bir ogrenciye
/// iki ogun verilirse hak sahibi 1, hakedis 2 gorunur. Rakamlarin anlami budur.
/// </summary>
public sealed class RepeatedGrantTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Day = new(2026, 10, 12);
    private Guid studentId;
    private Guid lunchId;
    private Guid breakfastId;

    public RepeatedGrantTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var student = new Student { StudentNo = "1001", FirstName = "Yağız", LastName = "Savaş" };
        var lunch = new MealType { Name = "Öğle Yemeği" };
        var breakfast = new MealType { Name = "Kahvaltı" };
        db.AddRange(student, lunch, breakfast);
        db.SaveChanges();
        studentId = student.Id; lunchId = lunch.Id; breakfastId = breakfast.Id;
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private EfMealEntitlementRepository Repository() => new(db);

    private Task<BulkEntitlementResult> GrantAsync(Guid mealTypeId, DateOnly date, int quantity = 1) =>
        Repository().UpsertBulkAsync([studentId], mealTypeId, [date], quantity, "Test", null, default);

    /// <summary>Ayni gun + ayni ogun: ikinci verme YENI HAK ACMAZ, gunceller.</summary>
    [Fact]
    public async Task GrantingTwiceForTheSameDayAndMealDoesNotDouble()
    {
        var first = await GrantAsync(lunchId, Day);
        var second = await GrantAsync(lunchId, Day);

        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(0, first.UpdatedCount);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(1, second.UpdatedCount);

        var rows = await db.MealEntitlements.Where(x => x.StudentId == studentId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Quantity);
    }

    /// <summary>Veritabani da garanti eder: ayni ucluden ikinci satir eklenemez.</summary>
    [Fact]
    public async Task TheDatabaseRefusesADuplicateRow()
    {
        await GrantAsync(lunchId, Day);

        db.Add(new MealEntitlement
        {
            StudentId = studentId, MealTypeId = lunchId, EntitlementDate = Day,
            Quantity = 1, Status = "Active"
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Iki AYRI OGUN iki ayri haktir; "2" gorunmesi dogrudur.</summary>
    [Fact]
    public async Task TwoDifferentMealsOnTheSameDayAreTwoEntitlements()
    {
        await GrantAsync(lunchId, Day);
        await GrantAsync(breakfastId, Day);

        var rows = await db.MealEntitlements.Where(x => x.StudentId == studentId && x.EntitlementDate == Day).ToListAsync();
        Assert.Equal(2, rows.Count);
        // Ogrenci SAYISI hala 1; degisen HAK ADEDIDIR.
        Assert.Single(rows.Select(x => x.StudentId).Distinct());
        Assert.Equal(2, rows.Sum(x => x.Quantity));
    }

    /// <summary>Iki AYRI GUN de iki ayri haktir; gunun rozeti yalnizca kendi gununu sayar.</summary>
    [Fact]
    public async Task TwoDaysAreTwoEntitlementsButOnePerDay()
    {
        await GrantAsync(lunchId, Day);
        await GrantAsync(lunchId, Day.AddDays(1));

        Assert.Equal(2, await db.MealEntitlements.CountAsync(x => x.StudentId == studentId));
        Assert.Equal(1, await db.MealEntitlements.CountAsync(x => x.StudentId == studentId && x.EntitlementDate == Day));
    }

    /// <summary>Gunluk adet artirilirsa yine TEK kayit kalir, adedi buyur.</summary>
    [Fact]
    public async Task RaisingTheDailyQuantityUpdatesTheSameRow()
    {
        await GrantAsync(lunchId, Day);

        var second = await GrantAsync(lunchId, Day, quantity: 2);

        Assert.Equal(1, second.UpdatedCount);
        var row = await db.MealEntitlements.SingleAsync(x => x.StudentId == studentId);
        Assert.Equal(2, row.Quantity);
    }

    /// <summary>Iptal edilmis hak tekrar verilince YENIDEN AKTIF olur, ikinci satir acilmaz.</summary>
    [Fact]
    public async Task ReGrantingACancelledEntitlementReactivatesIt()
    {
        await GrantAsync(lunchId, Day);
        var row = await db.MealEntitlements.SingleAsync(x => x.StudentId == studentId);
        row.Status = "Cancelled";
        await db.SaveChangesAsync();

        var second = await GrantAsync(lunchId, Day);

        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(1, second.UpdatedCount);
        var rows = await db.MealEntitlements.Where(x => x.StudentId == studentId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Active", rows[0].Status);
    }
}
