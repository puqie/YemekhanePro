using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Sayfalama HICBIR ogrenciyi atlamamali ve HICBIRINI tekrarlamamalidir.
///
/// <para>
/// Siralama yalnizca <c>OrderBy(StudentNo)</c> idi ve ikincil anahtari yoktu. Ogrenci
/// numarasi artik istege bagli oldugundan (anasinifi, misafir ogrenci) bircok satir ayni
/// BOS numarayi tasiyor; esit siralama anahtarli satirlarin SQLite'taki sirasi
/// LIMIT/OFFSET sorgulari ARASINDA garanti degildir. Sonuc: sayfa 1 ile sayfa 2 sinirinda
/// bazi ogrenciler hic gorunmuyor, bazilari iki kez cikiyordu -- "listede olmayan
/// ogrenciler var" sikayetinin kaynagi buydu. Toplam sayi dogru gorundugu icin hata
/// ekranda kendini ele vermiyordu.
/// </para>
/// <para>
/// Ayni desen rapor tarafinda dogru yazilmis (EfReportRepository: <c>ThenBy(x =&gt; x.Id)</c>);
/// bu testler ayni kurali ogrenci listesine baglar.
/// </para>
/// </summary>
public sealed class StudentPagingStabilityTests
{
    /// <summary>
    /// Ogrenci listesi sorgusunun URETTIGI SQL deterministik bir ikincil siralama
    /// anahtariyla BITMELIDIR.
    ///
    /// <para>
    /// Bu test davranisi degil, DEGISMEZIN KENDISINI olcer -- ve bunun sebebi var:
    /// bellek ici SQLite kucuk veri kumesinde satirlari tesadufen rowid sirasinda
    /// dondurur. "Sayfalar cakismasin" seklinde yazilan bir davranis testi, siralama
    /// TAMAMEN kaldirilsa bile yesil geciyordu (mutasyonla dogrulandi: test hicbir sey
    /// korumuyordu). Uretimde binlerce satirda sorgu plani, index secimi ve OFFSET
    /// degistikce sira kayar. Tek guvenilir kanit ORDER BY'in tekil bir sutunla
    /// bitmesidir.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OgrenciListesiSiralamasiTekilAnahtarlaBiter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new YemekhaneDbContext(
            new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();

        var sql = EfStudentRepository.OrderForPaging(context.Students).ToQueryString();

        var orderBy = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Contains("\"Id\"", orderBy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Numarasiz ogrenciler tek bir sayfaya sigmadiginda, butun sayfalarin birlesimi
    /// her ogrenciyi TAM BIR KEZ icermelidir. Yukaridaki degismez saglandiginda bu
    /// dogal olarak tutar; burada uctan uca dogrulanir.
    /// </summary>
    [Fact]
    public async Task NumarasizOgrencilerdeSayfalarBirbirininAyniniVermez()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new YemekhaneDbContext(
            new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();

        // Hepsi numarasiz: siralama anahtari tamamen esit, en zorlu durum.
        for (var index = 0; index < 40; index++)
            context.Students.Add(new Student { StudentNo = "", FirstName = $"Ogrenci{index:D2}", LastName = "Anasinifi" });
        await context.SaveChangesAsync();
        var repository = new EfStudentRepository(context);

        var first = await repository.SearchAsync(new StudentQuery(Page: 1, PageSize: 10), default);
        var second = await repository.SearchAsync(new StudentQuery(Page: 2, PageSize: 10), default);
        var third = await repository.SearchAsync(new StudentQuery(Page: 3, PageSize: 10), default);
        var fourth = await repository.SearchAsync(new StudentQuery(Page: 4, PageSize: 10), default);

        var seen = first.Items.Concat(second.Items).Concat(third.Items).Concat(fourth.Items)
            .Select(x => x.Id).ToList();
        Assert.Equal(40, first.TotalCount);
        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
    }
}
