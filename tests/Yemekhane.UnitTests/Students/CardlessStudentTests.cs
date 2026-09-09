using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Anasinifi ogrencileri KARTSIZ girecek ama sayilari takip edilecek. Kart numarasi
/// istege baglidir: kartsiz ogrenci kaydedilir, listede gorunur, ad/soyadla bulunur ve
/// hakedis verilebilir. Kart yalnizca TURNIKEDEN gecis icin gerekir.
/// </summary>
public sealed class CardlessStudentTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public CardlessStudentTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private StudentService Service() => new(new EfStudentRepository(db));
    private EfStudentRepository Repository() => new(db);

    [Fact]
    public async Task AStudentCanBeSavedWithoutACardOrANumber()
    {
        var created = await Service().CreateAsync(new SaveStudentRequest("", "Ayşe", "Yılmaz"));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("", created.StudentNo);
        // Kart ayri bir kayittir; ogrenci kartsiz olusur.
        Assert.False(await db.StudentCards.AnyAsync(x => x.StudentId == created.Id));
    }

    /// <summary>Kartsiz ogrenci ad/soyadla BULUNABILMELI; kasa ekrani bu aramayi kullanir.</summary>
    [Fact]
    public async Task ACardlessStudentIsFoundByName()
    {
        await Service().CreateAsync(new SaveStudentRequest("", "Ayşe", "Yılmaz"));

        var result = await Repository().SearchAsync(new StudentQuery(Search: "yılmaz", IsActive: true), default);

        Assert.Single(result.Items);
        Assert.Equal("Ayşe", result.Items[0].FirstName);
        Assert.Null(result.Items[0].CardNumber);
    }

    /// <summary>Kartsiz ogrenciler listede yan yana durabilir; kart benzersizligi onlari engellememeli.</summary>
    [Fact]
    public async Task ManyCardlessStudentsCoexist()
    {
        for (var index = 0; index < 5; index++)
            await Service().CreateAsync(new SaveStudentRequest("", "Ad" + index, "Soyad"));

        var result = await Repository().SearchAsync(new StudentQuery(Search: "soyad", IsActive: true), default);

        Assert.Equal(5, result.TotalCount);
    }

    /// <summary>Kartsiz ogrenciye hakedis verilebilir; sayimda gorunmesinin sarti budur.</summary>
    [Fact]
    public async Task ACardlessStudentCanHoldAnEntitlement()
    {
        var student = await Service().CreateAsync(new SaveStudentRequest("", "Ayşe", "Yılmaz"));
        var meal = new MealType { Name = "Öğle Yemeği" };
        db.Add(meal);
        await db.SaveChangesAsync();

        db.Add(new MealEntitlement
        {
            StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = new DateOnly(2026, 10, 12),
            Quantity = 1, Status = "Active"
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.MealEntitlements.CountAsync(x => x.StudentId == student.Id));
    }

    /// <summary>Kart numarasi VERILDIYSE hala benzersizdir; iki ogrenciye ayni kart verilemez.</summary>
    [Fact]
    public async Task AGivenCardNumberStaysUnique()
    {
        var first = await Service().CreateAsync(new SaveStudentRequest("1001", "Ayşe", "Yılmaz"));
        var second = await Service().CreateAsync(new SaveStudentRequest("1002", "Mehmet", "Demir"));
        db.Add(new StudentCard { StudentId = first.Id, CardNumber = "8350001", ValidFrom = DateTimeOffset.UtcNow, IsActive = true });
        await db.SaveChangesAsync();

        db.Add(new StudentCard { StudentId = second.Id, CardNumber = "8350001", ValidFrom = DateTimeOffset.UtcNow, IsActive = true });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
