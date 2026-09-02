using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.StudentImports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.StudentImports;

namespace Yemekhane.UnitTests.StudentImports;

/// <summary>
/// Sicil aktarimi eski programin Sicil Karti sutunlarini da tanir: BOLUM ve GOREV.
/// Once yalnizca NO/KART NO/AD/SOYAD/TC/DOGUM TARIHI/SINIF/TELEFON okunuyordu; bolum ve
/// gorev sutunlari sessizce yok sayiliyor, operator aktardigi bilginin kaybolduğunu
/// ancak ogrenci kartini acinca goruyordu.
/// </summary>
public sealed class StudentImportLookupColumnTests
{
    private static readonly Guid ActorId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task BolumVeGorevSutunlariOgrenciyeYazilir()
    {
        await using var fixture = await Fixture.CreateAsync();
        var department = new Department { Name = "Sayısal" };
        var job = new Job { Name = "Öğrenci" };
        fixture.Db.AddRange(department, job);
        await fixture.Db.SaveChangesAsync();

        var csv = "NO;KART NO;AD;SOYAD;BOLUM;GOREV\r\n" +
                  "7200;K-7200;Ada;Yılmaz;Sayısal;Öğrenci\r\n";
        var preview = await fixture.PreviewCsv(csv);
        Assert.Empty(preview.Rows.SelectMany(x => x.Errors));

        await fixture.Service.ApplyAsync(new ApplyStudentImportRequest(preview.Token), ActorId);

        var student = await fixture.Db.Students.SingleAsync(x => x.StudentNo == "7200");
        Assert.Equal(department.Id, student.DepartmentId);
        Assert.Equal(job.Id, student.JobId);
    }

    /// <summary>Turkce buyuk/kucuk harf duyarsiz eslesme (sinif sutunuyla ayni kural).</summary>
    [Fact]
    public async Task BolumAdiBuyukKucukHarfDuyarsizEslesir()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(new Department { Name = "Sayısal" });
        await fixture.Db.SaveChangesAsync();

        var preview = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD;BOLUM\r\n7201;K-7201;Ali;Demir;SAYISAL\r\n");

        Assert.Empty(preview.Rows.SelectMany(x => x.Errors));
    }

    /// <summary>Bulunmayan bolum/gorev SESSIZCE ATLANMAZ: satir hatali isaretlenir.</summary>
    [Fact]
    public async Task BilinmeyenBolumVeGorevHataUretir()
    {
        await using var fixture = await Fixture.CreateAsync();

        var preview = await fixture.PreviewCsv(
            "NO;KART NO;AD;SOYAD;BOLUM;GOREV\r\n7202;K-7202;Ali;Demir;Yok Böyle;Yok Görev\r\n");

        var errors = preview.Rows.SelectMany(x => x.Errors).ToList();
        Assert.Contains(errors, x => x.Code == "DepartmentNotFound");
        Assert.Contains(errors, x => x.Code == "JobNotFound");
        Assert.Equal(1, preview.ErrorCount);
    }

    /// <summary>
    /// Sutun YOKSA mevcut atama korunur: eski bicimli bir dosya, ogrencinin bolumunu
    /// silmemeli (bos sutun "temizle" anlamina gelmez).
    /// </summary>
    [Fact]
    public async Task SutunYokkaMevcutAtamaKorunur()
    {
        await using var fixture = await Fixture.CreateAsync();
        var department = new Department { Name = "Sayısal" };
        fixture.Db.Add(department);
        fixture.Db.Students.Add(new Student
        {
            StudentNo = "7203", FirstName = "Ada", LastName = "Yılmaz",
            RegisteredOn = DateOnly.FromDateTime(DateTime.Today), DepartmentId = department.Id
        });
        await fixture.Db.SaveChangesAsync();

        var preview = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD\r\n7203;K-7203;Ada;Yılmaz\r\n");
        await fixture.Service.ApplyAsync(new ApplyStudentImportRequest(preview.Token), ActorId);

        var student = await fixture.Db.Students.SingleAsync(x => x.StudentNo == "7203");
        Assert.Equal(department.Id, student.DepartmentId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public YemekhaneDbContext Db { get; }
        public StudentImportService Service { get; }

        private Fixture(SqliteConnection connection, YemekhaneDbContext db)
        {
            this.connection = connection;
            Db = db;
            Service = new StudentImportService(db, new StudentImportPreviewStore(TimeProvider.System), TimeProvider.System);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public Task<ImportPreviewResult> PreviewCsv(string csv) =>
            Service.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "students.csv", ActorId);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
