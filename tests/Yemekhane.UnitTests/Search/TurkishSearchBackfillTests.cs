using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yemekhane.Application.Common;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Search;

/// <summary>
/// Normalleştirilmiş sütun eklenmeden ÖNCE var olan kayıtların da aramada bulunması gerekir.
/// Migration'daki backfill bunu sağlar; çalışmazsa mevcut tüm öğrenciler aramada kaybolur.
/// </summary>
public sealed class TurkishSearchBackfillTests
{

    /// <summary>
    /// ASIL FAYDA: personel Türkçe karakter YAZMADAN da öğrenciyi bulabilmelidir.
    ///
    /// Ölçüldü: 423 öğrencinin 288'i (%68) bu düzeltme öncesinde ASCII yazımla
    /// bulunamıyordu. Okul personeli hızlı veri girerken Türkçe karakter kullanmaz;
    /// "simsek" yazıp sonuç alamamak, kaydın var olmadığı izlenimi verir.
    /// </summary>
    [Theory]
    [InlineData("Şevval", "Şimşek", "SEVVAL SIMSEK")]
    [InlineData("Hüseyin", "Çetin", "HUSEYIN CETIN")]
    [InlineData("Öznur", "Güngör", "OZNUR GUNGOR")]
    [InlineData("Ali", "Koç", "ALI KOC")]
    // i/ı birleştirmesi KORUNUR: eski davranış bozulmadı.
    [InlineData("Irmak", "Yılmaz", "IRMAK YILMAZ")]
    [InlineData("İsmail", "Işık", "ISMAIL ISIK")]
    public void AsciiYazimTurkceAdiBulur(string first, string last, string expected) =>
        Assert.Equal(expected, TurkishSearchText.NormalizeFullName(first, last));

    [Theory]
    [InlineData("ışıl", "Şahin")]
    [InlineData("İsmail", "Yılmaz")]
    [InlineData("Irmak", "Öztürk")]
    [InlineData("Çağrı", "Güneş")]
    public async Task ExistingRowsAreBackfilledToMatchDotnetNormalizer(string first, string last)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;

        // Şemayı normalleştirme migration'ından bir önceki sürüme getir.
        await using (var before = new YemekhaneDbContext(options))
            await before.GetService<IMigrator>().MigrateAsync("Task054PerformanceIndexes");

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "INSERT INTO students (Id, student_no, FirstName, LastName, IsActive, IsDeleted, CreatedAt, RegisteredOn) " +
                               "VALUES ($id, '9100', $first, $last, 1, 0, '2026-09-14T12:00:00+03:00', '2026-09-14')";
            seed.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            seed.Parameters.AddWithValue("$first", first);
            seed.Parameters.AddWithValue("$last", last);
            await seed.ExecuteNonQueryAsync();
        }

        // Normalleştirme migration'ı: backfill burada çalışır.
        await using (var after = new YemekhaneDbContext(options))
            await after.Database.MigrateAsync();

        await using var verification = new YemekhaneDbContext(options);
        var stored = await verification.Students.AsNoTracking().SingleAsync(x => x.StudentNo == "9100");

        Assert.Equal(TurkishSearchText.NormalizeFullName(first, last), stored.SearchName);
    }
}
