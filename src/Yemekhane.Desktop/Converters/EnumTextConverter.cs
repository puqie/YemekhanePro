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
        // Masaustu "Hizli Hakedis" cekmecesinin gonderdigi kaynak etiketi (MealEntitlementsViewModel.BuildGrant).
        // Ham haliyle "WPF Quick Grant" olarak gorunuyor ve sutunda kesiliyordu.
        ["WPF Quick Grant"] = "Hızlı Hakediş",
        ["QuickGrant"] = "Hızlı Hakediş",
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

    // Toplu takvim islemi turleri (BulkOperationService.Operations). Sihirbazin
    // 1. adimindaki acilir kutu ve Gecmis tablosu bu kodlari ham gosteriyordu.
    private static readonly Dictionary<string, string> BulkOperation = new(Codes)
    {
        ["CancelEntitlements"] = "Hak iptali / aktarımı",
        ["Holiday"] = "Tatil",
        ["Trip"] = "Gezi",
        ["Leave"] = "İzin",
        ["Transfer"] = "Aktarım",
    };

    // Hak davranislari: tatil/istisna/toplu islem hepsinde ayni kod kumesi kullanilir
    // (HolidayService, CalendarService, BulkOperationService). Ekranda ne yapacagi
    // acikca yazilmali; "Delete" kelimesi hakkin silinecegini ima eder, oysa kayit
    // iptal durumuna cekilir.
    private static readonly Dictionary<string, string> TransferBehavior = new(Codes)
    {
        ["Keep"] = "Hakları koru",
        ["Delete"] = "Hakları iptal et",
        ["Cancel"] = "Hakları iptal et",
        ["Forfeit"] = "Hakları yak (iade yok)",
        ["NextBusinessDay"] = "Sonraki iş gününe aktar",
        ["SpecifiedDate"] = "Belirli bir tarihe aktar",
    };

    private static readonly Dictionary<string, string> HolidayType = new(Codes)
    {
        ["Official"] = "Resmî tatil",
        ["Administrative"] = "İdari tatil",
        ["Trip"] = "Gezi",
        ["Other"] = "Diğer",
        ["Bulk"] = "Toplu işlem tatili",
    };

    private static readonly Dictionary<string, string> ExceptionType = new(Codes)
    {
        ["Trip"] = "Gezi",
        ["Special"] = "Özel gün",
        ["ScheduleChange"] = "Program değişikliği",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Maps = new(Codes)
    {
        ["Decision"] = Decision,
        ["Status"] = Status,
        ["Source"] = Source,
        ["LogLevel"] = LogLevel,
        ["DeviceStatus"] = DeviceStatus,
        ["SyncState"] = SyncState,
        ["BulkOperation"] = BulkOperation,
        ["TransferBehavior"] = TransferBehavior,
        ["HolidayType"] = HolidayType,
        ["ExceptionType"] = ExceptionType,
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
