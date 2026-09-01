using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Runtime.Versioning;
using Yemekhane.Licensing;

namespace Yemekhane.Desktop.Services;

/// <summary>Lisans kapisinin karari.</summary>
/// <param name="Allowed">Uygulamanin acilmasina izin verilip verilmedigi.</param>
/// <param name="Check">Karari veren denetim sonucu.</param>
public sealed record LicenseGateDecision(bool Allowed, LicenseCheck Check);

/// <summary>
/// Acilista lisansi denetler.
///
/// Bu kontrol YEREL API BASLAMADAN ONCE calisir: lisanssiz bir kurulumda veritabani,
/// turnike baglantilari ve zamanlayici servisler hic ayaga kalkmamalidir.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LicenseGate
{
    /// <summary>
    /// Yapilandirmadan lisans servisini kurar.
    /// </summary>
    /// <param name="configuration">Uygulama yapilandirmasi.</param>
    /// <param name="dataDirectory">
    /// Lisans dosyasinin klasoru. Disaridan verilir: Yemekhane.Licensing hicbir projeye
    /// referans veremedigi icin ApplicationDataPath'i kendisi cagiramaz.
    /// </param>
    public static ILicenseService CreateService(IConfiguration configuration, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var activationUri = configuration["Licensing:ActivationUri"];
        var signingSecret = configuration["Licensing:SigningSecret"];

        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new InvalidOperationException(
                "Licensing:SigningSecret yapılandırması bulunamadı. Lisans doğrulaması bu değer olmadan yapılamaz.");

        if (!Uri.TryCreate(activationUri, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Licensing:ActivationUri mutlak bir URI olmalıdır.");

        // Kisa zaman asimi: lisans sunucusu yanit vermiyorsa kullanici acilisi dakikalarca
        // beklememelidir. Zaman asiminda cevrimdisi toleransa dusulur.
        var httpClient = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };

        return new LicenseService(
            new WindowsLicenseStore(dataDirectory),
            new WindowsHardwareFingerprintReader(),
            new HttpLicenseActivationClient(httpClient),
            TimeProvider.System,
            signingSecret);
    }

    /// <summary>
    /// Lisansi denetler. Once yerel karar verilir; gecerliyse sunucuyla tazelenmeye
    /// CALISILIR ama basarisizligi acilisi engellemez.
    /// </summary>
    public static async Task<LicenseGateDecision> EvaluateAsync(
        ILicenseService licenseService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseService);

        var local = licenseService.Check();

        // Yerel olarak zaten gecersizse sunucuya sormanin anlami yok: kullanici
        // aktivasyon ekranina duser ve sorunu orada cozer.
        if (!local.IsValid) return new(false, local);

        // Gecerliyse sunucudan tazelenir. Ag yoksa Unreachable doner ve yerel karar
        // korunur - okulun interneti kesildiginde yemek dagitimi DURMAMALIDIR.
        var validated = await licenseService.ValidateAsync(cancellationToken).ConfigureAwait(false);
        return new(validated.IsValid, validated);
    }
}
