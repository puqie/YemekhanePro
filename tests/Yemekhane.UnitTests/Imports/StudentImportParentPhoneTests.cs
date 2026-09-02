using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.StudentImports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.StudentImports;

namespace Yemekhane.UnitTests.StudentImports;

/// <summary>
/// TELEFON sutunu: once dogrulanip sessizce atiliyordu (veli kaydi acilmiyordu) ve
/// onizleme sinif/telefon gostermiyordu. Operator dosyaya telefon yaziyor, SMS ekrani
/// "telefon yok" diyordu.
/// </summary>
public sealed class StudentImportParentPhoneTests
{
    private static readonly Guid ActorId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task OnizlemeSinifVeTelefonuNormallestirilmisGosterir()
    {
        await using var fixture = await Fixture.CreateAsync();

        var preview = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD;SINIF;TELEFON\r\n9101;K-9101;Ada;Akgün;5A;0532 111 22 33\r\n9102;K-9102;Ali;Aslan;;\r\n");

        Assert.Equal("5A", preview.Rows[0].ClassName);
        Assert.Equal("+905321112233", preview.Rows[0].Phone);
        Assert.Null(preview.Rows[1].ClassName);
        Assert.Null(preview.Rows[1].Phone);
    }

    [Fact]
    public async Task UygulamaTelefonuVeliKaydiOlarakAcarVeTekrarindaCogaltmaz()
    {
        await using var fixture = await Fixture.CreateAsync();
        var csv = "NO;KART NO;AD;SOYAD;TELEFON\r\n9201;K-9201;Ada;Akgün;05321112233\r\n9202;K-9202;Ali;Aslan;\r\n";

        var preview = await fixture.PreviewCsv(csv);
        await fixture.Service.ApplyAsync(new ApplyStudentImportRequest(preview.Token), ActorId);

        var ada = await fixture.Db.Students.SingleAsync(x => x.StudentNo == "9201");
        var parent = await fixture.Db.Parents.SingleAsync(x => x.StudentId == ada.Id);
        Assert.Equal("+905321112233", parent.NormalizedPhone);
        Assert.True(parent.IsPrimary);
        Assert.True(parent.IsActive);
        var ali = await fixture.Db.Students.SingleAsync(x => x.StudentNo == "9202");
        Assert.False(await fixture.Db.Parents.AnyAsync(x => x.StudentId == ali.Id));

        // Ayni dosya ikinci kez: veli cogalmamali.
        preview = await fixture.PreviewCsv(csv);
        await fixture.Service.ApplyAsync(new ApplyStudentImportRequest(preview.Token), ActorId);
        Assert.Equal(1, await fixture.Db.Parents.CountAsync(x => x.StudentId == ada.Id));
    }

    [Fact]
    public async Task MevcutVeliVarkenFarkliTelefonIkincilVeliOlarakEklenir()
    {
        await using var fixture = await Fixture.CreateAsync();
        var student = new Student { StudentNo = "9301", FirstName = "Eski", LastName = "Ad", IsActive = true };
        fixture.Db.Students.Add(student);
        fixture.Db.Parents.Add(new Parent { StudentId = student.Id, Name = "Anne", NormalizedPhone = "+905321110000", IsPrimary = true });
        await fixture.Db.SaveChangesAsync();

        var preview = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD;TELEFON\r\n9301;K-9301;Yeni;Ad;05329998877\r\n");
        await fixture.Service.ApplyAsync(new ApplyStudentImportRequest(preview.Token), ActorId);

        var parents = await fixture.Db.Parents.Where(x => x.StudentId == student.Id).OrderBy(x => x.NormalizedPhone).ToListAsync();
        Assert.Equal(2, parents.Count);
        Assert.True(parents.Single(x => x.NormalizedPhone == "+905321110000").IsPrimary);
        var added = parents.Single(x => x.NormalizedPhone == "+905329998877");
        Assert.False(added.IsPrimary);
        Assert.Equal("Veli", added.Name);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private Fixture(SqliteConnection connection, YemekhaneDbContext db)
        {
            this.connection = connection; Db = db;
            Service = new StudentImportService(db, new StudentImportPreviewStore(TimeProvider.System), TimeProvider.System);
        }
        public YemekhaneDbContext Db { get; }
        public StudentImportService Service { get; }

        public async Task<ImportPreviewResult> PreviewCsv(string csv)
        {
            using var stream = new MemoryStream(new UTF8Encoding(true).GetBytes(csv));
            return await Service.PreviewAsync(stream, "sicil.csv", ActorId);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Set<SchoolClass>().Add(new SchoolClass { Name = "5A" });
            await db.SaveChangesAsync();
            return new(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
