using Yemekhane.Application.Reports;

namespace Yemekhane.Reports;

/// <summary>
/// Disa aktarilan (PDF / Excel / CSV) raporlarda ham kod degerlerini Turkcelestirir.
///
/// Ekran bu isi masaustundeki EnumTextConverter ile yapiyordu; dosyalar ise "ALLOW", "VOIDED",
/// "OK" gibi kodlarla cikiyordu. Okul memurunun eline gecen belge Turkce olmali. Sozluk
/// masaustundekiyle ayni kavramlari tasir; taninmayan deger AYNEN kalir ki yeni bir kod ekranda
/// kaybolmasin. Karsilastirma buyuk/kucuk harf duyarsizdir (API "Active" ve "ACTIVE" yollar).
/// </summary>
public static class ReportText
{
    private static readonly StringComparer Codes = StringComparer.OrdinalIgnoreCase;

    private static readonly Dictionary<string, string> DecisionMap = new(Codes)
    {
        ["ALLOW"] = "İzin Verildi", ["DENY"] = "Reddedildi", ["ERROR"] = "Hata",
    };

    private static readonly Dictionary<string, string> StatusMap = new(Codes)
    {
        ["Active"] = "Aktif", ["Cancelled"] = "İptal", ["Transferred"] = "Aktarıldı",
        ["VOIDED"] = "İptal", ["INACTIVE"] = "Pasif", ["USED"] = "Kullanıldı", ["TRANSFER"] = "Aktarım",
        ["Completed"] = "Tamamlandı", ["Reverted"] = "Geri Alındı", ["Pending"] = "Bekliyor",
        ["Failed"] = "Başarısız", ["Sent"] = "Gönderildi",
    };

    private static readonly Dictionary<string, string> ReasonMap = new(Codes)
    {
        ["OK"] = "Geçiş onaylandı",
    };

    private static readonly Dictionary<string, string> TurnstileMap = new(Codes)
    {
        ["OK"] = "Başarılı", ["TIMEOUT"] = "Zaman aşımı", ["ERROR"] = "Hata", ["SKIPPED"] = "Atlandı",
        ["FAILED"] = "Başarısız", ["COMPENSATED_RETRY_REQUIRED"] = "Hak iade edildi, yeniden geçiş gerekli",
        ["OPEN"] = "Aç", ["DENY"] = "Reddet",
    };

    public static string Decision(string? value) => Translate(DecisionMap, value);

    /// <summary>
    /// "Status" sutunu rapor turune gore farkli sey tasir: gecis raporlarinda AccessLog.Reason,
    /// turnike raporunda TurnstileEvent.Result, digerlerinde durum kodu.
    /// </summary>
    public static string Status(ReportRow row) => row.Type switch
    {
        ReportType.Turnstile => Translate(TurnstileMap, row.Status),
        ReportType.DailyAccess or ReportType.DeniedAccess => Translate(ReasonMap, row.Status),
        _ => Translate(StatusMap, row.Status)
    };

    /// <summary>Turnike aciklamasi "OPEN / hata" -> "Aç / hata"; diger raporlarda aciklama aynen kalir.</summary>
    public static string Description(ReportRow row)
    {
        if (row.Type != ReportType.Turnstile || string.IsNullOrWhiteSpace(row.Description)) return row.Description ?? "";
        var parts = row.Description.Split(" / ", 2, StringSplitOptions.None);
        var command = Translate(TurnstileMap, parts[0]);
        return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? command + " / " + parts[1] : command;
    }

    private static string Translate(Dictionary<string, string> map, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        return map.TryGetValue(text, out var turkish) ? turkish : text;
    }
}
