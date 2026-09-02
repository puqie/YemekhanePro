using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Reports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// Sicil Listesi raporu (eski programin "Raporlar > Sicil Listesi"): her satir bir ogrencidir.
/// Sorgu gercek SQLite uzerinde kosar; projeksiyondaki alt sorgular (sinif, kart, veli) ve
/// yetkiye bagli TC sutunu ancak boyle kanitlanir.
/// </summary>
public sealed class StudentListReportTests
{
    [Fact]
    public async Task ListsEveryLivingStudentOrderedByClassSectionAndNumber()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery());

        // Silinmis 5004 yok; sira sinif > sube > numara.
        Assert.Equal(["5003", "5002", "5001"], result.Items.Select(x => x.StudentNo));
        Assert.Equal(3, result.Summary.TotalRecords);
        // TotalMeals aktif ogrenci sayisini tasir (ekran/PDF ozetinde "Aktif / Pasif").
        Assert.Equal(2, result.Summary.TotalMeals);
        Assert.Equal(0, result.Summary.Passed);

        var ada = result.Items.Single(x => x.StudentNo == "5001");
        Assert.Equal("6A", ada.Class); Assert.Equal("B", ada.Section);
        Assert.Equal("8350001", ada.CardNo);
        Assert.Equal("YILMAZ VELİSİ", ada.ParentName); Assert.Equal("05321234567", ada.ParentPhone);
        Assert.Equal("ACTIVE", ada.Status); Assert.Equal(new DateOnly(2025, 9, 15), ada.ReportDate);

        var demir = result.Items.Single(x => x.StudentNo == "5003");
        // Yalnizca AKTIF kart: eski (pasif) 8350000 karti listelenmez.
        Assert.Equal("8350003", demir.CardNo);
        Assert.Equal("INACTIVE", demir.Status);
        Assert.Null(demir.ParentName);
        // Kartsiz ve velisiz ogrenci bos gelir, satir dusmez.
        var kaya = result.Items.Single(x => x.StudentNo == "5002");
        Assert.Null(kaya.CardNo); Assert.Null(kaya.ParentPhone);
    }

    [Fact]
    public async Task NationalIdIsReturnedOnlyWhenSensitiveReadIsGranted()
    {
        await using var fixture = await Fixture.CreateAsync();

        var masked = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery());
        var granted = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(IncludeSensitive: true));

        Assert.All(masked.Items, x => Assert.Null(x.NationalId));
        Assert.Equal("12345678901", granted.Items.Single(x => x.StudentNo == "5001").NationalId);
        Assert.Null(granted.Items.Single(x => x.StudentNo == "5002").NationalId);
    }

    [Theory]
    [InlineData("class", "5A", 2)]
    [InlineData("section", "B", 2)]
    [InlineData("status", "INACTIVE", 1)]
    [InlineData("status", "ACTIVE", 2)]
    [InlineData("studentNo", "500", 3)]
    [InlineData("lastName", "KAYA", 1)]
    [InlineData("firstName", "ADA", 2)]
    [InlineData("cardNo", "8350003", 1)]
    public async Task FiltersNarrowTheListAndTheSummaryTogether(string filter, string value, int expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var query = filter switch
        {
            "class" => new ReportQuery(Class: value),
            "section" => new ReportQuery(Section: value),
            "status" => new ReportQuery(Status: value),
            "studentNo" => new ReportQuery(StudentNo: value),
            "lastName" => new ReportQuery(LastName: value),
            "firstName" => new ReportQuery(FirstName: value),
            _ => new ReportQuery(CardNo: value)
        };

        var result = await fixture.Service.QueryAsync(ReportType.StudentList, query);

        Assert.Equal(expected, result.Items.Count);
        Assert.Equal(expected, result.Summary.TotalRecords);
    }

    /// <summary>
    /// "ACTIVE" icerik aramasi "INACTIVE"i de tutuyordu: "Aktif" filtresi pasif ogrencileri de
    /// listeliyordu. Durum kod degeridir, tam eslesmeli.
    /// </summary>
    [Fact]
    public async Task ActiveFilterDoesNotMatchInactiveStudents()
    {
        await using var fixture = await Fixture.CreateAsync();

        var active = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(Status: "ACTIVE"));

        Assert.Equal(["5002", "5001"], active.Items.Select(x => x.StudentNo));
        Assert.DoesNotContain(active.Items, x => x.Status == "INACTIVE");
    }

    /// <summary>
    /// Ekran varsayilan olarak "bugun" tarihini gonderir; kayit tarihine uygulansaydi liste her
    /// aciliste bos gelirdi. Sicil Listesi'nde tarih yok sayilir (ekran bunu yazar).
    /// </summary>
    [Fact]
    public async Task DateRangeIsIgnoredForTheStudentList()
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.FromHours(3));

        var result = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(start, start.AddDays(1)));

        Assert.Equal(3, result.Summary.TotalRecords);
    }

    [Fact]
    public async Task PagesShareOneSummaryAndSortColumnsWork()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(Page: 1, PageSize: 2));
        var second = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(Page: 2, PageSize: 2));
        var byCard = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(SortBy: "cardNo", Descending: true));
        // ReportQuery varsayilani Descending=true'dur (olay raporlarinda "en yeni once"); sicil
        // listesi bundan ETKILENMEZ, sinif > sube > no her zaman artandir.
        var defaultDescending = await fixture.Service.QueryAsync(ReportType.StudentList, new ReportQuery(Descending: true));

        Assert.Equal(["5003", "5002"], first.Items.Select(x => x.StudentNo));
        Assert.Equal(["5001"], second.Items.Select(x => x.StudentNo));
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(["5003", "5001", "5002"], byCard.Items.Select(x => x.StudentNo));
        Assert.Equal(["5003", "5002", "5001"], defaultDescending.Items.Select(x => x.StudentNo));
    }

    [Fact]
    public async Task StreamingYieldsTheSameRowsForExports()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rows = new List<ReportRow>();

        await foreach (var batch in fixture.Service.StreamBatchesAsync(ReportType.StudentList, new ReportQuery(IncludeSensitive: true), 2))
            rows.AddRange(batch);

        Assert.Equal(["5003", "5002", "5001"], rows.Select(x => x.StudentNo));
        Assert.Equal("12345678901", rows.Single(x => x.StudentNo == "5001").NationalId);
    }

    private sealed class Fixture(SqliteConnection connection, YemekhaneDbContext context) : IAsyncDisposable
    {
        public ReportService Service { get; } = new(new EfReportRepository(context));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            await SeedAsync(context);
            return new Fixture(connection, context);
        }

        private static async Task SeedAsync(YemekhaneDbContext db)
        {
            var class5 = new SchoolClass { Name = "5A" };
            var class6 = new SchoolClass { Name = "6A" };
            var sectionA = new Section { Name = "A" };
            var sectionB = new Section { Name = "B" };
            var ada = new Student
            {
                StudentNo = "5001", FirstName = "ADA", LastName = "YILMAZ", ClassId = class6.Id, SectionId = sectionB.Id,
                NationalId = "12345678901", RegisteredOn = new DateOnly(2025, 9, 15)
            };
            var ali = new Student { StudentNo = "5002", FirstName = "ALİ", LastName = "KAYA", ClassId = class5.Id, SectionId = sectionB.Id };
            var demir = new Student { StudentNo = "5003", FirstName = "ADA", LastName = "DEMİR", ClassId = class5.Id, SectionId = sectionA.Id, IsActive = false };
            var deleted = new Student { StudentNo = "5004", FirstName = "SİLİNMİŞ", LastName = "ÖĞRENCİ", ClassId = class5.Id, IsDeleted = true };
            db.AddRange(class5, class6, sectionA, sectionB, ada, ali, demir, deleted);
            db.AddRange(
                new StudentCard { StudentId = ada.Id, CardNumber = "8350001", ValidFrom = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero) },
                new StudentCard { StudentId = demir.Id, CardNumber = "8350000", ValidFrom = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero), IsActive = false, ReplacementReason = "Kayıp" },
                new StudentCard { StudentId = demir.Id, CardNumber = "8350003", ValidFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new Parent { StudentId = ada.Id, Name = "YILMAZ VELİSİ", NormalizedPhone = "05321234567", IsPrimary = true },
                new Parent { StudentId = ada.Id, Name = "İKİNCİ VELİ", NormalizedPhone = "05329999999", IsPrimary = false });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
