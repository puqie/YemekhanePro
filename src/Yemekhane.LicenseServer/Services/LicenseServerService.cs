using Microsoft.EntityFrameworkCore;
using Yemekhane.Licensing;
using Yemekhane.LicenseServer.Data;

namespace Yemekhane.LicenseServer.Services;

/// <summary>Aktivasyon sonucu; denetleyici bunu HTTP durum koduna cevirir.</summary>
public enum ActivateOutcome
{
    /// <summary>200: lisans bu makineye baglandi.</summary>
    Activated,
    /// <summary>404: boyle bir anahtar yok.</summary>
    NotFound,
    /// <summary>409: lisans BASKA bir makinede aktif.</summary>
    AlreadyBound,
    /// <summary>410: lisans iptal edilmis.</summary>
    Revoked,
    /// <summary>410: abonelik suresi dolmus; yenilenmeden aktive edilemez.</summary>
    Expired
}

public sealed record ActivateReply(ActivateOutcome Outcome, LicenseRecord? License, string? Signature);
public sealed record ValidateReply(bool Valid, bool Revoked, DateTimeOffset? ExpiresAt, string? Signature);

/// <summary>
/// Lisans sunucusunun is mantigi. Masaustundeki HttpLicenseActivationClient'in
/// bekledigi sozlesmeyi birebir uygular.
/// </summary>
public sealed class LicenseServerService(LicenseDbContext db, TimeProvider clock, string signingSecret)
{
    /// <summary>
    /// Bu makineyi verilen anahtarla aktive eder.
    ///
    /// AYNI makine tekrar aktive edilebilir (onarim, yeniden kurulum): parmak izleri
    /// eslesiyorsa lisans yeniden imzalanip dondurulur. Aksi halde bilgisayarini
    /// formatlayan musteri destege mahkum kalirdi.
    /// </summary>
    public async Task<ActivateReply> ActivateAsync(
        string licenseKey, IReadOnlyList<string> fingerprints, CancellationToken cancellationToken)
    {
        var key = LicenseKeyGenerator.Normalize(licenseKey);
        var license = await db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, cancellationToken);
        if (license is null) return new(ActivateOutcome.NotFound, null, null);
        if (license.IsRevoked) return new(ActivateOutcome.Revoked, null, null);

        var now = clock.GetUtcNow();
        if (license.ExpiresAt is { } expires && expires <= now)
            return new(ActivateOutcome.Expired, null, null);

        var incoming = Join(fingerprints);
        if (license.FingerprintHashes is { Length: > 0 } bound && bound != incoming)
            return new(ActivateOutcome.AlreadyBound, null, null);

        if (license.ActivatedAt is null)
        {
            license.ActivatedAt = now;
            license.FingerprintHashes = incoming;
        }
        license.LastValidatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        // IssuedAt olarak ilk aktivasyon ani kullanilir; imza bu degeri kapsar ve
        // masaustu ayni degeri saklar, yoksa imza dogrulamasi tutmaz.
        var issuedAt = license.ActivatedAt ?? now;
        var signature = LicenseSignature.Sign(
            LicenseSignature.BuildPayload(license.LicenseKey, fingerprints, issuedAt, license.ExpiresAt),
            signingSecret);
        return new(ActivateOutcome.Activated, license, signature);
    }

    /// <summary>
    /// Kayitli lisansin hala gecerli olup olmadigini soyler.
    ///
    /// Suresi DOLMUS lisans icin "iptal" DENMEZ: masaustu zaten bitis tarihini kendi
    /// kontrol eder ve kullaniciya "aboneligin bitti" der. Buraya 410 donmek
    /// "satici iptal etti" mesajini gosterirdi ki yanlis olurdu.
    /// </summary>
    public async Task<ValidateReply> ValidateAsync(
        string licenseKey, IReadOnlyList<string> fingerprints, CancellationToken cancellationToken)
    {
        var key = LicenseKeyGenerator.Normalize(licenseKey);
        var license = await db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, cancellationToken);

        // Bilinmeyen anahtar da IPTAL sayilir: veritabanindan silinmis bir lisans
        // sahada sonsuza kadar calismaya devam etmemelidir.
        if (license is null || license.IsRevoked) return new(false, true, null, null);

        var incoming = Join(fingerprints);
        if (license.FingerprintHashes is { Length: > 0 } bound && bound != incoming)
            return new(false, true, null, null);

        var now = clock.GetUtcNow();
        license.LastValidatedAt = now;
        license.ValidationCount++;
        await db.SaveChangesAsync(cancellationToken);

        var issuedAt = license.ActivatedAt ?? license.CreatedAt;
        var signature = LicenseSignature.Sign(
            LicenseSignature.BuildPayload(license.LicenseKey, fingerprints, issuedAt, license.ExpiresAt),
            signingSecret);
        return new(true, false, license.ExpiresAt, signature);
    }

    /// <summary>Parmak izi listesini karsilastirilabilir tek metne cevirir.</summary>
    private static string Join(IReadOnlyList<string> fingerprints) =>
        string.Join('|', fingerprints.Select(x => x.Trim().ToUpperInvariant()));
}
