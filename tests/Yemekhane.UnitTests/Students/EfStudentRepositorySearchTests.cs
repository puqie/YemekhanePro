using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Ogrenci listesi aramasi Turkce harflere duyarsiz olmali. SQLite'in LIKE'i yalnizca
/// ASCII'de buyuk/kucuk harf duyarsizdir; ham FirstName/LastName sutunlari uzerinden
/// yapilan arama "ali" ile ALİ'yi, "öz" ile ÖZTÜRK'u bulamiyordu (canli API: 0 sonuc).
/// Bu testler gercek SQLite uzerinde kosar; sahte bellek saglayicisi LIKE'in
/// davranisini taklit etmedigi icin hatayi yakalayamazdi.
/// </summary>
public sealed class EfStudentRepositorySearchTests
{
    [Theory]
    [InlineData("ali", "5009", "5010", "5011")]      // kucuk harf, noktasiz i -> ALİ
    [InlineData("ALI", "5009", "5010", "5011")]      // buyuk harf, noktasiz I -> ALİ
    [InlineData("ALİ", "5009", "5010", "5011")]      // birebir
    [InlineData("öz", "5009", "5010", "5028")]       // soyad basi: ÖZTÜRK, ÖZDEMİR
    [InlineData("öztürk", "5009", "5010")]
    [InlineData("ÇET", "5011")]                      // ÇETİN
    [InlineData("5009", "5009")]                     // numara BASTAN: 8350090 kartini getirmez
    [InlineData("8350010", "5010")]                  // aktif kart
    [InlineData("6-E", "5009")]                     // sınıf
    [InlineData("B Şubesi", "5009")]                // şube
    [InlineData("Bilişim", "5009")]                 // bölüm
    [InlineData("Nöbetçi", "5009")]                 // görev
    [InlineData("90555", "5009")]                   // veli telefonu
    [InlineData("VELİ NUR", "5009")]                // veli adı
    public async Task GenelAramaTurkceHarfeDuyarsiz(string term, params string[] expectedNos)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = await SeedAsync(connection);
        var repository = new EfStudentRepository(db);

        var result = await repository.SearchAsync(new StudentQuery(Search: term, PageSize: 50), CancellationToken.None);

        Assert.Equal(expectedNos.OrderBy(x => x), result.Items.Select(x => x.StudentNo).OrderBy(x => x));
    }

    [Fact]
    public async Task AdVeSoyadFiltreleriTurkceHarfeDuyarsiz()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = await SeedAsync(connection);
        var repository = new EfStudentRepository(db);

        var byFirst = await repository.SearchAsync(new StudentQuery(FirstName: "ali"), CancellationToken.None);
        Assert.Equal(["5009", "5010", "5011"], byFirst.Items.Select(x => x.StudentNo).OrderBy(x => x));

        var byLast = await repository.SearchAsync(new StudentQuery(LastName: "öz"), CancellationToken.None);
        Assert.Equal(["5009", "5010", "5028"], byLast.Items.Select(x => x.StudentNo).OrderBy(x => x));

        // Ad filtresi soyadla eslesmez: "öz" adi ÖZ ile baslayan kimse yok.
        var noFirst = await repository.SearchAsync(new StudentQuery(FirstName: "öz"), CancellationToken.None);
        Assert.Empty(noFirst.Items);

        // Soyad filtresi adla eslesmez.
        var noLast = await repository.SearchAsync(new StudentQuery(LastName: "ali"), CancellationToken.None);
        Assert.Empty(noLast.Items);
    }

    private static async Task<YemekhaneDbContext> SeedAsync(SqliteConnection connection)
    {
        var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var class6E = new SchoolClass { Name = "6-E", SearchName = "6-E" };
        var sectionB = new Section { Name = "B Şubesi" };
        var department = new Department { Name = "Bilişim" };
        var job = new Job { Name = "Nöbetçi" };
        db.AddRange(class6E, sectionB, department, job);

        var s5009 = new Student
        {
            StudentNo = "5009", FirstName = "ALİ", LastName = "ÖZTÜRK",
            ClassId = class6E.Id, SectionId = sectionB.Id, DepartmentId = department.Id, JobId = job.Id,
        };
        var s5010 = new Student { StudentNo = "5010", FirstName = "ALİ", LastName = "ÖZTÜRK" };
        var s5011 = new Student { StudentNo = "5011", FirstName = "ALİ", LastName = "ÇETİN" };
        var s5028 = new Student { StudentNo = "5028", FirstName = "AYŞE", LastName = "ÖZDEMİR" };
        var s5090 = new Student { StudentNo = "5090", FirstName = "HALİL", LastName = "KAYA" };
        db.Students.AddRange(s5009, s5010, s5011, s5028, s5090);
        db.StudentCards.AddRange(
            new StudentCard { StudentId = s5010.Id, CardNumber = "8350010", ValidFrom = DateTimeOffset.UtcNow, IsActive = true },
            new StudentCard { StudentId = s5090.Id, CardNumber = "8350090", ValidFrom = DateTimeOffset.UtcNow, IsActive = true });
        db.Parents.Add(new Parent { StudentId = s5009.Id, Name = "VELİ NUR", NormalizedPhone = "905551234567", IsActive = true });
        await db.SaveChangesAsync();
        return db;
    }
}
