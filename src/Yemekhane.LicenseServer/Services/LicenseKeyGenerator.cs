using System.Security.Cryptography;

namespace Yemekhane.LicenseServer.Services;

/// <summary>
/// Musteriye verilecek lisans anahtarini uretir: YMK-2026-A7K9-3FQ2.
///
/// Anahtar TAHMIN EDILEMEZ olmalidir: sirali numara verilseydi (YMK-0001, YMK-0002)
/// bir musteri komsu numarayi deneyerek baskasinin lisansini aktive edebilirdi.
/// Bu yuzden son iki blok kriptografik rastgeledir.
/// </summary>
public static class LicenseKeyGenerator
{
    /// <summary>
    /// Karisabilecek harf/rakamlar (0/O, 1/I/L) DISARIDA birakilir: anahtar telefonda
    /// okunur ve elle yazilir; "0 mi O mu" sorusu destege gereksiz cagri yaratir.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Create(DateTimeOffset now) =>
        $"YMK-{now.Year}-{Block()}-{Block()}";

    private static string Block()
    {
        Span<char> block = stackalloc char[4];
        for (var i = 0; i < block.Length; i++)
            block[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(block);
    }

    /// <summary>
    /// Kullanicinin yazdigi anahtari karsilastirmaya HAZIR hale getirir: bosluklar
    /// atilir, buyuk harfe cevrilir. "ymk 2026 a7k9 3fq2" ile "YMK-2026-A7K9-3FQ2"
    /// ayni lisansi bulmalidir; aksi halde musteri anahtarini dogru yazdigi halde
    /// "bulunamadi" mesaji alir.
    /// </summary>
    public static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}
