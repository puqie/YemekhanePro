using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// TEK ARAMA KUTUSU: kullanici ad, ogrenci no, kart no ve sinifi ayri ayri kutulara
/// degil, TEK bir kutuya yazar.
///
/// <para>
/// Once dort ayri filtre kutusu vardi ve kullanici aradigi seyin hangi kutuya ait
/// oldugunu bilmek zorundaydi: kart numarasini "Ogrenci no" kutusuna yazan hicbir
/// sonuc alamiyor, sebebini de goremiyordu. Ayrica dokuz kutu %125 olcekte ekrandan
/// tasiyordu.
/// </para>
/// <para>
/// Arama TURKCE normalizasyon kullanir (<see cref="TurkishSearchText"/>): "ismail"
/// ile "İsmail", "cigdemli" ile "Çiğdemli" eslesmelidir -- aksi halde kullanici
/// dogru yazdigi ismi bulamaz ve kaydi yok saniyor.
/// </para>
/// </summary>
public sealed class EntitlementSearchTests
{
    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);

    /// <summary>Uc ogrenci, uc kart, uc hakedis kurar; ikisi ayni sinifta.</summary>
    private static async Task SeedAsync(YemekhaneDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var meal = new MealType { Id = Guid.NewGuid(), Name = "Öğle", CreatedAt = now };
        db.Set<MealType>().Add(meal);
        var sinif = new SchoolClass { Id = Guid.NewGuid(), Name = "8A", CreatedAt = now };
        db.Set<SchoolClass>().Add(sinif);

        void Add(string studentNo, string first, string last, string card, Guid? classId)
        {
            var student = new Student
            {
                Id = Guid.NewGuid(),
                StudentNo = studentNo,
                FirstName = first,
                LastName = last,
                SearchName = TurkishSearchText.NormalizeFullName(first, last),
                ClassId = classId,
                IsActive = true,
                CreatedAt = now
            };
            db.Students.Add(student);
            db.StudentCards.Add(new StudentCard
            {
                Id = Guid.NewGuid(),
                StudentId = student.Id,
                CardNumber = card,
                IsActive = true,
                CreatedAt = now
            });
            db.MealEntitlements.Add(new MealEntitlement
            {
                Id = Guid.NewGuid(),
                StudentId = student.Id,
                MealTypeId = meal.Id,
                EntitlementDate = new DateOnly(2026, 9, 7),
                Quantity = 1,
                ConsumedQuantity = 0,
                Status = "Active",
                CreatedAt = now
            });
        }

        Add("5012", "İsmail", "Çiğdemli", "1111", sinif.Id);
        Add("5013", "Ayşe", "Öztürk", "2222", sinif.Id);
        Add("5014", "Mehmet", "Yılmaz", "3333", null);
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<string>> SearchAsync(string? term)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        await SeedAsync(db);

        var repository = new EfMealEntitlementRepository(db);
        var page = await repository.SearchAsync(new MealEntitlementQuery(Search: term), default);
        return [.. page.Items.Select(x => x.StudentNo).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task AdIleAranir() => Assert.Equal(["5013"], await SearchAsync("Ayşe"));

    [Fact]
    public async Task SoyadIleAranir() => Assert.Equal(["5014"], await SearchAsync("Yılmaz"));

    /// <summary>
    /// Turkce harf farki eslesmeyi BOZMAMALIDIR: kullanici "ismail" yazdiginda
    /// "İsmail" bulunmali.
    /// </summary>
    [Theory]
    [InlineData("ismail")]
    [InlineData("İSMAİL")]
    [InlineData("cigdemli")]
    [InlineData("ÇİĞDEMLİ")]
    public async Task TurkceHarfFarkiEslesmeyiBozmaz(string term) =>
        Assert.Equal(["5012"], await SearchAsync(term));

    [Fact]
    public async Task OgrenciNumarasiIleAranir() => Assert.Equal(["5013"], await SearchAsync("5013"));

    /// <summary>
    /// Kart numarasi da AYNI kutudan aranabilmelidir: kullanicinin elindeki kartta
    /// yazan numarayi hangi kutuya yazacagini dusunmesi gerekmemeli.
    /// </summary>
    [Fact]
    public async Task KartNumarasiIleAranir() => Assert.Equal(["5014"], await SearchAsync("3333"));

    [Fact]
    public async Task SinifAdiIleAranir() => Assert.Equal(["5012", "5013"], await SearchAsync("8A"));

    /// <summary>Bos arama HICBIR filtre uygulamaz; tum kayitlar doner.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BosAramaFiltrelemez(string? term) =>
        Assert.Equal(["5012", "5013", "5014"], await SearchAsync(term));

    [Fact]
    public async Task EslesmeyenAramaBosDoner() => Assert.Empty(await SearchAsync("boyle-biri-yok"));

    /// <summary>Arama PARCA eslesmelidir: kullanici tam adi yazmak zorunda kalmamali.</summary>
    [Fact]
    public async Task ParcaEslesmeCalisir() => Assert.Equal(["5013"], await SearchAsync("Öztü"));
}
