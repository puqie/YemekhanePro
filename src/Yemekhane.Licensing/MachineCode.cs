using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.Licensing;

/// <summary>
/// Musterinin saticiya gonderdigi MAKINE KODU.
///
/// <para>
/// Lisans dosyasi hedef makineye kilitlenerek uretilir; bunun icin saticinin o
/// makinenin donanim parmak izlerine ihtiyaci vardir. Ekranda gosterilen 12 haneli
/// "Bilgisayar kimligi" tek yonlu bir ozettir ve ondan parmak izlerine DONULEMEZ,
/// dolayisiyla tek basina yeterli degildir.
/// </para>
/// <para>
/// Kod tek satirdir ve sonunda saglama tasir: musteri WhatsApp'ta eksik kopyalarsa
/// ya da satir bolunurse arac bunu ANINDA soyler. Sagalama olmasaydi bozuk koddan
/// sessizce yanlis bir lisans dosyasi uretilir, hata ancak musteri "calismiyor"
/// dediginde ortaya cikardi.
/// </para>
/// </summary>
public static class MachineCode
{
    private const string Prefix = "YMK1";
    private const char Separator = '.';
    private const int ChecksumLength = 6;

    /// <summary>Bu makinenin kodunu uretir.</summary>
    public static string Create(HardwareFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        // Bos hash'ler ayiklanir: okunamayan bir bilesen, kodu uzatmaktan baska
        // ise yaramaz ve karsi tarafta anlamsiz bir parmak izi olusturur.
        var hashes = fingerprint.Hashes.Where(hash => !string.IsNullOrEmpty(hash)).ToArray();
        if (hashes.Length == 0)
            throw new InvalidOperationException("Donanim bilgisi okunamadigi icin makine kodu uretilemedi.");

        // Hash'ler ONALTILIK metindir; ham bayta cevrilerek kodlanir. Metni dogrudan
        // Base64'lemek kodu IKI KATINA cikarirdi (64 karakter yerine 32 bayt) ve
        // musteri bunu WhatsApp'ta elle kopyaliyor -- uzunluk dogrudan hata demektir.
        var bytes = new List<byte>(hashes.Length * 32 + hashes.Length);
        foreach (var hash in hashes)
        {
            var raw = TryFromHex(hash);
            // Beklenmeyen bicimdeki bir hash aynen tasinir: kaybetmek, kodu
            // kisaltmaktan daha kotudur.
            bytes.Add((byte)(raw is null ? 0 : 1));
            bytes.AddRange(raw ?? Encoding.UTF8.GetBytes(hash));
            if (raw is null) bytes.Add(0);   // metin sonu isareti
        }

        var encoded = Convert.ToBase64String([.. bytes])
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');   // URL/metin guvenli
        return $"{Prefix}{Separator}{encoded}{Separator}{Checksum(encoded)}";
    }

    /// <summary>
    /// Kodu parmak izlerine cozer. Bozuk veya eksik kopyalanmis kodda <c>null</c> doner.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        // Musteri kodu WhatsApp'tan kopyalar: bosluk ve satir sonlari temizlenir.
        var cleaned = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var parts = cleaned.Split(Separator);
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var encoded = parts[1];
        if (!string.Equals(parts[2], Checksum(encoded), StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);

            var hashes = new List<string>();
            var index = 0;
            while (index < bytes.Length)
            {
                var isHex = bytes[index++] == 1;
                if (isHex)
                {
                    if (index + 32 > bytes.Length) return null;
                    hashes.Add(Convert.ToHexString(bytes, index, 32));
                    index += 32;
                }
                else
                {
                    var end = Array.IndexOf(bytes, (byte)0, index);
                    if (end < 0) return null;
                    hashes.Add(Encoding.UTF8.GetString(bytes, index, end - index));
                    index = end + 1;
                }
            }
            return hashes.Count == 0 ? null : hashes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Kodun ait oldugu makinenin kisa kimligi; ekranda gosterilenle AYNI deger.
    /// Satici, dogru makine icin uretip uretmedigini bu sayede karsilastirabilir.
    /// </summary>
    public static string? MachineIdOf(string? code) =>
        Parse(code) is { } hashes ? new HardwareFingerprint(hashes).MachineId : null;

    /// <summary>Onaltilik metni ham bayta cevirir; bicim uymuyorsa null.</summary>
    private static byte[]? TryFromHex(string value)
    {
        if (value.Length != 64) return null;
        try { return Convert.FromHexString(value); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Yazim hatasi kalkani. Kriptografik degil: kodun icerigi zaten gizli degildir,
    /// amac yalnizca eksik/bozuk kopyalamayi yakalamaktir.
    /// </summary>
    private static string Checksum(string encoded) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)))[..ChecksumLength];
}
