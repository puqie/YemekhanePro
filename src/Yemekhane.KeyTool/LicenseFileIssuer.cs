using Yemekhane.Licensing;

namespace Yemekhane.KeyTool;

/// <summary>
/// Makineye kilitli lisans dosyasi uretir.
///
/// <para>
/// Arayuzden AYRI durur cunku bu mantik dogrudan test edilebilir olmalidir: ilk
/// yazildiginda imzalama dali asimetrik moda gore guncellendi ama on kosul kontrolu
/// eski HMAC sirrini istemeye devam etti. Sonuc, anahtar cifti uretmis bir saticinin
/// HIC dosya uretememesiydi -- dugme aciliyor, basinca "once imza sirrini kaydedin"
/// diyordu. Code-behind'da oldugu icin hicbir test bunu goremiyordu.
/// </para>
/// </summary>
public static class LicenseFileIssuer
{
    /// <param name="Content">Diske yazilacak <c>.lic</c> icerigi.</param>
    /// <param name="Key">Uretilen lisans anahtari; satis kaydinda izlenir.</param>
    /// <param name="MachineId">Dosyanin kilitlendigi bilgisayarin kimligi.</param>
    /// <param name="SuggestedFileName">Kaydetme icin onerilen dosya adi.</param>
    public sealed record Result(string Content, string Key, string MachineId, string SuggestedFileName);

    /// <summary>
    /// Lisans dosyasi uretir. Imzalama <paramref name="keyPair"/> varsa OZEL ANAHTARLA
    /// yapilir; yoksa eski HMAC sirrina dusulur (sunucu modu ve daha once satilmis
    /// lisanslar icin korunur). Ikisi de yoksa <c>null</c> doner.
    /// </summary>
    public static Result? Issue(
        IReadOnlyList<string> hashes,
        string customer,
        LicenseKeyPair? keyPair,
        string? secret,
        DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        // ON KOSUL: iki imzalama yolundan BIRI yeter. Yalnizca sirri sormak, asimetrik
        // modu tamamen calismaz kilardi -- tam olarak yasanan hata buydu.
        if (keyPair is null && string.IsNullOrWhiteSpace(secret)) return null;
        if (string.IsNullOrWhiteSpace(customer)) return null;

        var machineId = new HardwareFingerprint([.. hashes]).MachineId;

        StoredLicense license;
        string key;
        if (keyPair is not null)
        {
            key = OfflineLicenseKey.Create(issuedAt, keyPair.PrivateKey);
            var payload = LicenseSignature.BuildPayload(key, [.. hashes], issuedAt, null);
            license = new StoredLicense(key, customer, "Standart", [.. hashes], issuedAt,
                ExpiresAt: null, LastValidatedAt: issuedAt,
                LicenseKeyPairFactory.Sign(payload, keyPair.PrivateKey));
        }
        else
        {
            key = OfflineLicenseKey.Create(issuedAt, secret!);
            license = LicenseIssuer.Issue(key, customer, "Standart", [.. hashes],
                issuedAt, expiresAt: null, secret!);
        }

        return new Result(LicenseFile.Write(license), key, machineId,
            LicenseFile.SuggestFileName(customer, machineId));
    }
}
