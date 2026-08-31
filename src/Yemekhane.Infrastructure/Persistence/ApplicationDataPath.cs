namespace Yemekhane.Infrastructure.Persistence;

/// <summary>
/// Uygulama veri klasorunun tek kaynagi.
///
/// Urun adi OkulYemek'ten YemekhanePro'ya degistiginde veri klasoru de degisti. Mevcut
/// kurulumlarda veritabani hala eski klasorde duruyor; hicbir sey yapilmazsa kullanici
/// uygulamayi acinca BOMBOS bir sistem gorur ve tum verisini kaybettigini sanir.
///
/// Bu yuzden yeni klasor yoksa ve eski klasor varsa, icerik bir kez tasinir.
/// </summary>
public static class ApplicationDataPath
{
    public const string FolderName = "YemekhanePro";
    public const string LegacyFolderName = "OkulYemek";

    /// <summary>Uygulamanin yerel veri klasoru; gerekirse eski klasorden bir kez tasinir.</summary>
    public static string Resolve() =>
        Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Test edilebilmesi icin kok dizin disaridan verilebilir.</summary>
    public static string Resolve(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var current = Path.Combine(root, FolderName);
        var legacy = Path.Combine(root, LegacyFolderName);
        // Yeni klasor zaten varsa kullanici yeni surumde calisiyordur: eski klasordeki
        // bayat veritabaninin uzerine yazmak guncel verinin kaybi olurdu.
        if (!Directory.Exists(current) && Directory.Exists(legacy))
            MigrateFromLegacy(legacy, current);
        Directory.CreateDirectory(current);
        return current;
    }

    private static void MigrateFromLegacy(string legacy, string current)
    {
        // Once gecici bir klasore kopyalanip sonra yerine tasinir: yarida kesilen bir kopyalama,
        // yeni klasoru "var ama eksik" halde birakip sonraki acilista tasimanin atlanmasina yol
        // acardi. Boylece yeni klasor ya tam olusur ya hic olusmaz.
        //
        // Kopyalanir, tasinmaz: eski klasor yerinde birakilir ki tasima yarida kesilse bile
        // (elektrik kesintisi, surec sonlandirma) kullanicinin verisi hala eski yerinde dursun.
        var staging = current + ".migrating-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            CopyDirectory(legacy, staging);
            Directory.Move(staging, current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Tasima basarisiz olduysa uygulama yine de acilmalidir; eski veri yerinde
            // durdugu icin kalici kayip yoktur.
            TryDelete(staging);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Kilit dosyalari surece aittir, veriye degil; tasinirsa yeni kurulumda
            // sahibi olmayan bir migration kilidi kalir.
            var name = Path.GetFileName(file);
            if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, name), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
