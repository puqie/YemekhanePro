using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yemekhane.Licensing;

/// <summary>Lisans kaydinin yerel deposu.</summary>
public interface ILicenseStore
{
    /// <summary>Kayitli lisansi okur; yoksa veya okunamiyorsa null.</summary>
    StoredLicense? Load();

    /// <summary>Lisansi diske yazar.</summary>
    void Save(StoredLicense license);

    /// <summary>Kayitli lisansi siler.</summary>
    void Clear();
}

/// <summary>
/// Lisansi DPAPI ile sifreleyip dosyaya yazar.
///
/// Kendi entropisini kullanir. Mevcut ayar entropisi (OkulYemek.SystemSettings.v1)
/// DEGISTIRILMEZ: degisirse sahadaki sifreli ayarlar okunamaz hale gelir.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLicenseStore : ILicenseStore
{
    /// <summary>Bu depoya ozgu DPAPI entropisi.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YemekhanePro.License.v1");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string filePath;

    /// <param name="dataDirectory">
    /// Lisans dosyasinin klasoru. Disaridan verilir: bu proje Infrastructure'a referans
    /// veremez, dolayisiyla ApplicationDataPath'i kendisi cagiramaz.
    /// </param>
    public WindowsLicenseStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        filePath = Path.Combine(dataDirectory, "license.dat");
    }

    public StoredLicense? Load()
    {
        // Bozuk, yarim yazilmis veya baska bir makinede sifrelenmis bir dosya
        // uygulamayi ACILISTA COKERTMEMELIDIR. Boyle bir dosya "lisans yok" sayilir
        // ve kullanici aktivasyon ekranina duser - yani sorunu cozebilecegi yere.
        try
        {
            if (!File.Exists(filePath)) return null;

            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(filePath), Entropy, DataProtectionScope.LocalMachine));

            return JsonSerializer.Deserialize<StoredLicense>(json, SerializerOptions);
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(StoredLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);

        var payload = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(license, SerializerOptions)),
            Entropy, DataProtectionScope.LocalMachine);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Once gecici dosyaya yazilip sonra yerine tasinir: yazma sirasinda elektrik
        // kesilirse yarim bir lisans dosyasi kalirdi ve musteri kendi lisansini
        // kaybederek kilitlenirdi.
        var temporary = filePath + ".tmp";
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, filePath, overwrite: true);
    }

    public void Clear()
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
