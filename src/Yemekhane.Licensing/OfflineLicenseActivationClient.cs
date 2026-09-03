namespace Yemekhane.Licensing;

/// <summary>
/// SUNUCUSUZ aktivasyon: anahtar yerel olarak dogrulanir, lisans yerelde uretilir.
///
/// <para>
/// Neden var: 7/24 aktivasyon sunucusu barindirmak kucuk bir okul yaziliminda
/// gereksiz aylik maliyettir. Anahtarin imzasi (<see cref="OfflineLicenseKey"/>)
/// zaten "bu anahtari satici verdi" sorusunu yanitlar; lisansi makineye baglayan
/// donanim parmak izi de yereldir. Geriye sunucunun tek basina yapabildigi is
/// kalir: UZAKTAN IPTAL. Bu mod onu bilerek feda eder.
/// </para>
/// <para>
/// <see cref="ILicenseActivationClient"/> arayuzu korunur: masaustu tarafi hicbir sey
/// bilmez, yalnizca yapilandirmaya gore bu uygulama ya da HTTP olani secilir.
/// </para>
/// </summary>
public sealed class OfflineLicenseActivationClient(
    string signingSecret,
    string customerName = "Yerel Kurulum",
    string edition = "Standart",
    TimeProvider? timeProvider = null) : ILicenseActivationClient
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<ActivationResult> ActivateAsync(
        string licenseKey, HardwareFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        if (!OfflineLicenseKey.Verify(licenseKey, signingSecret))
            return Task.FromResult(new ActivationResult(false, null,
                "Lisans anahtari gecersiz. Lutfen anahtari kontrol edin veya saticinizla iletisime gecin."));

        // Donanim parmak izi olmadan lisans HER makinede gecerli olurdu; bu kontrol
        // LicenseService'te de var ama burada da yapilir: bu sinif tek basina da dogru olmalidir.
        if (!fingerprint.IsUsable)
            return Task.FromResult(new ActivationResult(false, null,
                "Bilgisayarin donanim bilgisi okunamadi. Lisans bu bilgisayara baglanamiyor."));

        var now = clock.GetUtcNow();
        var license = LicenseIssuer.Issue(
            OfflineLicenseKey.Normalize(licenseKey), customerName, edition,
            [.. fingerprint.Hashes], now,
            // SURESIZ: sunucusuz modda suresi dolan lisansi yenileyecek bir merci yoktur.
            // Yillik abonelik satilacaksa sunucu modu kullanilmalidir.
            expiresAt: null,
            signingSecret);

        return Task.FromResult(new ActivationResult(true, license, null));
    }

    /// <summary>
    /// Sunucu olmadigi icin dogrulama her zaman "ulasilamadi" doner.
    ///
    /// Bu, <see cref="LicenseService"/> icinde YEREL karara duser -- imza ve donanim
    /// kontrolu yine yapilir. "Valid" donmek yanlis olurdu: cevrimdisi sayaci
    /// gercekte hic dogrulanmadigi halde surekli sifirlanir, sunucu moduna gecildiginde
    /// tolerans mantigi bozulurdu.
    /// </summary>
    public Task<ValidationResult> ValidateAsync(
        StoredLicense license, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ValidationResult(ValidationOutcome.Unreachable));
}
