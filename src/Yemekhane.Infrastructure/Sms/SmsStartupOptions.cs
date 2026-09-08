using Yemekhane.Application.Settings;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Ayarlar ekranindan kaydedilen SMS saglayici bilgisini (SystemSetting) calisma zamani
/// options nesnesine yazar. Program.cs acilista cagirir; test SMS ve kontor sorgusu ayni
/// eslemeyi CANLI ayarlarla kullanir (yeniden baslatma gerekmeden).
/// </summary>
public static class SmsStartupOptions
{
    /// <summary>Ayar eksikse (Http icin adres, Mutlucell icin kullanici adi) hicbir sey degistirmez ve false doner.</summary>
    public static bool Apply(SmsProviderOptions options, SmsProviderSettings settings, string? secret)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        if (SmsProviders.IsMutlucell(settings.Provider))
        {
            if (string.IsNullOrWhiteSpace(settings.Username)) return false;
            options.Provider = SmsProviders.Mutlucell;
            options.Endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? MutlucellGateway.DefaultSendEndpoint : settings.Endpoint.Trim();
            options.AuthType = "None";
            options.Username = settings.Username.Trim();
            options.Sender = settings.Sender;
            options.Secret = secret;
            options.TimeoutSeconds = settings.TimeoutSeconds;
            return true;
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint)) return false;
        options.Provider = SmsProviders.Http;
        options.Endpoint = settings.Endpoint;
        options.AuthType = settings.AuthType;
        options.Username = settings.Username;
        options.Sender = settings.Sender;
        options.Secret = secret;
        options.TimeoutSeconds = settings.TimeoutSeconds;
        return true;
    }

    /// <summary>Kuyruk/JSON alanlarini koruyup saglayici alanlarini kopyalar; canli sinama icin taze nesne.</summary>
    public static SmsProviderOptions Clone(SmsProviderOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new SmsProviderOptions
        {
            Provider = source.Provider, Endpoint = source.Endpoint, AllowHttp = source.AllowHttp,
            AllowPrivateNetworks = source.AllowPrivateNetworks, Method = source.Method, BearerToken = source.BearerToken,
            AuthType = source.AuthType, Username = source.Username, Sender = source.Sender, Secret = source.Secret,
            RecipientProperty = source.RecipientProperty, MessageProperty = source.MessageProperty,
            ProviderMessageIdJsonPath = source.ProviderMessageIdJsonPath, TimeoutSeconds = source.TimeoutSeconds,
            BatchSize = source.BatchSize, MaxAttempts = source.MaxAttempts, InitialRetrySeconds = source.InitialRetrySeconds,
            MaxRetrySeconds = source.MaxRetrySeconds, StaleSendingSeconds = source.StaleSendingSeconds,
            DispatchIntervalSeconds = source.DispatchIntervalSeconds
        };
        foreach (var header in source.Headers) copy.Headers[header.Key] = header.Value;
        foreach (var property in source.AdditionalJsonProperties) copy.AdditionalJsonProperties[property.Key] = property.Value;
        return copy;
    }
}
