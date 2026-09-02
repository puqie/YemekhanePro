using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Search;

namespace Yemekhane.UnitTests.Search;

public sealed class GlobalSearchRepositoryTests
{
    /// <summary>
    /// SQLite LIKE yalnizca ASCII'de buyuk/kucuk harf duyarsizdir; Turkce I/i/I/i ciftleri
    /// karsilastirmayi kacirirdi. Arama, kullanicinin hangi harfi yazdigina bakmaksizin bulmali.
    /// </summary>
    [Theory]
    [InlineData("IŞ")]
    [InlineData("ış")]
    [InlineData("Iş")]
    [InlineData("ıŞ")]
    public async Task StudentNameMatchesRegardlessOfTurkishCasing(string term)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(new Student { StudentNo = "9001", FirstName = "ışıl", LastName = "Yılmaz" });
        await fixture.Db.SaveChangesAsync();

        var response = await fixture.Search.SearchAsync(term, Permissions("students.read"), default);

        var group = Assert.Single(response.Groups, x => x.Type == "student");
        Assert.Contains(group.Items, item => item.Title.StartsWith("ışıl", StringComparison.Ordinal));
    }

    /// <summary>i ve ı arama icin birlestirilir: kullanici hangisini yazarsa yazsin sonuc gelir.</summary>
    [Theory]
    [InlineData("ir")]
    [InlineData("IR")]
    [InlineData("ır")]
    [InlineData("İR")]
    public async Task DottedAndDotlessIAreUnifiedForSearch(string term)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(new Student { StudentNo = "9002", FirstName = "Irmak", LastName = "Demir" });
        await fixture.Db.SaveChangesAsync();

        var response = await fixture.Search.SearchAsync(term, Permissions("students.read"), default);

        var group = Assert.Single(response.Groups, x => x.Type == "student");
        Assert.Contains(group.Items, item => item.Title.StartsWith("Irmak", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("şu")]
    [InlineData("ŞU")]
    public async Task ClassAndGroupNamesMatchRegardlessOfTurkishCasing(string term)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(new SchoolClass { Name = "Şubat Sınıfı", IsActive = true });
        fixture.Db.Add(new StudentGroup { Name = "Şubat Grubu", GroupType = "Kulüp", IsActive = true });
        await fixture.Db.SaveChangesAsync();

        var response = await fixture.Search.SearchAsync(term, Permissions("students.read"), default);

        Assert.Single(Assert.Single(response.Groups, x => x.Type == "class").Items);
        Assert.Single(Assert.Single(response.Groups, x => x.Type == "group").Items);
    }

    [Theory]
    [InlineData("ıd")]
    [InlineData("ID")]
    [InlineData("id")]
    public async Task HolidayNameMatchesRegardlessOfTurkishCasing(string term)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(new Holiday { Date = new DateOnly(2026, 3, 20), Name = "Idris Bayramı",
            HolidayType = "Resmi", TransferBehavior = "Delete" });
        await fixture.Db.SaveChangesAsync();

        var response = await fixture.Search.SearchAsync(term, Permissions("calendar.manage"), default);

        var group = Assert.Single(response.Groups, x => x.Type == "calendar");
        Assert.Contains(group.Items, item => item.Title == "Idris Bayramı");
    }

    /// <summary>
    /// Kart numarasiyla arama ogrenciyi dondurur ve alt satirda o karti GOSTERIR: memur
    /// elindeki karti okutup "bu kart kimin?" diye bakar; kartin sonucta gorunmesi
    /// dogru ogrenciyi bulduguna dair tek kanittir. Ogrenci listesi ayni students.read
    /// izniyle ayni kart numarasini zaten gosterir; burada gizlemek ek guvenlik saglamaz.
    /// </summary>
    [Fact]
    public async Task ExactStudentAndNumericCardLookupReturnStudentWithItsCard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = new Student { StudentNo = "7", FirstName = "Ada", LastName = "Yılmaz" };
        fixture.Db.AddRange(student, new StudentCard { StudentId = student.Id, CardNumber = "123456", IsActive = true });
        await fixture.Db.SaveChangesAsync();

        var byNumber = await fixture.Search.SearchAsync("7", Permissions("students.read"), default);
        var byCard = await fixture.Search.SearchAsync("123456", Permissions("students.read"), default);

        Assert.Equal(student.Id.ToString(), Assert.Single(Assert.Single(byNumber.Groups).Items).RouteParameters["id"]);
        var cardResult = Assert.Single(Assert.Single(byCard.Groups).Items);
        Assert.Contains("Kart 123456", cardResult.Subtitle);
        Assert.Equal("student", cardResult.Type);
    }

    [Fact]
    public async Task NameRequiresTwoCharactersAndResultsAreBounded()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.AddRange(Enumerable.Range(1, 12).Select(index => new Student
        { StudentNo = $"S{index}", FirstName = "Ada", LastName = $"Test{index:00}" }));
        await fixture.Db.SaveChangesAsync();

        Assert.Empty((await fixture.Search.SearchAsync("A", Permissions("students.read"), default)).Groups);
        var results = await fixture.Search.SearchAsync("Ad", Permissions("students.read"), default);
        Assert.Equal(EfGlobalSearchRepository.GroupLimit, Assert.Single(results.Groups).Items.Count);
    }

    [Fact]
    public async Task ClassGroupAndTurkishDateProduceRealRoutes()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.AddRange(new SchoolClass { Name = "11-A" }, new StudentGroup { Name = "11 Gezi", GroupType = "Manual" },
            new Holiday { Date = new DateOnly(2026, 9, 11), Name = "Okul Tatili", HolidayType = "Official", TransferBehavior = "Delete" });
        await fixture.Db.SaveChangesAsync();

        var organization = await fixture.Search.SearchAsync("11", Permissions("students.read"), default);
        Assert.Contains(organization.Groups, group => group.Type == "class");
        Assert.Contains(organization.Groups, group => group.Type == "group");
        Assert.True(TurkishDateParser.TryParse("11 Eylül", new DateOnly(2026, 8, 31), out var date));
        Assert.Equal(new DateOnly(2026, 9, 11), date);
        var calendar = await fixture.Search.SearchAsync("2026-09-11", Permissions("calendar.manage"), default);
        Assert.Contains(calendar.Groups.SelectMany(group => group.Items), item => item.Title == "Okul Tatili" && item.Route == "holiday-transfer");
    }

    [Fact]
    public async Task PermissionsHideDataAndUnavailableModules()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(new Student { StudentNo = "42", FirstName = "Gizli", LastName = "Öğrenci" });
        await fixture.Db.SaveChangesAsync();

        Assert.Empty((await fixture.Search.SearchAsync("Gizli", Permissions(), default)).Groups);
        var reportOnly = await fixture.Search.SearchAsync("Rapor", Permissions("reports.read"), default);
        Assert.Equal("reports", Assert.Single(Assert.Single(reportOnly.Groups).Items).Route);
        Assert.DoesNotContain(reportOnly.Groups.SelectMany(group => group.Items), item => item.Route == "students");
    }

    private static HashSet<string> Permissions(params string[] values) => values.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Ayni ad-soyadli ogrenciler (okulda uc ADA, dort ALI var) yalnizca numarayla
    /// ayirt edilemez: memur numaralari ezbere bilmez, sinif/subeyi ve karti bilir.
    /// Arama sonucunun alt satiri no + sinif/sube + kart tasimali.
    /// </summary>
    [Fact]
    public async Task IdenticalNamesAreDistinguishedByClassSectionAndCard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var class5A = new SchoolClass { Name = "5A", IsActive = true };
        var class7C = new SchoolClass { Name = "7C", IsActive = true };
        var sectionB = new Section { Name = "B" };
        fixture.Db.AddRange(class5A, class7C, sectionB);
        var first = new Student { StudentNo = "9101", FirstName = "Ali", LastName = "Aslan", ClassId = class5A.Id, SectionId = sectionB.Id };
        var second = new Student { StudentNo = "9102", FirstName = "Ali", LastName = "Aslan", ClassId = class7C.Id };
        var third = new Student { StudentNo = "9103", FirstName = "Ali", LastName = "Aslan" };
        fixture.Db.AddRange(first, second, third);
        fixture.Db.Add(new StudentCard { StudentId = first.Id, CardNumber = "8350101", IsActive = true });
        fixture.Db.Add(new StudentCard { StudentId = second.Id, CardNumber = "8350999", IsActive = false });
        await fixture.Db.SaveChangesAsync();

        var response = await fixture.Search.SearchAsync("ali", Permissions("students.read"), default);

        var items = Assert.Single(response.Groups, x => x.Type == "student").Items;
        var byNo = items.ToDictionary(item => item.RouteParameters["id"]);
        Assert.Equal("No 9101 • 5A / B • Kart 8350101", byNo[first.Id.ToString()].Subtitle);
        // Pasif kart gosterilmez: turnikede calismayan bir numara memuru yaniltir.
        Assert.Equal("No 9102 • 7C • Kart yok", byNo[second.Id.ToString()].Subtitle);
        Assert.Equal("No 9103 • Sınıf yok • Kart yok", byNo[third.Id.ToString()].Subtitle);
    }

    private sealed class Fixture(SqliteConnection connection, YemekhaneDbContext db) : IAsyncDisposable
    {
        public YemekhaneDbContext Db { get; } = db;
        public EfGlobalSearchRepository Search { get; } = new(db, TimeProvider.System);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(); return new(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
