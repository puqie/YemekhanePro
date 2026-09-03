using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.Licensing;

/// <summary>
/// SUNUCUSUZ lisans anahtari: YMK-2026-A7K9-3FQ2-X4TB
///
/// <para>
/// Aktivasyon sunucusu olmadan da "bu anahtari ben verdim mi" sorusunun
/// yanitlanabilmesi gerekir; aksi halde musteri rastgele bir sey yazip gecerdi.
/// Cozum: anahtarin SON blogu, onceki bloklarin imza sirriyla hesaplanmis
/// kisaltilmis HMAC'idir. Sirri bilmeyen gecerli anahtar uretemez.
/// </para>
/// <para>
/// Sir masaustu kurulumuna gomulu oldugu icin dogrulama tamamen yereldir --
/// internet, sunucu ve aylik maliyet gerekmez.
/// </para>
/// <para>
/// Bu bir kopyalama korumasi DEGILDIR: ayni anahtar iki bilgisayara girilebilir.
/// Kopyalamaya karsi koruma <see cref="HardwareFingerprint"/> ile lisansin makineye
/// baglanmasindan gelir; bu sinif yalnizca anahtarin SIZDEN geldigini kanitlar.
/// </para>
/// </summary>
public static class OfflineLicenseKey
{
    /// <summary>
    /// Karisabilecek harf/rakamlar (0/O, 1/I/L) DISARIDA birakilir: anahtar telefonda
    /// okunur ve elle yazilir; "0 mi O mu" sorusu destege gereksiz cagri yaratir.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private const int BlockLength = 4;

    /// <summary>Imzali anahtar uretir. <paramref name="secret"/> kurulumdaki sir ile AYNI olmalidir.</summary>
    public static string Create(DateTimeOffset now, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var body = $"YMK-{now.Year}-{RandomBlock()}-{RandomBlock()}";
        return $"{body}-{Checksum(body, secret)}";
    }

    /// <summary>
    /// Anahtarin bu sirla uretilmis olup olmadigini soyler.
    /// Karsilastirma SABIT ZAMANLIDIR: erken cikis, saldirgana dogru blogu
    /// karakter karakter aramaya izin verirdi.
    /// </summary>
    public static bool Verify(string? licenseKey, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var normalized = Normalize(licenseKey);
        var lastSeparator = normalized.LastIndexOf('-');
        if (lastSeparator <= 0 || lastSeparator == normalized.Length - 1) return false;

        var body = normalized[..lastSeparator];
        var provided = normalized[(lastSeparator + 1)..];
        if (provided.Length != BlockLength) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Checksum(body, secret)),
            Encoding.UTF8.GetBytes(provided));
    }

    /// <summary>
    /// Kullanicinin yazdigi anahtari karsilastirmaya HAZIR hale getirir: bosluklar
    /// atilir, buyuk harfe cevrilir. "ymk 2026 a7k9 3fq2 x4tb" ile
    /// "YMK-2026-A7K9-3FQ2-X4TB" ayni lisans olmalidir; aksi halde musteri anahtarini
    /// dogru yazdigi halde "gecersiz" mesaji alir.
    /// </summary>
    public static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>
    /// Govdenin kisaltilmis HMAC'i, anahtar alfabesine yazilir.
    ///
    /// Dort karakter (31^4 ≈ 920 bin olasilik) kaba kuvvete karsi tek basina yeterli
    /// degildir; asil koruma donanim baglantisidir. Buradaki amac, uydurma anahtarlarin
    /// aktivasyon ekraninda ANINDA reddedilmesi ve destek cagrisi yaratmamasidir.
    /// </summary>
    private static string Checksum(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        Span<char> block = stackalloc char[BlockLength];
        for (var i = 0; i < BlockLength; i++)
            block[i] = Alphabet[hash[i] % Alphabet.Length];
        return new string(block);
    }

    private static string RandomBlock()
    {
        Span<char> block = stackalloc char[BlockLength];
        for (var i = 0; i < block.Length; i++)
            block[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(block);
    }
}
