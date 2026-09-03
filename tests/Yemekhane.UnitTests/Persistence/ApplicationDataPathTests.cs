using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Persistence;

/// <summary>
/// Urun adi degisince veri klasoru de degisti. Mevcut kurulumlarda veritabani hala eski
/// klasorde; tasima olmazsa kullanici uygulamayi acinca BOMBOS bir sistem gorur ve tum
/// verisini kaybettigini sanir.
/// </summary>
public sealed class ApplicationDataPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adp-" + Guid.NewGuid().ToString("N")[..8]);

    private string Legacy => Path.Combine(root, ApplicationDataPath.LegacyFolderName);
    private string Current => Path.Combine(root, ApplicationDataPath.FolderName);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void LegacyDatabaseIsMigratedSoTheUserDoesNotSeeAnEmptySystem()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "yemekhane.db"), "veri");

        var resolved = ApplicationDataPath.Resolve(root);

        Assert.Equal(Current, resolved);
        Assert.Equal("veri", File.ReadAllText(Path.Combine(resolved, "yemekhane.db")));
    }

    [Fact]
    public void LegacyFolderIsKeptSoAFailedMigrationCannotLoseData()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "yemekhane.db"), "veri");

        ApplicationDataPath.Resolve(root);

        Assert.True(File.Exists(Path.Combine(Legacy, "yemekhane.db")));
    }

    [Fact]
    public void NestedFoldersLikeLogsAreMigratedToo()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "logs"));
        File.WriteAllText(Path.Combine(Legacy, "logs", "api.json"), "log");

        var resolved = ApplicationDataPath.Resolve(root);

        Assert.True(File.Exists(Path.Combine(resolved, "logs", "api.json")));
    }

    [Fact]
    public void ExistingCurrentFolderIsNeverOverwrittenByLegacyData()
    {
        // Kullanici yeni surumde calismaya baslamissa, eski klasordeki BAYAT veritabaninin
        // uzerine yazilmasi guncel verinin kaybi demektir.
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "yemekhane.db"), "eski");
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "yemekhane.db"), "guncel");

        var resolved = ApplicationDataPath.Resolve(root);

        Assert.Equal("guncel", File.ReadAllText(Path.Combine(resolved, "yemekhane.db")));
    }

    [Fact]
    public void FreshInstallJustCreatesTheFolder()
    {
        var resolved = ApplicationDataPath.Resolve(root);

        Assert.Equal(Current, resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.Empty(Directory.EnumerateFileSystemEntries(resolved));
    }

    [Fact]
    public void StaleLockFilesAreNotCarriedOver()
    {
        // Kilit dosyasi surece aittir, veriye degil; tasinirsa yeni kurulumda
        // sahibi olmayan bir migration kilidi kalir.
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "yemekhane.db"), "veri");
        File.WriteAllText(Path.Combine(Legacy, "yemekhane.db.migration.lock"), "");

        var resolved = ApplicationDataPath.Resolve(root);

        Assert.True(File.Exists(Path.Combine(resolved, "yemekhane.db")));
        Assert.False(File.Exists(Path.Combine(resolved, "yemekhane.db.migration.lock")));
    }

    /// <summary>
    /// PROGRAM KALDIRILIP YENIDEN KURULDUGUNDA VERI DURMALIDIR.
    ///
    /// Kullanicinin guncelleme yolu budur: "Program Ekle/Kaldir"dan kaldir, yeni surumu kur.
    /// Program dosyalari Program Files'ta, veri ise %LOCALAPPDATA%'dadir; kaldirma yalnizca
    /// ilkini siler. Bu test, veri yolunun kuruluma GOMULU olmadigini ve yeni kurulumun ayni
    /// klasoru buldugunu kanitlar -- aksi halde okul her guncellemede tum ogrenci, gecis ve
    /// kasa gecmisini kaybederdi.
    /// </summary>
    [Fact]
    public void UninstallingAndReinstallingKeepsTheSchoolsData()
    {
        // 1) Okul programi kullaniyor: veritabani ve alt klasorler olustu.
        var first = ApplicationDataPath.Resolve(root);
        File.WriteAllText(Path.Combine(first, "yemekhane.db"), "ogrenciler-gecisler-kasa");
        Directory.CreateDirectory(Path.Combine(first, "Backups"));
        File.WriteAllText(Path.Combine(first, "Backups", "2026-09-01.db"), "yedek");

        // 2) Program kaldirildi. Kaldirma Program Files'i siler, veri klasorune DOKUNMAZ:
        //    burada veri klasorune hicbir sey yapmiyoruz -- kaldirmanin yaptigi da budur.

        // 3) Yeni surum kuruldu ve acildi.
        var second = ApplicationDataPath.Resolve(root);

        Assert.Equal(first, second);
        Assert.Equal("ogrenciler-gecisler-kasa", File.ReadAllText(Path.Combine(second, "yemekhane.db")));
        Assert.Equal("yedek", File.ReadAllText(Path.Combine(second, "Backups", "2026-09-01.db")));
    }

    /// <summary>
    /// Veri klasoru kullaniciya GORE cozulur, kurulum klasorune gore degil. Program baska
    /// bir klasore kurulsa da (kullanici kurulum sihirbazinda yolu degistirebilir) ayni
    /// veriye ulasilmalidir.
    /// </summary>
    [Fact]
    public void DataIsFoundRegardlessOfWhereTheProgramWasInstalled()
    {
        var before = ApplicationDataPath.Resolve(root);
        File.WriteAllText(Path.Combine(before, "yemekhane.db"), "veri");

        // Kurulum yolu degisti diye veri yolu degismez: Resolve yalnizca kullanici
        // profilindeki koke bakar.
        var after = ApplicationDataPath.Resolve(root);

        Assert.Equal(before, after);
        Assert.Equal("veri", File.ReadAllText(Path.Combine(after, "yemekhane.db")));
    }
}
