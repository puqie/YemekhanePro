using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.UnitTests.Persistence;

namespace Yemekhane.UnitTests.Backup;

/// <summary>
/// Yedek BIR BILGISAYARDAN DIGERINE tasinabilmelidir; iki makinenin ayni surumde
/// olmasi beklenemez ve lisans farkli olabilir.
///
/// Kurallar asimetriktir:
/// - ESKI yedek YENI programa GIRER. Geri yukleme sonrasi MigrateAsync eksik tablo ve
///   sutunlari ekler. Onceki kural ana surum ESITLIGI ariyordu ve 1.x -> 2.x gecisinde
///   eski yedegi tamamen erisilemez kiliyordu.
/// - YENI yedek ESKI programa GIRMEZ. Eski surum yeni sutunlari tanimaz, geri gocu yoktur.
///
/// Lisans/makine kimligi geri yuklemede HIC kontrol edilmez: yedek baska bilgisayarda
/// acilabilir. Bu testler o davranisi da kilitler.
/// </summary>
[Collection(LocalDatabaseTests.CollectionName)]
public sealed class BackupPortabilityTests
{
    /// <summary>Arsivdeki manifest'i degistirip checksum'lari yeniden yazar.</summary>
    private static async Task RewriteManifestAsync(string archivePath, Action<Dictionary<string, JsonElement>> edit)
    {
        Dictionary<string, JsonElement> manifest;
        using (var read = ZipFile.OpenRead(archivePath))
        {
            await using var stream = read.GetEntry("manifest.json")!.Open();
            manifest = (await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(stream))!;
        }

        edit(manifest);

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry("manifest.json")!.Delete();
        var entry = archive.CreateEntry("manifest.json");
        await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        await writer.WriteAsync(JsonSerializer.Serialize(manifest));
    }

    private static JsonElement Text(string value) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));

    /// <summary>ESKI surumde alinmis yedek bu programa GIRMELIDIR (kok istek).</summary>
    [Fact]
    public async Task ABackupFromAnOlderMajorVersionCanBeRestored()
    {
        using var fixture = await BackupServiceTests.BackupFixture.CreateAsync();
        var studentId = await fixture.AddStudentAsync("tasima-001");
        var backup = await fixture.Service.CreateAsync();
        await fixture.DeleteStudentAsync(studentId);

        // Yedek 0.9.0'da alinmis gibi damgalanir: bu programdan ESKI.
        await RewriteManifestAsync(backup.ArchivePath, m => m["appVersion"] = Text("0.9.0"));

        var restored = await fixture.Service.RestoreAsync(backup.ArchivePath);

        Assert.True(restored.Restored);
        Assert.True(await fixture.StudentExistsAsync(studentId));
    }

    /// <summary>Ayni ana surumun ESKI yamasi da girer (1.2.0 -> 1.3.x).</summary>
    [Fact]
    public async Task ABackupFromAnEarlierPatchOfTheSameMajorCanBeRestored()
    {
        using var fixture = await BackupServiceTests.BackupFixture.CreateAsync();
        var studentId = await fixture.AddStudentAsync("tasima-002");
        var backup = await fixture.Service.CreateAsync();
        await fixture.DeleteStudentAsync(studentId);

        await RewriteManifestAsync(backup.ArchivePath, m => m["appVersion"] = Text("1.2.0"));

        var restored = await fixture.Service.RestoreAsync(backup.ArchivePath);

        Assert.True(restored.Restored);
        Assert.True(await fixture.StudentExistsAsync(studentId));
    }

    /// <summary>
    /// DAHA YENI ana surumde alinmis yedek REDDEDILIR: bu program o veriyi tanimaz.
    /// Mesaj kullaniciya NE YAPACAGINI soylemelidir.
    /// </summary>
    [Fact]
    public async Task ABackupFromANewerMajorVersionIsRejectedWithAnActionableMessage()
    {
        using var fixture = await BackupServiceTests.BackupFixture.CreateAsync();
        await fixture.AddStudentAsync("tasima-003");
        var backup = await fixture.Service.CreateAsync();

        await RewriteManifestAsync(backup.ArchivePath, m => m["appVersion"] = Text("99.0.0"));

        var error = await Assert.ThrowsAsync<BackupValidationException>(
            () => fixture.Service.RestoreAsync(backup.ArchivePath));

        Assert.Contains("güncelleyin", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reddedilen geri yukleme MEVCUT VERIYE DOKUNMAZ: yanlis yedek secen kullanici
    /// okulun verisini kaybetmemelidir.
    /// </summary>
    [Fact]
    public async Task ARejectedRestoreLeavesTheCurrentDataUntouched()
    {
        using var fixture = await BackupServiceTests.BackupFixture.CreateAsync();
        var backup = await fixture.Service.CreateAsync();
        // Yedek alindiktan SONRA eklenen ogrenci: reddedilen geri yuklemede kalmalidir.
        var afterBackup = await fixture.AddStudentAsync("tasima-004");
        await RewriteManifestAsync(backup.ArchivePath, m => m["appVersion"] = Text("99.0.0"));

        await Assert.ThrowsAsync<BackupValidationException>(
            () => fixture.Service.RestoreAsync(backup.ArchivePath));

        Assert.True(await fixture.StudentExistsAsync(afterBackup));
    }

    /// <summary>
    /// Bozulmus arsiv (checksum tutmuyor) REDDEDILIR ve veri korunur. Yedek USB ile
    /// tasinirken bozulabilir; sessizce yarim veri yuklemek en kotu sonuctur.
    /// </summary>
    [Fact]
    public async Task ATamperedArchiveIsRejectedAndDataSurvives()
    {
        using var fixture = await BackupServiceTests.BackupFixture.CreateAsync();
        var backup = await fixture.Service.CreateAsync();
        var survivor = await fixture.AddStudentAsync("tasima-005");

        // Veritabani icerigi degistirilir ama manifest'teki checksum eski kalir.
        using (var archive = ZipFile.Open(backup.ArchivePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("database.sqlite")!;
            await using var stream = entry.Open();
            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(RandomNumberGenerator.GetBytes(64));
        }

        await Assert.ThrowsAsync<BackupValidationException>(
            () => fixture.Service.RestoreAsync(backup.ArchivePath));

        Assert.True(await fixture.StudentExistsAsync(survivor));
    }

    /// <summary>
    /// Geri yukleme LISANS ya da MAKINE KIMLIGI SORMAZ: yedek baska bilgisayarda
    /// acilabilmelidir. Manifest'te boyle bir alan bulunmamalidir.
    /// </summary>
    [Fact]
    public async Task TheBackupCarriesNoMachineOrLicenseLock()
    {
        using var fixture = await BackupServiceTests.BackupFixture.CreateAsync();
        var backup = await fixture.Service.CreateAsync();

        using var archive = ZipFile.OpenRead(backup.ArchivePath);
        await using var stream = archive.GetEntry("manifest.json")!.Open();
        using var reader = new StreamReader(stream);
        var manifest = await reader.ReadToEndAsync();

        foreach (var locked in new[] { "machineId", "fingerprint", "licenseKey" })
            Assert.DoesNotContain(locked, manifest, StringComparison.OrdinalIgnoreCase);
    }
}
