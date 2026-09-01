using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Gercek DPAPI ile diske yazip okur. Sahte bir depo bu testleri gecirir ama sahada
/// bozuk dosya uygulamayi acilista cokertirdi.
/// </summary>
public sealed class LicenseStoreTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "yemekhane-lisans-" + Guid.NewGuid().ToString("N"));

    public LicenseStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static StoredLicense Sample() => new(
        "ANAHTAR-1", "Test Okulu", "Standart",
        [FingerprintHasher.Hash("A"), FingerprintHasher.Hash("B"), FingerprintHasher.Hash("C")],
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        "IMZA");

    [Fact]
    public void ASavedLicenseComesBackIdentical()
    {
        var store = new WindowsLicenseStore(directory);
        var original = Sample();

        store.Save(original);

        Assert.Equal(original, store.Load());
    }

    [Fact]
    public void WithNoFileNothingIsReturnedRatherThanThrowing()
    {
        Assert.Null(new WindowsLicenseStore(directory).Load());
    }

    [Fact]
    public void TheLicenseIsNotReadableAsPlainTextOnDisk()
    {
        // Dosya duz metin olsaydi kullanici bitis tarihini bir metin duzenleyiciyle
        // degistirebilirdi.
        var store = new WindowsLicenseStore(directory);
        store.Save(Sample());

        var raw = File.ReadAllBytes(Path.Combine(directory, "license.dat"));

        Assert.DoesNotContain("ANAHTAR-1", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("Test Okulu", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptFileDoesNotCrashTheApplicationAtStartup()
    {
        // Yarim yazilmis veya baska bir makinede sifrelenmis bir dosya uygulamayi
        // ACILISTA COKERTMEMELIDIR: kullanici sorunu cozebilecegi aktivasyon ekranina
        // dusmelidir. Cokme, kullanicinin yapabilecegi hicbir sey birakmaz.
        File.WriteAllBytes(Path.Combine(directory, "license.dat"), [1, 2, 3, 4, 5]);

        Assert.Null(new WindowsLicenseStore(directory).Load());
    }

    [Fact]
    public void AnEmptyFileIsTreatedAsNoLicense()
    {
        File.WriteAllBytes(Path.Combine(directory, "license.dat"), []);

        Assert.Null(new WindowsLicenseStore(directory).Load());
    }

    [Fact]
    public void ClearingRemovesTheLicense()
    {
        var store = new WindowsLicenseStore(directory);
        store.Save(Sample());

        store.Clear();

        Assert.Null(store.Load());
    }

    [Fact]
    public void SavingTwiceOverwritesRatherThanLeavingTheOldLicense()
    {
        var store = new WindowsLicenseStore(directory);
        store.Save(Sample());

        var renewed = Sample() with { CustomerName = "Yeni Okul" };
        store.Save(renewed);

        Assert.Equal("Yeni Okul", store.Load()!.CustomerName);
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehindAfterSaving()
    {
        // Gecici dosya kalirsa bir sonraki yazma sirasinda karisiklik yaratir.
        var store = new WindowsLicenseStore(directory);
        store.Save(Sample());

        Assert.False(File.Exists(Path.Combine(directory, "license.dat.tmp")));
    }
}
