using Microsoft.Extensions.Options;
using Yemekhane.Application.Common;

namespace Yemekhane.Infrastructure.Sms;

public sealed class SmsProviderOptionsValidator : IValidateOptions<SmsProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, SmsProviderOptions options)
    {
        if (options.BatchSize is < 1 or > 500 || options.MaxAttempts is < 1 or > 20 ||
            options.InitialRetrySeconds < 1 || options.MaxRetrySeconds < options.InitialRetrySeconds ||
            options.StaleSendingSeconds < 1 || options.DispatchIntervalSeconds < 1)
            return ValidateOptionsResult.Fail("SMS kuyruk ayarları geçersizdir.");
        if (string.IsNullOrWhiteSpace(options.Provider))
            return ValidateOptionsResult.Fail("Sms:Provider yapılandırılmalıdır; varsayılan sahte başarı kullanılmaz.");
        if (options.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Success;
        if (!options.Provider.Equals("Http", StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail("Sms:Provider yalnız Http veya Mock olabilir.");
        try { OutboundEndpointPolicy.ValidateSyntax(options.Endpoint, options.AllowHttp, options.AllowPrivateNetworks); }
        catch (RequestValidationException exception) { return ValidateOptionsResult.Fail(exception.Message); }
        if (string.IsNullOrWhiteSpace(options.Method))
            return ValidateOptionsResult.Fail("Sms:Method zorunludur.");
        if (options.TimeoutSeconds is < 1 or > 300)
            return ValidateOptionsResult.Fail("Sms:TimeoutSeconds 1-300 aralığında olmalıdır.");
        if (string.IsNullOrWhiteSpace(options.RecipientProperty) ||
            string.IsNullOrWhiteSpace(options.MessageProperty) ||
            options.RecipientProperty == options.MessageProperty)
            return ValidateOptionsResult.Fail("SMS JSON alan adları dolu ve birbirinden farklı olmalıdır.");
        if (options.Headers.Any(header => string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value)))
            return ValidateOptionsResult.Fail("Sms:Headers boş ad veya değer içeremez.");

        return ValidateOptionsResult.Success;
    }
}
