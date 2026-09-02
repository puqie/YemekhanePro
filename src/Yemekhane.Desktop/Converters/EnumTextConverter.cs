using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// API'den gelen ham İngilizce kod degerlerini ekranda Turkce metne cevirir.
///
/// Arayuz tamamen Turkce olmalidir; ancak API ile konusan degerler (filtre
/// sorgu parametreleri, DataTrigger karsilastirmalari) INGILIZCE KALMALIDIR.
/// Bu yuzden ceviri yalnizca goruntuleme katmaninda, bir converter ile yapilir --
/// ViewModel'deki degeri Turkcelestirirsek sunucuya yanlis filtre gider.
///
/// Hangi sozlugun kullanilacagi ConverterParameter ile secilir:
/// Decision, Status, Source, LogLevel, DeviceStatus, SyncState.
///
/// Iki kritik davranis:
/// 1. Taninmayan bir deger AYNEN dondurulur. Bos string dondurursek sunucunun
///    ekledigi yeni bir durum kodu ekranda kaybolur ve kimse fark etmez.
/// 2. Karsilastirma buyuk/kucuk harf duyarsizdir: API ayni kavram icin hem
///    "Active" (hakedis kaydi) hem "ACTIVE" (rapor projeksiyonu) gonderiyor.
/// </summary>
public sealed class EnumTextConverter : IValueConverter
{
    // Turkce kultur kurallari burada ISTENMEZ: sozluk anahtarlari İngilizce kod
    // degerleridir, "I" harfi Turkce'de "ı"ya donusurse "Information" eslesmez.
    private static readonly StringComparer Codes = StringComparer.OrdinalIgnoreCase;

    private static readonly Dictionary<string, string> Decision = new(Codes)
    {
        ["ALLOW"] = "İzin Verildi",
        ["DENY"] = "Reddedildi",
    };

    private static readonly Dictionary<string, string> Status = new(Codes)
    {
        // Hakedis durumlari (MealEntitlement.Status)
        ["Active"] = "Aktif",
        ["Cancelled"] = "İptal",
        ["Transferred"] = "Aktarıldı",
        // Rapor projeksiyonlarinin buyuk harfli karsiliklari (EfReportRepository)
        ["VOIDED"] = "İptal",
        ["INACTIVE"] = "Pasif",
        ["USED"] = "Kullanıldı",
        ["TRANSFER"] = "Aktarım",
        // Islem / SMS gecmisi durumlari
        ["Completed"] = "Tamamlandı",
        ["Reverted"] = "Geri Alındı",
        ["Pending"] = "Bekliyor",
        ["Failed"] = "Başarısız",
        ["Sent"] = "Gönderildi",
    };

    private static readonly Dictionary<string, string> Source = new(Codes)
    {
        ["Manual"] = "Elle",
        ["Transfer"] = "Aktarım",
        ["BulkTransfer"] = "Toplu Aktarım",
        ["LeaveTransfer"] = "İzin Aktarımı",
        ["Import"] = "İçe Aktarım",
        ["Criteria"] = "Kriter",
    };

    private static readonly Dictionary<string, string> LogLevel = new(Codes)
    {
        ["Trace"] = "İzleme",
        ["Debug"] = "Ayıklama",
        ["Information"] = "Bilgi",
        ["Warning"] = "Uyarı",
        ["Error"] = "Hata",
        ["Critical"] = "Kritik",
    };

    // DevicesViewModel.StatusText ile ayni karsiliklar; orada kod icinde,
    // burada XAML'den erisilebilir bicimde.
    private static readonly Dictionary<string, string> DeviceStatus = new(Codes)
    {
        ["Connected"] = "Bağlı",
        ["Online"] = "Çevrimiçi",
        ["Connecting"] = "Bağlanıyor",
        ["Reconnecting"] = "Yeniden bağlanıyor",
        ["Disconnected"] = "Bağlı değil",
        ["Offline"] = "Çevrimdışı",
        ["Error"] = "Hata",
    };

    private static readonly Dictionary<string, string> SyncState = new(Codes)
    {
        ["Disabled"] = "Kapalı",
        ["Ready"] = "Hazır",
        ["Pending"] = "Bekliyor",
        ["Offline"] = "Çevrimdışı",
        ["Attention"] = "İnceleme gerekiyor",
    };

    /// <summary>
    /// Izin kaydinin yemek hakkina etkisi (LeaveService.Behaviors). Ogrenci detayindaki
    /// Izinler sekmesi bu kodu ham ("Keep") gosteriyordu.
    /// </summary>
    private static readonly Dictionary<string, string> LeaveBehavior = new(Codes)
    {
        ["Keep"] = "Hak korunur",
        ["Cancel"] = "Hak iptal edilir",
        ["NextBusinessDay"] = "Sonraki iş gününe aktarılır",
    };

    // Gecis nedeni (AccessLog.Reason). Karar servisi Turkce yazar ("Kart pasif", "Bugün tatil");
    // cihaz/tohum kaynakli kayitlar ise ham "OK" kodu tasiyabilir. Turkce metinler sozlukte
    // olmadigi icin aynen gecer, yalnizca kod bicimindeki degerler cevrilir.
    private static readonly Dictionary<string, string> Reason = new(Codes)
    {
        ["OK"] = "Geçiş onaylandı",
    };

    // Turnike olay sonucu (TurnstileEvent.Result) ve komutu; Turnike raporunda gorunur.
    private static readonly Dictionary<string, string> TurnstileResult = new(Codes)
    {
        ["OK"] = "Başarılı",
        ["TIMEOUT"] = "Zaman aşımı",
        ["ERROR"] = "Hata",
        ["SKIPPED"] = "Atlandı",
        ["FAILED"] = "Başarısız",
        ["COMPENSATED_RETRY_REQUIRED"] = "Hak iade edildi, yeniden geçiş gerekli",
        ["OPEN"] = "Aç",
        ["DENY"] = "Reddet",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Maps = new(Codes)
    {
        ["Decision"] = Decision,
        ["Status"] = Status,
        ["Reason"] = Reason,
        ["TurnstileResult"] = TurnstileResult,
        ["Source"] = Source,
        ["LogLevel"] = LogLevel,
        ["DeviceStatus"] = DeviceStatus,
        ["SyncState"] = SyncState,
        ["LeaveBehavior"] = LeaveBehavior,
    };

    /// <summary>Kod icinden de kullanilabilsin diye ayri: ViewModel'ler XAML'siz cevirir.</summary>
    public static string Translate(string? value, string? map)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        return map is not null && Maps.TryGetValue(map, out var table) && table.TryGetValue(text, out var turkish)
            ? turkish
            : text; // Taninmayan deger kaybolmaz, ham haliyle gorunur.
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || ReferenceEquals(value, DependencyProperty.UnsetValue)) return "";
        return Translate(value.ToString(), parameter?.ToString());
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Durum metni yalnizca goruntuleme icindir.");
}
