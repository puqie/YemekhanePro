using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yemekhane.Licensing;

/// <summary>
/// Musteriye gonderilen lisans dosyasi (.lic).
///
/// <para>
/// Anahtar yerine dosya gonderilmesinin sebebi: sunucusuz modda "bu anahtar daha once
/// kullanildi mi" sorusunu soracak bir merci yoktur, dolayisiyla ayni anahtar ikinci,
/// ucuncu bilgisayarda da aktive edilebilir. Dosya ise URETILIRKEN hedef makineye
/// kilitlenir; kopyalanabilir ama baska makinede matematiksel olarak calismaz.
/// </para>
/// <para>
/// "Kullandiktan sonra kendini silsin" yaklasimi bilerek SECILMEDI: musteri dosyayi
/// acmadan once kopyalarsa -- ki bir dosyayi yedeklemek son derece dogaldir -- silme
/// hicbir sey korumaz, yalnizca guvenlik yanilsamasi yaratir. Makineye kilitlemek
/// kopyalamayi ONEMSIZ hale getirir.
/// </para>
/// </summary>
public static class LicenseFile
{
    /// <summary>Dosya uzantisi; Windows'ta cift tiklanabilir olmasi icin kisa tutuldu.</summary>
    public const string Extension = ".lic";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Lisansi dosya icerigine cevirir.</summary>
    public static string Write(StoredLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return JsonSerializer.Serialize(license, Options);
    }

    /// <summary>
    /// Dosya icerigini okur. Bicim bozuksa <c>null</c> doner: kullaniciya
    /// "dosya gecerli degil" demek, cokme yiginindan iyidir.
    /// </summary>
    public static StoredLicense? Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            var license = JsonSerializer.Deserialize<StoredLicense>(content, Options);
            // Imza ve parmak izi olmadan lisans anlamsizdir; eksikse dosya bozuktur.
            return license is { Signature.Length: > 0, FingerprintHashes.Count: > 0 } ? license : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Musteriye gonderilecek dosya adi. Makine kimligi ada yazilir ki hangi dosyanin
    /// hangi bilgisayara ait oldugu karismasin -- bir okula iki bilgisayar satildiginda
    /// yanlis dosyayi gondermek en sik hatadir.
    /// </summary>
    public static string SuggestFileName(string customerName, string machineId)
    {
        var safe = new StringBuilder();
        foreach (var character in customerName ?? string.Empty)
            safe.Append(char.IsLetterOrDigit(character) ? character : '-');
        var trimmed = safe.ToString().Trim('-');
        if (trimmed.Length == 0) trimmed = "lisans";
        return $"{trimmed}-{machineId}{Extension}";
    }
}
