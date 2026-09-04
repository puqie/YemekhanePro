namespace Yemekhane.Devices.Abstractions;

/// <summary>
/// Cihaz hata kodlarinin uretici-bagimsiz siniflandirmasi.
///
/// Kodlar "SATICI_SEBEP" bicimindedir (SF300_TIMEOUT, ZK_TIMEOUT). Tuketiciler kodu tam metniyle
/// karsilastirirsa her yeni cihaz ailesi sessizce siniflandirma disinda kalir: ornegin kopmus bir
/// baglanti "kalici hata" sayilmaz ve kart yukleme dongusu olu cihazi kart kart denemeye devam eder.
/// Bu yuzden siniflandirma SEBEP son ekine gore yapilir, saticiya gore degil.
/// </summary>
public static class DeviceErrorCodes
{
    /// <summary>Yeniden denemenin durumu degistirmeyecegi kalici sebepler.</summary>
    private static readonly HashSet<string> PermanentReasons =
        ["INVALID_CARD", "MEMORY_FULL", "UNSUPPORTED", "CAPABILITY", "DEVICE_VALIDATION_REQUIRED"];

    /// <summary>Cihazla baglantinin kopmus oldugunu gosteren sebepler.</summary>
    private static readonly HashSet<string> DisconnectedReasons =
        ["DISCONNECTED", "CONNECT_FAILED", "CONNECT_TIMEOUT", "WRITE_FAILED", "NOT_CONFIGURED"];

    /// <summary>Kart bu cihaza hic yazilamaz; kuyrukta tutmak yalnizca gercek sorunlari gizler.</summary>
    public static bool IsPermanent(string? errorCode) => Matches(errorCode, PermanentReasons);

    /// <summary>Baglanti koptu; ayni turda kalan kartlari denemek anlamsizdir.</summary>
    public static bool IsDisconnected(string? errorCode) => Matches(errorCode, DisconnectedReasons);

    private static bool Matches(string? errorCode, HashSet<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) return false;
        var normalized = errorCode.Trim().ToUpperInvariant();

        // Tam eslesme, saticisiz kodlari (ornegin "DISCONNECTED") da kapsar.
        if (reasons.Contains(normalized)) return true;

        // "SF300_CONNECT_TIMEOUT" -> "CONNECT_TIMEOUT". Sebepler alt cizgi icerdiginden ilk alt
        // cizgiden bolmek yetmez; bilinen her sebep icin son ek karsilastirmasi yapilir.
        foreach (var reason in reasons)
        {
            if (normalized.Length > reason.Length + 1 &&
                normalized.EndsWith(reason, StringComparison.Ordinal) &&
                normalized[normalized.Length - reason.Length - 1] == '_')
            {
                return true;
            }
        }

        return false;
    }
}
