using System.Globalization;
using System.Text.Json;
using Yemekhane.Desktop.Converters;

namespace Yemekhane.Desktop.Services;

/// <summary>
/// Ogrenci detay sekmelerindeki bir JSON kaydini kullaniciya gosterilecek
/// Turkce tek satira cevirir.
///
/// NEDEN GENEL BIR DOKUMCU DEGIL:
/// Onceki surum JSON'daki ilk 6 alani ham adlariyla basiyordu. Bu iki
/// somut zarar veriyordu:
///  1. Alan sirasi DTO'nun kayit sirasidir, onem sirasi degil. Odemelerde
///     ilk alti alanin ucu GUID (id, operationId, studentId) oldugu icin
///     asil bilgi -- TUTAR, gelir turu ve aciklama -- kesiliyordu.
///     Kullanici odeme gecmisinde odedigi parayi goremiyordu.
///  2. Etiketler ("cardNumber", "validFrom") ve degerler (GUID, ISO tarih,
///     ALLOW/DENY, true/false) ham teknik biciminde kaliyordu; arayuzun
///     geri kalani tamamen Turkce.
///
/// Bu yuzden her sekme icin HANGI alanlarin, HANGI sirayla, HANGI Turkce
/// etiketle ve HANGI bicimde gosterilecegi burada ACIKCA tanimlanir.
/// Yeni bir alan ekrana gelsin isteniyorsa listeye yazilmalidir -- sessizce
/// kesilmesindense hic gorunmemesi ve fark edilmesi yeglenir.
///
/// GUID'ler kasten disaridadir: kullaniciya hicbir sey anlatmaz, satirin
/// tamamini doldurur ve asil bilgiyi disari iter.
/// </summary>
public static class StudentTabFormatter
{
    /// <summary>Alanin nasil bicimlendirilecegi. Ham metin varsayilan.</summary>
    public enum FieldKind
    {
        Text,
        /// <summary>DateTimeOffset/DateTime -> "02.09.2026 13:00" (saniye YOK).</summary>
        DateTime,
        /// <summary>DateOnly ("2026-09-02") -> "02.09.2026".</summary>
        Date,
        /// <summary>decimal -> "₺750,00".</summary>
        Money,
        /// <summary>true/false -> "Evet"/"Hayır".</summary>
        YesNo,
    }

    /// <summary>
    /// Bir alanin gosterim tanimi. <paramref name="Map"/> doluysa deger
    /// EnumTextConverter sozluklerinden cevrilir (ALLOW -> "İzin Verildi").
    /// <paramref name="TrueText"/>/<paramref name="FalseText"/> bool alanlar
    /// icin duruma ozel metin verir (Aktif/Pasif gibi).
    /// </summary>
    public sealed record Field(
        string Json,
        string Label,
        FieldKind Kind = FieldKind.Text,
        string? Map = null,
        string? TrueText = null,
        string? FalseText = null);

    private const string Separator = "  |  ";

    /// <summary>Hicbir kayit donmediginde gosterilecek metin.</summary>
    public const string EmptyText = "Kayıt yok.";

    // Sekme KIMLIKLERI degismez: LoadTabAsync bu adlara gore uc nokta secer.
    // Ekranda gorunen Turkce basliklar ayri tutulur (bkz. TabTitle).
    private static readonly Dictionary<string, Field[]> Layouts = new(StringComparer.Ordinal)
    {
        // CardDetails: kart numarasi ve gecerlilik araligi kimligi belirler.
        ["Cards"] =
        [
            new("cardNumber", "Kart No"),
            new("isActive", "Durum", FieldKind.YesNo, TrueText: "Aktif", FalseText: "Pasif"),
            new("validFrom", "Başlangıç", FieldKind.DateTime),
            new("validTo", "Bitiş", FieldKind.DateTime),
            new("replacementReason", "Değiştirme Nedeni"),
        ],

        // ParentDetails: veliyi arayan kullanici once ad ve telefon ister.
        ["Parents"] =
        [
            new("name", "Ad Soyad"),
            new("relationship", "Yakınlık"),
            new("phone", "Telefon"),
            new("isPrimary", "Birincil", FieldKind.YesNo),
            new("isActive", "Durum", FieldKind.YesNo, TrueText: "Aktif", FalseText: "Pasif"),
        ],

        // EntitlementDetails: tarih + kalan hak, hakedis sorgusunun cevabidir.
        ["Entitlements"] =
        [
            new("date", "Tarih", FieldKind.Date),
            new("quantity", "Hak"),
            new("consumedQuantity", "Kullanılan"),
            new("remainingQuantity", "Kalan"),
            new("status", "Durum", Map: "Status"),
            new("source", "Kaynak", Map: "Source"),
        ],

        // DailyTrackingRow: KARAR en kritik alan; ham sirada 13. gelir ve
        // eski dokumcude her zaman kesilirdi.
        ["Access History"] =
        [
            new("timestamp", "Zaman", FieldKind.DateTime),
            new("decision", "Karar", Map: "Decision"),
            new("mealType", "Öğün"),
            new("deviceName", "Cihaz"),
            new("cardNumber", "Kart No"),
            new("reason", "Sebep"),
        ],

        // LeaveDetails
        ["Leaves"] =
        [
            new("startsOn", "Başlangıç", FieldKind.Date),
            new("endsOn", "Bitiş", FieldKind.Date),
            new("leaveType", "İzin Türü"),
            new("entitlementBehavior", "Hakediş"),
            new("description", "Açıklama"),
        ],

        // MealTransferDetails: aktarimin nereden nereye gittigi.
        ["Holiday/Transfer"] =
        [
            new("originalDate", "Kaynak Tarih", FieldKind.Date),
            new("targetDate", "Hedef Tarih", FieldKind.Date),
            new("quantity", "Adet"),
            new("reason", "Sebep"),
        ],

        // IncomeTransactionDetails: TUTAR ilk sirada. Ham JSON'da 9. alandir
        // ve eski Take(6) yuzunden hic gorunmuyordu -- bu hatanin merkezi.
        ["Payments"] =
        [
            new("transactionAt", "Tarih", FieldKind.DateTime),
            new("amount", "Tutar", FieldKind.Money),
            new("incomeTypeName", "Gelir Türü"),
            new("description", "Açıklama"),
            new("isVoided", "İptal edildi", FieldKind.YesNo),
            new("voidReason", "İptal Nedeni"),
        ],

        // SmsLogDetails: gonderim durumu ve hata mesaji.
        ["SMS History"] =
        [
            new("createdAt", "Tarih", FieldKind.DateTime),
            new("status", "Durum", Map: "Status"),
            new("phone", "Telefon"),
            new("message", "Mesaj"),
            new("error", "Hata"),
        ],

        // AuditLogDetails: kim ne zaman ne yapti.
        ["Audit"] =
        [
            new("timestamp", "Zaman", FieldKind.DateTime),
            new("action", "İşlem"),
            new("description", "Açıklama"),
            new("affectedRecords", "Etkilenen Kayıt"),
        ],
    };

    // Ekranda gorunen basliklar. KIMLIK (sozluk anahtari) ile GORUNEN METIN
    // ayri tutulur; kimlik degisirse LoadTabAsync switch'i ve
    // StudentsViewModel'deki "Leaves" aramasi kirilir.
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["General"] = "Genel",
        ["Cards"] = "Kartlar",
        ["Parents"] = "Veliler",
        ["Entitlements"] = "Hakedişler",
        ["Access History"] = "Geçiş Geçmişi",
        ["Leaves"] = "İzinler",
        ["Holiday/Transfer"] = "Tatil/Aktarım",
        ["Payments"] = "Ödemeler",
        ["SMS History"] = "SMS Geçmişi",
        ["Audit"] = "Denetim",
    };

    /// <summary>Sekme kimliginin Turkce ekran basligi; taninmayan kimlik aynen doner.</summary>
    public static string TabTitle(string tab) => Titles.TryGetValue(tab, out var title) ? title : tab;

    /// <summary>
    /// Bir kaydi sekmeye ozel alan listesine gore Turkcelestirir.
    ///
    /// Bos/null alanlar hic yazilmaz; aksi halde satir "Açıklama: " diye
    /// biter ve kullanici eksik veri mi hata mi oldugunu anlayamaz.
    /// </summary>
    public static string Summarize(string tab, JsonElement value)
    {
        if (!Layouts.TryGetValue(tab, out var fields))
            return Fallback(value);

        var parts = new List<string>(fields.Length);
        foreach (var field in fields)
        {
            if (!value.TryGetProperty(field.Json, out var raw)) continue;
            var text = Format(raw, field);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add($"{field.Label}: {text}");
        }

        // Tanimli alanlarin hicbiri dolu degilse bos satir gostermek yerine
        // ham dokume duseriz: veri var ama beklenmedik bicimde demektir.
        return parts.Count > 0 ? string.Join(Separator, parts) : Fallback(value);
    }

    private static string Format(JsonElement raw, Field field)
    {
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            or JsonValueKind.Object or JsonValueKind.Array) return "";

        if (raw.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var flag = raw.GetBoolean();
            // "İptal edildi: Hayır" gurultudur; olumsuz bayrak yazilmaz.
            if (!flag && field.TrueText is null) return "";
            return flag ? field.TrueText ?? "Evet" : field.FalseText ?? "Hayır";
        }

        var text = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.ToString();
        if (string.IsNullOrWhiteSpace(text)) return "";

        return field.Kind switch
        {
            FieldKind.DateTime => FormatDateTime(text),
            FieldKind.Date => FormatDate(text),
            FieldKind.Money => FormatMoney(raw, text),
            _ => field.Map is not null ? EnumTextConverter.Translate(text, field.Map) : text,
        };
    }

    // Turkce kultur sabit: makinenin bolgesel ayari ne olursa olsun ekranda
    // ayni bicim gorunmelidir.
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Saniye ve milisaniye kasten atilir: "02.09.2026 13:00".</summary>
    private static string FormatDateTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Turkish)
            : text;

    private static string FormatDate(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("dd.MM.yyyy", Turkish)
            : text;

    private static string FormatMoney(JsonElement raw, string text) =>
        raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out var amount)
            ? amount.ToString("C2", Turkish)
            : text;

    /// <summary>
    /// Tanimsiz sekme ya da beklenmedik govde icin son care: skaler alanlari
    /// ham adlariyla basar. GUID'ler yine elenir -- kullaniciya hicbir sey
    /// anlatmadigi icin gosterilmeleri satiri bozmaktan baska ise yaramaz.
    /// </summary>
    private static string Fallback(JsonElement value) => string.Join(Separator, value.EnumerateObject()
        .Where(x => x.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Object and not JsonValueKind.Array)
        .Where(x => !IsGuid(x.Value))
        .Take(6).Select(x => $"{x.Name}: {x.Value}"));

    private static bool IsGuid(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.TryGetGuid(out _);
}
