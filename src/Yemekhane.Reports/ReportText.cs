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
        // Toplu islemde "Yakma" secilince yazilan durum; ekran sozlugunde vardi,
        // rapor sozlugunde YOKTU ve dosyada ham "Forfeited" basiliyordu.
        ["Forfeited"] = "Yakıldı",
        // SMS raporunda gorunen ara durumlar; bunlar da yalnizca ekranda cevriliydi.
        ["Sending"] = "Gönderiliyor", ["RetryScheduled"] = "Yeniden denenecek",
    };

    /// <summary>
    /// Tatil/Aktarim raporunda DURUM sutunu tatil TURUNU tasir (HolidayType).
    /// Bu degerler StatusMap'te YOKTU: rapor "Official" / "Administrative" / "Bulk"
    /// diye ham Ingilizce basiyordu -- hem ekranda hem PDF/Excel/CSV'de.
    /// </summary>
    private static readonly Dictionary<string, string> HolidayTypeMap = new(Codes)
    {
        ["Official"] = "Resmî tatil",
        ["Administrative"] = "İdari izin",
        ["Trip"] = "Gezi",
        ["Other"] = "Diğer",
        ["Bulk"] = "Toplu işlem",
    };

    /// <summary>
    /// Aktarim davranisi kodlari. Tatil raporunun ACIKLAMA sutununda ham geciyordu:
    /// "Yılbaşı / NextBusinessDay" gibi yari Turkce metinler olusuyordu.
    /// </summary>
    private static readonly Dictionary<string, string> TransferBehaviorMap = new(Codes)
    {
        ["Delete"] = "Hakları iptal et",
        ["Forfeit"] = "Hakları yak",
        ["NextBusinessDay"] = "Sonraki iş gününe aktar",
        ["SpecifiedDate"] = "Belirli bir tarihe aktar",
    };

    private static readonly Dictionary<string, string> ReasonMap = new(Codes)
    {
        ["OK"] = "Geçiş onaylandı",
        // On odemeli bakiye yolu (BalanceAccessReasons). Ekrandaki EnumTextConverter bunlari
        // cevirdigi halde burada eksikti: rapor ekranda "Bakiyeden düşüldü" yazarken ayni
        // raporun PDF/Excel/CSV ciktisinda ham "BalanceUsed" kodu basiliyordu.
        ["BalanceUsed"] = "Bakiyeden düşüldü",
        ["InsufficientBalance"] = "Yemek hakkı yok; bakiye yetersiz",
    };

    private static readonly Dictionary<string, string> TurnstileMap = new(Codes)
    {
        ["OK"] = "Başarılı", ["TIMEOUT"] = "Zaman aşımı", ["ERROR"] = "Hata", ["SKIPPED"] = "Atlandı",
        ["FAILED"] = "Başarısız", ["COMPENSATED_RETRY_REQUIRED"] = "Hak iade edildi, yeniden geçiş gerekli",
        ["OPEN"] = "Aç", ["DENY"] = "Reddet",
    };

    /// <summary>
    /// Bakiye defteri satir turleri (StudentBalanceEntryKinds); Bakiye Hareketleri raporunda
    /// "Hareket" sutunu. Ekrandaki EnumTextConverter "BalanceKind" sozluguyle ayni.
    /// </summary>
    private static readonly Dictionary<string, string> BalanceKindMap = new(Codes)
    {
        ["TopUp"] = "Yükleme", ["Deduction"] = "Düşüm", ["Refund"] = "İade", ["Adjustment"] = "Düzeltme",
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
        // Bakiye Hareketleri'nde "Status" defter satir turudur (TopUp/Deduction/Refund/Adjustment).
        ReportType.Balance => Translate(BalanceKindMap, row.Status),
        // Tatil/Aktarim raporunda "Status" tatil TURUDUR, durum kodu degil.
        ReportType.HolidayTransfer => Translate(HolidayTypeMap, row.Status),
        // Tatil/Aktarim raporunda "Status" tatil TURUDUR, durum kodu degil.

        _ => Translate(StatusMap, row.Status)
    };

    /// <summary>Turnike aciklamasi "OPEN / hata" -> "Aç / hata"; diger raporlarda aciklama aynen kalir.</summary>
    public static string Description(ReportRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Description)) return row.Description ?? "";

        // TATIL/AKTARIM: aciklama "tatil adi / davranis kodu" bicimindedir ve IKINCI
        // parca ham geciyordu -- "Yılbaşı / NextBusinessDay" gibi yari Turkce metinler
        // olusuyordu. Ilk parca tatilin ADIDIR, cevrilmez.
        if (row.Type == ReportType.HolidayTransfer)
        {
            var holiday = row.Description.Split(" / ", 2, StringSplitOptions.None);
            return holiday.Length > 1 && !string.IsNullOrWhiteSpace(holiday[1])
                ? holiday[0] + " / " + Translate(TransferBehaviorMap, holiday[1])
                : row.Description;
        }

        if (row.Type != ReportType.Turnstile) return row.Description;
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
