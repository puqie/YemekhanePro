using Microsoft.Extensions.Options;
using Yemekhane.Application.Sms;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Kuyrugun kullandigi tek <see cref="ISmsProvider"/>: gonderim aninda <c>Sms:Provider</c>'a
/// bakip Mutlucell ya da genel HTTP saglayicisina yonlendirir. Secim acilista degil her
/// gonderimde okunur cunku kalici ayarlar (SettingsService) acilista options nesnesine
/// sonradan yazilir.
/// </summary>
public sealed class SmsProviderRouter(
    IOptions<SmsProviderOptions> options,
    HttpSmsProvider http,
    MutlucellSmsProvider mutlucell) : ISmsProvider
{
    public Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default) =>
        Select(options.Value.Provider).SendAsync(request, cancellationToken);

    public ISmsProvider Select(string? provider) =>
        SmsProviders.IsMutlucell(provider) ? mutlucell : http;
}

/// <summary>Ayarlar ekrani ve options'ta kullanilan saglayici adlari.</summary>
public static class SmsProviders
{
    public const string Http = "Http";
    public const string Mutlucell = "Mutlucell";
    public const string Mock = "Mock";

    /// <summary>Kullanici ekrandan secebilecekleri; Mock yalnizca gelistirme icindir.</summary>
    public static readonly IReadOnlyList<string> Selectable = [Http, Mutlucell];

    public static bool IsMutlucell(string? provider) => string.Equals(provider, Mutlucell, StringComparison.OrdinalIgnoreCase);
    public static bool IsHttp(string? provider) => string.Equals(provider, Http, StringComparison.OrdinalIgnoreCase);
    public static bool IsMock(string? provider) => string.Equals(provider, Mock, StringComparison.OrdinalIgnoreCase);
}
