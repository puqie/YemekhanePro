using System.Globalization;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;

namespace Yemekhane.Application.Sms;

/// <summary>
/// Otomatik SMS kurallari (eski programdaki "Sms Sistemi Tanımları" sag paneli).
/// Saglayici ayarlari (uc nokta, kullanici, gizli) <c>SettingsService</c>'te kalir;
/// burada yalnizca "ne zaman, kime, hangi metinle" kurallari tutulur.
/// </summary>
public sealed record SmsAutomationSettings(
    EntitlementWarningRule EntitlementWarning,
    IncomeNoticeRule IncomeNotice,
    CardReplacementRule CardReplacement)
{
    public static SmsAutomationSettings Default => new(
        new EntitlementWarningRule(false, new TimeOnly(13, 10), 2, SmsAutomationTemplates.EntitlementWarningDefault),
        new IncomeNoticeRule(false, null, SmsAutomationTemplates.IncomeNoticeDefault),
        new CardReplacementRule(false, SmsAutomationTemplates.CardReplacementDefault, null));
}

/// <summary>Her gun <see cref="SendAt"/> saatinde, kalan hak gunu &lt;= <see cref="DaysThreshold"/> olan ogrencilerin velisine.</summary>
public sealed record EntitlementWarningRule(bool Enabled, TimeOnly SendAt, int DaysThreshold, string Template);

/// <summary>Kasaya gelir girildiginde yetkiliye.</summary>
public sealed record IncomeNoticeRule(bool Enabled, string? AdminPhone, string Template);

/// <summary>Kart atandiginda/degistirildiginde birincil veliye; istege bagli olarak yetkiliye de.</summary>
public sealed record CardReplacementRule(bool Enabled, string Template, string? AdminPhone);

/// <summary>GET yaniti: kurallar + sunucu saati (Istanbul) + zamanlanmis kosunun son gunu.</summary>
public sealed record SmsAutomationStatus(SmsAutomationSettings Settings, DateTimeOffset ServerTime,
    DateOnly? LastEntitlementRunDate);

/// <summary>Hak uyarisi kosusunun ozeti; "Şimdi gönder" dugmesi bunu gosterir.</summary>
public sealed record EntitlementWarningRunResult(DateOnly Date, int Candidates, int Queued,
    int SkippedNoPhone, int SkippedAlreadySent);

/// <summary>SMS'e konu ogrencinin kimligi ve birincil (yoksa herhangi aktif) velisi.</summary>
public sealed record StudentSmsContact(Guid StudentId, string StudentNo, string FirstName, string LastName,
    string? ClassName, string? SectionName, string? ParentName, string? ParentPhone);

/// <summary>Hak uyarisi adayi: bugunden itibaren kalan farkli hak gunu sayisi ve son hak tarihi.</summary>
public sealed record EntitlementWarningCandidate(StudentSmsContact Contact, int RemainingDays, DateOnly? LastDate);

public static class SmsAutomationTemplates
{
    public const string EntitlementWarningDefault =
        "Sayın {veli}, {ad} {soyad} ({no}) adlı öğrencinizin {kalan_gun} gün yemek hakkı kalmıştır. Son hak tarihi: {son_tarih}. Lütfen ödeme yapınız.";
    public const string IncomeNoticeDefault =
        "{tarih} {ad} {soyad} ({no}) için {tutar} TL {tur} gelir giriş hareketi olmuştur. {aciklama}";
    public const string CardReplacementDefault =
        "Sayın veli, {ad} {soyad} ({no}) adlı öğrencinizin yemekhane kartı yenilendi. Yeni kart no: {kart_no}.";

    public static readonly IReadOnlyList<string> EntitlementWarningVariables =
        ["ad", "soyad", "no", "sinif", "kalan_gun", "son_tarih", "veli"];
    public static readonly IReadOnlyList<string> IncomeNoticeVariables =
        ["ad", "soyad", "no", "tutar", "tur", "tarih", "aciklama"];
    public static readonly IReadOnlyList<string> CardReplacementVariables =
        ["ad", "soyad", "no", "kart_no", "eski_kart_no"];
}

/// <summary>
/// SMS kaydinin kaynagi, <c>IdempotencyKey</c> onekinden turetilir: sema degismeden
/// (Entities.cs kullanicinin dosyasi) gecmis ekraninda "Elle / Toplu / Otomatik" ayrimi yapilir.
/// Toplu gonderim anahtari SHA-256 hex (64 karakter), otomatik kurallar "oto:" onekli,
/// geri kalani elle (tekil API) gonderimdir.
/// </summary>
public static class SmsSources
{
    public const string Manual = "Manual";
    public const string Bulk = "Bulk";
    public const string AutoEntitlement = "AutoEntitlement";
    public const string AutoIncome = "AutoIncome";
    public const string AutoCard = "AutoCard";

    public const string EntitlementPrefix = "oto:hak:";
    public const string IncomePrefix = "oto:gelir:";
    public const string CardPrefix = "oto:kart:";

    public static readonly IReadOnlyList<string> All = [Manual, Bulk, AutoEntitlement, AutoIncome, AutoCard];

    public static string FromKey(string? idempotencyKey)
    {
        var key = idempotencyKey ?? string.Empty;
        if (key.StartsWith(EntitlementPrefix, StringComparison.Ordinal)) return AutoEntitlement;
        if (key.StartsWith(IncomePrefix, StringComparison.Ordinal)) return AutoIncome;
        if (key.StartsWith(CardPrefix, StringComparison.Ordinal)) return AutoCard;
        return IsBulkKey(key) ? Bulk : Manual;
    }

    private static bool IsBulkKey(string key) => key.Length == 64 && key.All(Uri.IsHexDigit);
}

public static class SmsAutomationValidation
{
    public static SmsAutomationSettings Validate(SmsAutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.EntitlementWarning is null || settings.IncomeNotice is null || settings.CardReplacement is null)
            throw new RequestValidationException("Otomatik SMS kurallarının üçü de gönderilmelidir.");

        var warning = settings.EntitlementWarning;
        if (warning.DaysThreshold is < 1 or > 30)
            throw new RequestValidationException("Hak uyarısı gün eşiği 1 ile 30 arasında olmalıdır.");
        var warningTemplate = Template(warning.Template, SmsAutomationTemplates.EntitlementWarningVariables, "Hak uyarısı");

        var income = settings.IncomeNotice;
        var incomePhone = Phone(income.AdminPhone, income.Enabled, "Gelir bildirimi yetkili GSM no");
        var incomeTemplate = Template(income.Template, SmsAutomationTemplates.IncomeNoticeVariables, "Gelir bildirimi");

        var card = settings.CardReplacement;
        var cardPhone = Phone(card.AdminPhone, false, "Kart yenileme yetkili GSM no");
        var cardTemplate = Template(card.Template, SmsAutomationTemplates.CardReplacementVariables, "Kart yenileme");

        return new SmsAutomationSettings(
            warning with { Template = warningTemplate },
            income with { AdminPhone = incomePhone, Template = incomeTemplate },
            card with { AdminPhone = cardPhone, Template = cardTemplate });
    }

    private static string Template(string? template, IReadOnlyList<string> allowed, string rule)
    {
        var body = template?.Trim() ?? string.Empty;
        if (body.Length == 0) throw new RequestValidationException($"{rule} mesaj şablonu boş olamaz.");
        if (body.Length > 1600) throw new RequestValidationException($"{rule} mesaj şablonu 1600 karakteri aşamaz.");
        foreach (var name in SmsTemplateRenderer.NamedPlaceholders(body))
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new RequestValidationException(
                    $"{rule} şablonunda bilinmeyen değişken: '{{{name}}}'. Kullanılabilir: " +
                    string.Join(" ", allowed.Select(x => "{" + x + "}")) + ".");
        return body;
    }

    /// <summary>
    /// Bos telefon yalnizca kural kapaliyken kabul edilir; doluysa Turkiye mobil numarasi olmali.
    /// Girildigi bicimde saklanir (0532..., 532..., +90532...), gonderimde normallestirilir.
    /// </summary>
    private static string? Phone(string? phone, bool required, string name)
    {
        var text = phone?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (required) throw new RequestValidationException($"{name} zorunludur; kural etkinken boş bırakılamaz.");
            return null;
        }
        var digits = text.Count(char.IsDigit);
        if (digits is < 10 or > 12)
            throw new RequestValidationException($"{name} 10-11 haneli bir GSM numarası olmalıdır (örn. 05321234567).");
        try { TurkishMobilePhone.Normalize(text); }
        catch (RequestValidationException) { throw new RequestValidationException($"{name} geçerli bir Türkiye mobil numarası olmalıdır (5 ile başlamalı)."); }
        return text;
    }
}

/// <summary>Kurallar + son kosu tarihi <c>SystemSetting</c> tablosunda JSON olarak durur (sema degisikligi yok).</summary>
public interface ISmsAutomationStore
{
    Task<SmsAutomationSettings?> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(SmsAutomationSettings settings, CancellationToken cancellationToken);
    Task<DateOnly?> GetLastRunDateAsync(CancellationToken cancellationToken);
    Task SetLastRunDateAsync(DateOnly date, CancellationToken cancellationToken);
}

public interface ISmsAutomationRepository
{
    Task<StudentSmsContact?> GetStudentContactAsync(Guid studentId, CancellationToken cancellationToken);
    /// <summary>
    /// Aktif ogrencilerden, <paramref name="today"/> dahil ileriye donuk kalan hak gunu
    /// (Status Active, kalan adet &gt; 0, farkli tarihler) &lt;= <paramref name="threshold"/> olanlar.
    /// Hic hakedisi olmamis ogrenci dahil DEGILDIR: "hakki bitti" ancak hakki olmus biri icin anlamlidir.
    /// </summary>
    Task<IReadOnlyList<EntitlementWarningCandidate>> ListEntitlementWarningCandidatesAsync(
        DateOnly today, int threshold, CancellationToken cancellationToken);
    /// <summary>Yeni kart disinda en son pasiflesen kartin numarasi (kart degistirmede "eski kart").</summary>
    Task<string?> GetReplacedCardNumberAsync(Guid studentId, Guid newCardId, CancellationToken cancellationToken);
    /// <summary>Verilen onekle baslayan SMS idempotency anahtarlari (ayni gun tekrar gondermemek icin).</summary>
    Task<IReadOnlySet<string>> ListIdempotencyKeysAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>
/// Is servislerinin (gelir, kart) kayit BASARISINDAN sonra cagirdigi kanca. Uygulama hicbir
/// zaman firlatmaz: SMS kuyruklamada hata olursa ana islem yine basarilidir.
/// </summary>
public interface ISmsAutomationTrigger
{
    Task IncomeRecordedAsync(IncomeTransactionDetails transaction, CancellationToken cancellationToken);
    Task CardChangedAsync(CardDetails card, bool replaced, CancellationToken cancellationToken);
}

internal static class SmsAutomationFormat
{
    public static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
}
