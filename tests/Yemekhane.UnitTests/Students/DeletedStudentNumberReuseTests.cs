using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// SILINMIS bir ogrencinin numarasi yeniden kullanildiginda sistem ANLASILIR bir
/// mesaj vermelidir; 500 hatasi ve "Sunucuya ulasilamadi" DEGIL.
///
/// <para>
/// Ogrenci "Sil" ile soft-delete edilir (<c>IsDeleted = true</c>) ve global sorgu
/// filtresi (<c>HasQueryFilter(x =&gt; !x.IsDeleted)</c>) onu her sorgudan gizler. Ancak
/// <c>student_no</c> uzerindeki BENZERSIZ INDEKS silinmis satiri hala gorur.
/// </para>
/// <para>
/// Sonuc: numara kontrolu "bu numara bos" der (filtre gizledigi icin), kayit denenir,
/// veritabani UNIQUE ihlali firlatir, hicbir yerde yakalanmadigi icin 500 doner ve
/// masaustu bunu "Sunucuya ulasilamadi." diye gosterir. Sekreter ag/API arar, sorun
/// veritabanindadir ve hicbir ipucu yoktur.
/// </para>
/// </summary>
public sealed class DeletedStudentNumberReuseTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public DeletedStudentNumberReuseTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// Numara kontrolu silinmis kayitlari da GORMELIDIR; aksi halde guzel Turkce
    /// catisma mesaji atlanir ve is veritabani hatasina kalir.
    /// </summary>
    [Fact]
    public async Task SilinmisOgrencininNumarasiKullanimdaSayilir()
    {
        var student = new Student { StudentNo = "1042", FirstName = "Eski", LastName = "Ogrenci" };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        var repository = new EfStudentRepository(db);
        await repository.SoftDeleteAsync(student.Id, default);

        var exists = await repository.StudentNoExistsAsync("1042", null, default);

        Assert.True(exists, "Silinmis ogrencinin numarasi hala benzersiz indekste; 'kullanimda' sayilmalidir.");
    }

    /// <summary>
    /// Ayni numarayla yeni ogrenci eklendiginde kullanici ANLASILIR bir catisma hatasi
    /// almalidir -- ham DbUpdateException degil.
    /// </summary>
    [Fact]
    public async Task SilinmisNumarayiTekrarEklemekAnlasilirHataVerir()
    {
        var student = new Student { StudentNo = "1043", FirstName = "Eski", LastName = "Ogrenci" };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        var repository = new EfStudentRepository(db);
        await repository.SoftDeleteAsync(student.Id, default);
        var service = new StudentService(repository);

        var error = await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.CreateAsync(new SaveStudentRequest(StudentNo: "1043", FirstName: "Yeni", LastName: "Ogrenci"), default));

        Assert.Contains("1043", error.Message, StringComparison.Ordinal);
    }
}
