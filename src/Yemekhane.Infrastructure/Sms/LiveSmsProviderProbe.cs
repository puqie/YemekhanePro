using Microsoft.Extensions.Options;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Settings;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// "Test SMS gönder" ve "Kontör sorgula": kuyruga girmeden, KAYITLI (canli) ayarlarla dogrudan
/// saglayiciya gider ve ham yaniti oldugu gibi doner. Acilista options'a yazilan degil,
/// SettingsService'teki guncel deger kullanilir; boylece memur ayari kaydedip hemen sinar,
/// yeniden baslatmayi beklemez. Kuyruk gonderimi ise yeniden baslatmayla yeni ayara gecer.
/// </summary>
public sealed class LiveSmsProviderProbe(
    ISettingsService settings,
    IHttpClientFactory httpClientFactory,
    IOptions<SmsProviderOptions> baseOptions,
    TimeProvider timeProvider,
    IAuditService? audit = null) : ISmsProviderProbe
{
    public const string HttpClientName = "sms-probe";

    public async Task<SmsTestResult> SendTestAsync(SmsTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var phone = TurkishMobilePhone.Normalize(request.Phone);
        var (options, configured) = await LiveOptionsAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (!configured)
            return new SmsTestResult(false, options.Provider, phone, null, SmsErrorCategory.Configuration.ToString(), "not_configured",
                "SMS sağlayıcısı yapılandırılmamış: Ayarlar → SMS'te sağlayıcıyı seçip bilgileri kaydedin.", null, now);

        var message = string.IsNullOrWhiteSpace(request.Message)
            ? $"YemekhanePro test mesajı {TimeZoneInfo.ConvertTime(now, Istanbul):dd.MM.yyyy HH:mm}"
            : request.Message.Trim();
        var provider = CreateProvider(options);
        var result = await provider.SendAsync(new SmsSendRequest(phone, message), cancellationToken).ConfigureAwait(false);
        audit?.Record(new AuditEntry("SmsTestSent", "SmsProvider", options.Provider,
            result.IsSuccess ? $"Test SMS gönderildi: {phone}" : $"Test SMS gönderilemedi: {phone} ({result.ErrorCode})",
            After: new { options.Provider, Phone = phone, result.IsSuccess, result.ErrorCode, result.ProviderMessageId }));
        return new SmsTestResult(result.IsSuccess, options.Provider, phone, result.ProviderMessageId,
            result.IsSuccess ? null : result.ErrorCategory.ToString(), result.ErrorCode,
            result.IsSuccess ? null : result.ErrorMessage ?? Describe(result), result.RawResponse, now);
    }

    public async Task<SmsCreditResult> QueryCreditAsync(CancellationToken cancellationToken = default)
    {
        var (options, configured) = await LiveOptionsAsync(cancellationToken).ConfigureAwait(false);
        if (!configured)
            return new SmsCreditResult(false, null, "SMS sağlayıcısı yapılandırılmamış: Ayarlar → SMS'te bilgileri kaydedin.", null);
        if (!SmsProviders.IsMutlucell(options.Provider))
            return new SmsCreditResult(false, null, "Kontör sorgusu yalnızca Mutlucell sağlayıcısında yapılabilir.", null);
        var provider = new MutlucellSmsProvider(httpClientFactory.CreateClient(HttpClientName), Options.Create(options));
        return await provider.QueryCreditAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(SmsProviderOptions Options, bool Configured)> LiveOptionsAsync(CancellationToken cancellationToken)
    {
        var document = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var secret = await settings.GetSecretAsync(SettingsService.SmsSecretKey, cancellationToken).ConfigureAwait(false);
        var options = SmsStartupOptions.Clone(baseOptions.Value);
        var configured = SmsStartupOptions.Apply(options, document.Sms, secret);
        return (options, configured);
    }

    private ISmsProvider CreateProvider(SmsProviderOptions options)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var wrapped = Options.Create(options);
        return SmsProviders.IsMutlucell(options.Provider)
            ? new MutlucellSmsProvider(client, wrapped)
            : new HttpSmsProvider(client, wrapped, new JsonSmsResponseParser(wrapped));
    }

    private static string Describe(SmsSendResult result) => result.ErrorCategory switch
    {
        SmsErrorCategory.Timeout => "Sağlayıcı zamanında yanıt vermedi.",
        SmsErrorCategory.Transport => "Sağlayıcıya bağlanılamadı; internet ve adresi kontrol edin.",
        SmsErrorCategory.Authentication => "Sağlayıcı kimlik bilgilerini reddetti.",
        SmsErrorCategory.RateLimited => "Sağlayıcı çok sık istek uyarısı verdi.",
        SmsErrorCategory.ProviderUnavailable => "Sağlayıcı geçici olarak hizmet vermiyor.",
        SmsErrorCategory.InvalidResponse => "Sağlayıcı beklenmeyen bir yanıt döndürdü.",
        SmsErrorCategory.Configuration => "Sağlayıcı ayarı eksik veya hatalı.",
        _ => "Sağlayıcı isteği reddetti."
    } + (result.HttpStatusCode is { } status ? $" (HTTP {status})" : string.Empty);

    private static readonly TimeZoneInfo Istanbul = FindIstanbul();

    private static TimeZoneInfo FindIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
