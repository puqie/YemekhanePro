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
}
