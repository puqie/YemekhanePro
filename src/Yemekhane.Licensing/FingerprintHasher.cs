using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.Licensing;

/// <summary>Donanim bilesenlerini geri donulmez sekilde hash'ler.</summary>
public static class FingerprintHasher
{
    /// <summary>
    /// Bileseni SHA-256 ile hash'ler. Bos veya bosluktan ibaret deger okunamamis sayilir
    /// ve bos dize dondurur - boylece "okunamadi" ile "belirli bir deger" karistirilmaz.
    /// </summary>
    public static string Hash(string? component)
    {
        if (string.IsNullOrWhiteSpace(component)) return string.Empty;

        // Buyuk/kucuk harf ve bosluk farklari ayni donanimi farkli gostermemelidir:
        // WMI ayni seri numarasini surumden surume farkli bicimlendirebilir.
        var normalized = component.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
