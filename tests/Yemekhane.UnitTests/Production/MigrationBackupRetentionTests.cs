using Yemekhane.Api.Infrastructure;

namespace Yemekhane.UnitTests.Production;

/// <summary>
/// Gocmeden once alinan veritabani yedeklerinin BIRIKMEMESI gerekir.
///
/// Her surum yukseltmesinde bir kopya daha aliniyor ve hicbiri silinmiyordu.
/// Olcum sirasinda tek makinede 4 kopya birikmisti; 100 bin ogrencilik bir
/// veritabani ~1 GB oldugundan bu, diski dolduran bir zaman bombasidir.
///
/// Kural: en yeni birkac yedek TUTULUR (geri donus icin), eskiler silinir.
/// Hicbirini silmemek de, hepsini silmek de yanlistir --
/// bkz. [[okulyemek-backup-retention-dataloss]].
/// </summary>
public sealed class MigrationBackupRetentionTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "yemekhane-goc-" + Guid.NewGuid().ToString("N"));

    public MigrationBackupRetentionTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    private string CreateBackup(string stamp, string content = "veri")
    {
        var path = Path.Combine(directory, $"pre-migration-{stamp}.db");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void OldMigrationBackupsArePrunedSoTheDiskDoesNotFillUp()
    {
        // Sekiz surum yukseltmesi yasanmis bir makine.
        var created = new List<string>();
        for (var i = 1; i <= 8; i++)
            created.Add(CreateBackup($"2026010{i}120000"));

        ProductionConfiguration.PruneMigrationBackups(directory);

        var remaining = Directory.GetFiles(directory, "pre-migration-*.db");
        Assert.True(remaining.Length < created.Count,
            $"Hiçbir eski yedek silinmedi ({remaining.Length} dosya duruyor).");
    }

    [Fact]
    public void TheMostRecentBackupsAreKeptSoARollbackIsStillPossible()
    {
        // Goc bozulursa en yeni yedege donmek gerekir; hepsini silmek
        // veri kaybi riskidir.
        for (var i = 1; i <= 8; i++)
            CreateBackup($"2026010{i}120000");
        var newest = Path.Combine(directory, "pre-migration-20260108120000.db");

        ProductionConfiguration.PruneMigrationBackups(directory);

        Assert.True(File.Exists(newest),
            "En yeni yedek silindi; göç bozulursa geri dönüş yolu kalmaz.");
        Assert.NotEmpty(Directory.GetFiles(directory, "pre-migration-*.db"));
    }

    [Fact]
    public void PruningNeverTouchesTheLiveDatabaseOrOtherFiles()
    {
        var live = Path.Combine(directory, "yemekhane.db");
        var wal = Path.Combine(directory, "yemekhane.db-wal");
        var unrelated = Path.Combine(directory, "ayarlar.json");
        File.WriteAllText(live, "canli veritabani");
        File.WriteAllText(wal, "wal");
        File.WriteAllText(unrelated, "{}");
        for (var i = 1; i <= 8; i++) CreateBackup($"2026010{i}120000");

        ProductionConfiguration.PruneMigrationBackups(directory);

        Assert.True(File.Exists(live), "CANLI VERİTABANI SİLİNDİ.");
        Assert.True(File.Exists(wal), "WAL dosyası silindi.");
        Assert.True(File.Exists(unrelated), "İlgisiz dosya silindi.");
        Assert.Equal("canli veritabani", File.ReadAllText(live));
    }

    [Fact]
    public void PruningAnEmptyOrMissingDirectoryDoesNotThrow()
    {
        // Ilk kurulumda henuz yedek yoktur; burada patlamak API'yi hic baslatmaz.
        ProductionConfiguration.PruneMigrationBackups(directory);

        var missing = Path.Combine(directory, "hic-olmayan");
        ProductionConfiguration.PruneMigrationBackups(missing);
    }
}
