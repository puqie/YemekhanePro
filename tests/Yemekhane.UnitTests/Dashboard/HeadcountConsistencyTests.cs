using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Dashboard;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Dashboard;

/// <summary>
/// MUTFAGA VERILEN SAYI her ekranda AYNI olmalidir.
///
/// <para>
/// Okul sahibi soyle demisti: "yemekhaneye kisi sayisi verdigim zaman ilkokulun gunluk
/// aktif kisisini goruyorum... anasinifinin kisi sayisi onemli degil." Bunun uzerine
/// TAKVIM ekranina sinif turu suzgeci eklendi (varsayilan: anasinifi haric).
/// </para>
/// <para>
/// Ama PANEL suzgeci uygulamiyordu: ayni gun icin iki ekran FARKLI sayi gosteriyordu.
/// Kullanici hangisine bakarsa mutfaga o sayiyi veriyor -- yemek eksik ya da fazla
/// cikiyordu. Panel artik ayni suzgeci ve ayni varsayilani kullanir.
/// </para>
/// </summary>
public sealed class HeadcountConsistencyTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Today = new(2026, 10, 1);
    private Guid mealId;

    public HeadcountConsistencyTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var meal = new MealType { Name = "Öğle" };
        db.Add(meal);
        db.SaveChanges();
        mealId = meal.Id;
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>Verilen sinif turunde bir ogrenci ve o gune hakki olusturur.</summary>
    private void SeedStudent(string classKind, string no)
    {
        var schoolClass = new SchoolClass { Name = "S-" + no, SearchName = "s" + no, Kind = classKind };
        var student = new Student
        {
            StudentNo = no, FirstName = "Ad" + no, LastName = "Soyad",
            SearchName = "ad" + no + " soyad", ClassId = schoolClass.Id, IsActive = true
        };
        db.AddRange(schoolClass, student);
        db.SaveChanges();
        db.Add(new MealEntitlement
        {
            StudentId = student.Id, MealTypeId = mealId, EntitlementDate = Today,
            Quantity = 1, Status = "Active"
        });
        db.SaveChanges();
    }

    private Task<Yemekhane.Application.Dashboard.DashboardSnapshot> SnapshotAsync(string? classKind)
    {
        var start = new DateTimeOffset(2026, 9, 30, 21, 0, 0, TimeSpan.Zero);
        return new EfDashboardRepository(db)
            .GetAsync(Today, start, start.AddDays(1), start, default, classKind);
    }

    /// <summary>Varsayilan (anasinifi HARIC): yalnizca ilkokul ogrencileri sayilir.</summary>
    [Fact]
    public async Task ThePreschoolStudentsAreExcludedByDefault()
    {
        SeedStudent(ClassKinds.Normal, "100");
        SeedStudent(ClassKinds.Normal, "101");
        SeedStudent(ClassKinds.Preschool, "900");

        var snapshot = await SnapshotAsync(ClassKinds.Normal);

        Assert.Equal(2, snapshot.Kpis.EntitledStudents);
        Assert.Equal(2, snapshot.Kpis.ActiveStudents);
    }

    /// <summary>"Yalnizca anasinifi" secilirse yalnizca onlar sayilir.</summary>
    [Fact]
    public async Task OnlyPreschoolCanBeCountedOnItsOwn()
    {
        SeedStudent(ClassKinds.Normal, "100");
        SeedStudent(ClassKinds.Preschool, "900");
        SeedStudent(ClassKinds.Preschool, "901");

        var snapshot = await SnapshotAsync(ClassKinds.Preschool);

        Assert.Equal(2, snapshot.Kpis.EntitledStudents);
    }

    /// <summary>Suzgec verilmezse (tumu) herkes sayilir; eski davranis korunur.</summary>
    [Fact]
    public async Task WithoutAFilterEveryoneIsCounted()
    {
        SeedStudent(ClassKinds.Normal, "100");
        SeedStudent(ClassKinds.Preschool, "900");

        var snapshot = await SnapshotAsync(null);

        Assert.Equal(2, snapshot.Kpis.EntitledStudents);
    }

    /// <summary>
    /// Sinifi GIRILMEMIS ogrenci ilkokul sayilir: anasinifi olmadigi bilinmiyorsa mutfak
    /// sayimindan dusurmek yemegi EKSIK cikarirdi.
    /// </summary>
    [Fact]
    public async Task StudentsWithoutAClassCountAsPrimary()
    {
        var student = new Student
        {
            StudentNo = "200", FirstName = "Sınıfsız", LastName = "Öğrenci",
            SearchName = "sinifsiz ogrenci", IsActive = true
        };
        db.Add(student);
        db.SaveChanges();
        db.Add(new MealEntitlement
        {
            StudentId = student.Id, MealTypeId = mealId, EntitlementDate = Today,
            Quantity = 1, Status = "Active"
        });
        db.SaveChanges();

        var snapshot = await SnapshotAsync(ClassKinds.Normal);

        Assert.Equal(1, snapshot.Kpis.EntitledStudents);
    }
}
