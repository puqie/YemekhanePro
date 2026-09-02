using System.Globalization;
using Microsoft.Extensions.Logging;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;

namespace Yemekhane.Application.Sms;

/// <summary>
/// Otomatik SMS kurallarinin degerlendirilmesi: ayar okuma/yazma, gelir ve kart kancalari,
/// gunluk hak uyarisi. Gonderim yapmaz; <see cref="ISmsLogRepository"/> ile kuyruga yazar,
/// mevcut <c>SmsDispatcher</c> gonderir. Tekrarli calismaya karsi koruma idempotency
/// anahtaridir: "oto:gelir:{islemId}", "oto:kart:{kartId}", "oto:hak:{gun}:{ogrenciId}".
/// </summary>
public sealed class SmsAutomationService(
    ISmsAutomationStore store,
    ISmsAutomationRepository repository,
    ISmsLogRepository smsLogs,
    TimeProvider timeProvider,
    ILogger<SmsAutomationService>? logger = null,
    IAuditService? audit = null) : ISmsAutomationTrigger
{
    private static readonly TimeZoneInfo Istanbul = FindIstanbul();
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<SmsAutomationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await store.GetAsync(cancellationToken) ?? SmsAutomationSettings.Default;

    public async Task<SmsAutomationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        new(await GetSettingsAsync(cancellationToken), NowIstanbul(), await store.GetLastRunDateAsync(cancellationToken));

    public async Task<SmsAutomationStatus> SaveAsync(SmsAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        var valid = SmsAutomationValidation.Validate(settings);
        audit?.Record(new AuditEntry("SmsAutomationUpdated", "SystemSetting", "sms.automation",
            "Otomatik SMS kuralları güncellendi.", After: new
            {
                EntitlementWarning = new { valid.EntitlementWarning.Enabled, valid.EntitlementWarning.SendAt, valid.EntitlementWarning.DaysThreshold },
                IncomeNotice = new { valid.IncomeNotice.Enabled, PhoneSet = valid.IncomeNotice.AdminPhone is not null },
                CardReplacement = new { valid.CardReplacement.Enabled, PhoneSet = valid.CardReplacement.AdminPhone is not null }
            }));
        await store.SaveAsync(valid, cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Zamanlanmis kosu karari (saf): kural acik, Istanbul saati gonderim saatini GECMIS ve bugun
    /// daha once kosulmamis. "Esit" degil "gecmis": API 13:10'da kapaliysa 14:00'te acilinca
    /// o gunun uyarisi yine gider; ertesi gune sarkmaz.
    /// </summary>
    public static bool IsScheduledRunDue(SmsAutomationSettings settings, DateTimeOffset nowUtc, DateOnly? lastRunDate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EntitlementWarning.Enabled) return false;
        var local = TimeZoneInfo.ConvertTime(nowUtc, Istanbul);
        var today = DateOnly.FromDateTime(local.DateTime);
        if (lastRunDate == today) return false;
        return TimeOnly.FromDateTime(local.DateTime) >= settings.EntitlementWarning.SendAt;
    }

    /// <summary>Arka plan isleyicisi her dakika cagirir; kosulduysa true.</summary>
    public async Task<bool> RunScheduledAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (!IsScheduledRunDue(settings, now, await store.GetLastRunDateAsync(cancellationToken))) return false;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Istanbul).DateTime);
        // Once tarih yazilir: kosu yarida kalirsa (istisna) ayni gun her dakika yeniden
        // denenip ayni ogrencilere tekrar tekrar gitmesin; dedupe anahtari zaten ikinci korumadir.
        await store.SetLastRunDateAsync(today, cancellationToken);
        await RunEntitlementWarningAsync(settings, today, cancellationToken);
        return true;
    }

    /// <summary>Elle tetikleme ("Şimdi gönder"): kural kapali olsa da kayitli esik ve sablonla kosar.</summary>
    public async Task<EntitlementWarningRunResult> RunEntitlementWarningAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var today = DateOnly.FromDateTime(NowIstanbul().DateTime);
        return await RunEntitlementWarningAsync(settings, today, cancellationToken);
    }

    public async Task<EntitlementWarningRunResult> RunEntitlementWarningAsync(SmsAutomationSettings settings, DateOnly today,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var rule = settings.EntitlementWarning;
        var candidates = await repository.ListEntitlementWarningCandidatesAsync(today, rule.DaysThreshold, cancellationToken);
        var prefix = SmsSources.EntitlementPrefix + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ":";
        var alreadySent = await repository.ListIdempotencyKeysAsync(prefix, cancellationToken);
        int queued = 0, noPhone = 0, duplicate = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contact = candidate.Contact;
            if (string.IsNullOrWhiteSpace(contact.ParentPhone)) { noPhone++; continue; }
            var key = prefix + contact.StudentId.ToString("D");
            if (alreadySent.Contains(key)) { duplicate++; continue; }
            var message = SmsTemplateRenderer.RenderNamed(rule.Template, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ad"] = contact.FirstName, ["soyad"] = contact.LastName, ["no"] = contact.StudentNo,
                ["sinif"] = ClassText(contact), ["kalan_gun"] = candidate.RemainingDays.ToString(CultureInfo.InvariantCulture),
                ["son_tarih"] = candidate.LastDate?.ToString("dd.MM.yyyy", Turkish) ?? "-",
                ["veli"] = string.IsNullOrWhiteSpace(contact.ParentName) ? "Veli" : contact.ParentName
            });
            await smsLogs.EnqueueAsync(TurkishMobilePhone.Normalize(contact.ParentPhone), message, key,
                contact.StudentId, null, cancellationToken);
            queued++;
        }
        return new EntitlementWarningRunResult(today, candidates.Count, queued, noPhone, duplicate);
    }

    async Task ISmsAutomationTrigger.IncomeRecordedAsync(IncomeTransactionDetails transaction, CancellationToken cancellationToken)
    {
        try
        {
            var rule = (await GetSettingsAsync(cancellationToken)).IncomeNotice;
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.AdminPhone)) return;
            var contact = transaction.StudentId is { } studentId
                ? await repository.GetStudentContactAsync(studentId, cancellationToken) : null;
            var local = TimeZoneInfo.ConvertTime(transaction.TransactionAt, Istanbul);
            var message = SmsTemplateRenderer.RenderNamed(rule.Template, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ad"] = contact?.FirstName ?? "Öğrenci belirtilmedi", ["soyad"] = contact?.LastName,
                ["no"] = contact?.StudentNo ?? transaction.CardNumber ?? "-",
                ["tutar"] = transaction.Amount.ToString("N2", Turkish), ["tur"] = transaction.IncomeTypeName,
                ["tarih"] = local.ToString("dd.MM.yyyy HH:mm", Turkish), ["aciklama"] = transaction.Description
            });
            await smsLogs.EnqueueAsync(TurkishMobilePhone.Normalize(rule.AdminPhone), message,
                SmsSources.IncomePrefix + transaction.Id.ToString("D"), transaction.StudentId, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogTriggerFailed(logger, "gelir", transaction.Id, exception); }
    }

    async Task ISmsAutomationTrigger.CardChangedAsync(CardDetails card, bool replaced, CancellationToken cancellationToken)
    {
        try
        {
            var rule = (await GetSettingsAsync(cancellationToken)).CardReplacement;
            if (!rule.Enabled) return;
            var contact = await repository.GetStudentContactAsync(card.StudentId, cancellationToken);
            if (contact is null) return;
            var previous = replaced ? await repository.GetReplacedCardNumberAsync(card.StudentId, card.Id, cancellationToken) : null;
            var message = SmsTemplateRenderer.RenderNamed(rule.Template, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ad"] = contact.FirstName, ["soyad"] = contact.LastName, ["no"] = contact.StudentNo,
                ["kart_no"] = card.CardNumber, ["eski_kart_no"] = previous ?? "-"
            });
            var key = SmsSources.CardPrefix + card.Id.ToString("D");
            if (!string.IsNullOrWhiteSpace(contact.ParentPhone))
                await smsLogs.EnqueueAsync(TurkishMobilePhone.Normalize(contact.ParentPhone), message, key,
                    card.StudentId, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(rule.AdminPhone))
                await smsLogs.EnqueueAsync(TurkishMobilePhone.Normalize(rule.AdminPhone), message, key + ":yetkili",
                    card.StudentId, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogTriggerFailed(logger, "kart", card.Id, exception); }
    }

    private DateTimeOffset NowIstanbul() => TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), Istanbul);

    private static string? ClassText(StudentSmsContact contact) =>
        string.Join("/", new[] { contact.ClassName, contact.SectionName }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static TimeZoneInfo FindIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }

    private static readonly Action<ILogger, string, Guid, Exception?> TriggerFailed = LoggerMessage.Define<string, Guid>(
        LogLevel.Error, new EventId(2951, nameof(TriggerFailed)),
        "Otomatik SMS ({Rule}) kuyruklanamadı; ana işlem etkilenmedi. Kayıt: {EntityId}");

    private static void LogTriggerFailed(ILogger? logger, string rule, Guid id, Exception exception)
    {
        if (logger is not null) TriggerFailed(logger, rule, id, exception);
    }
}
