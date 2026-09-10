using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.StudentImports;
using Yemekhane.Infrastructure.StudentImports;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.StudentImports;

/// <summary>
/// SILINMIS bir ogrencinin numarasi dosyada geri geldiginde aktarim CALISMALI ve kayit
/// yeniden canlandirilmalidir.
///
/// <para>
/// Onizleme adimi <c>IgnoreQueryFilters()</c> kullanip silinmis ogrenciyi GORUYOR ve
/// satiri "Update" diye isaretliyordu; uygulama dongusu ise filtreli sorgu kullandigi
/// icin ogrenciyi BULAMIYOR ve yeni kayit acmaya calisiyordu. <c>student_no</c>
/// benzersiz indeksi silinmis satiri hala gordugu icin UNIQUE ihlali olusuyor,
/// transaction geri aliniyor ve dosyadaki TUM GECERLI SATIRLAR da kayboluyordu --
/// kullaniciya yalnizca "Sunucuya ulasilamadi." yaziyordu.
/// </para>
/// <para>
/// Onizleme ile uygulamanin ayni veriyi gormesi sart: aksi halde kullaniciya gosterilen
/// onizleme, gerceklesecek islemi TEMSIL ETMIYOR demektir.
/// </para>
/// </summary>
public sealed class DeletedStudentImportRevivalTests
{
    private static readonly Guid ActorId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task SilinmisOgrencininNumarasiAktarimlaGeriGelir()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new YemekhaneDbContext(
            new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var service = new StudentImportService(db, new StudentImportPreviewStore(TimeProvider.System), TimeProvider.System);

        var first = await PreviewAsync(service, "NO;KART NO;AD;SOYAD\n70;C70;Ayrilan;Ogrenci");
        await service.ApplyAsync(new(first.Token), ActorId);
        var student = db.Students.Single(x => x.StudentNo == "70");
        student.IsDeleted = true;
        student.IsActive = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Ogrenci geri dondu; ayni numarayla ve YANINDA gecerli baska bir satirla aktariliyor.
        var again = await PreviewAsync(service, "NO;KART NO;AD;SOYAD\n70;C70;Ayrilan;Ogrenci\n71;C71;Yeni;Ogrenci");
        var result = await service.ApplyAsync(new(again.Token), ActorId);

        Assert.Equal(0, result.ErrorCount);
        var revived = db.Students.IgnoreQueryFilters().Single(x => x.StudentNo == "70");
        Assert.False(revived.IsDeleted);
        Assert.True(revived.IsActive);
        // Kopya kayit ACILMAMALI ve yanindaki gecerli satir da kaybolmamali.
        Assert.Single(db.Students.IgnoreQueryFilters().Where(x => x.StudentNo == "70"));
        Assert.Single(db.Students.Where(x => x.StudentNo == "71"));
    }


    /// <summary>
    /// SINIF sutunu OLMAYAN dosya mevcut sinif atamalarini SILMEMELIDIR.
    ///
    /// <para>
    /// <c>student.ClassId = row.ClassId</c> KOSULSUZ calisiyordu. Bolum ve Gorev icin
    /// "yalnizca dosyada doluysa yaz" korumasi vardi (yorumu: "eski dosya bicimleri bu
    /// sutunlari tasimaz, bos sutun yuzunden mevcut atama silinmemeli") ama SINIF icin
    /// ayni koruma YOKTU.
    /// </para>
    /// <para>
    /// Sonuc: okul yalnizca kart numaralarini guncellemek icin sade bir
    /// "NO;KART NO;AD;SOYAD" dosyasi hazirlayip uyguladiginda TUM ogrencilerin sinifi
    /// NULL'a dusuyordu. Sonrasinda sinifsiz ogrenciler anasinifi sayimlarindan sessizce
    /// dusuyor ve mutfaga yanlis sayi gidiyordu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SinifSutunsuzDosyaMevcutSinifiSilmez()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new YemekhaneDbContext(
            new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var schoolClass = new Yemekhane.Domain.Entities.SchoolClass { Name = "5A" };
        db.Add(schoolClass);
        await db.SaveChangesAsync();
        var service = new StudentImportService(db, new StudentImportPreviewStore(TimeProvider.System), TimeProvider.System);

        var withClass = await PreviewAsync(service, "NO;KART NO;AD;SOYAD;SINIF\n80;C80;Sinifli;Ogrenci;5A");
        await service.ApplyAsync(new(withClass.Token), ActorId);
        db.ChangeTracker.Clear();
        Assert.Equal(schoolClass.Id, db.Students.Single(x => x.StudentNo == "80").ClassId);

        // Yalnizca kart numarasini guncelleyen sade dosya: SINIF sutunu YOK.
        var withoutClass = await PreviewAsync(service, "NO;KART NO;AD;SOYAD\n80;C81;Sinifli;Ogrenci");
        await service.ApplyAsync(new(withoutClass.Token), ActorId);

        db.ChangeTracker.Clear();
        // Sinif atamasi KORUNMALI: dosyada olmayan bir sutun mevcut veriyi silmemeli.
        Assert.Equal(schoolClass.Id, db.Students.Single(x => x.StudentNo == "80").ClassId);
    }

    private static Task<ImportPreviewResult> PreviewAsync(StudentImportService service, string csv) =>
        service.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "students.csv", ActorId);
}
