namespace Yemekhane.Licensing;

/// <summary>
/// Imzali lisans uretir.
///
/// Aktivasyon sunucusu yayina girene kadar lisanslar bu kod yoluyla verilir;
/// sunucu devreye girdiginde ayni imzalama mantigini kullanir, dolayisiyla
/// uretilen lisanslar gecerliligini korur.
///
/// Imza sirri ASLA depoda tutulmaz: sir sizarsa herkes gecerli lisans
/// uretebilir ve koruma anlamini yitirir.
/// </summary>
public static class LicenseIssuer
{
    /// <summary>
    /// Belirtilen makine icin imzali bir lisans uretir.
    /// </summary>
    /// <param name="licenseKey">Musteriye verilen anahtar (orn. YMK-2026-0001).</param>
    /// <param name="customerName">Okul adi; destek gorusmelerinde kullanilir.</param>
    /// <param name="edition">Surum adi (Standart, Kurumsal...).</param>
    /// <param name="fingerprintHashes">
    /// Hedef makinenin donanim parmak izleri. Lisans ekranindaki "Bilgisayar
    /// kimligi" bu degerlerden uretilir; en az biri gereklidir, aksi halde
    /// lisans HER makinede gecerli olurdu.
    /// </param>
    /// <param name="issuedAt">Duzenlenme zamani.</param>
    /// <param name="expiresAt">Bitis zamani; null ise suresiz.</param>
    /// <param name="secret">Imza sirri.</param>
    public static StoredLicense Issue(
        string licenseKey,
        string customerName,
        string edition,
        IReadOnlyList<string> fingerprintHashes,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt,
        string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(edition);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(fingerprintHashes);

        if (fingerprintHashes.Count == 0)
            throw new ArgumentException(
                "En az bir donanım parmak izi gerekir; aksi halde lisans her makinede geçerli olur.",
                nameof(fingerprintHashes));

        if (expiresAt is not null && expiresAt <= issuedAt)
            throw new ArgumentException(
                "Bitiş zamanı düzenlenme zamanından sonra olmalıdır.", nameof(expiresAt));

        var payload = LicenseSignature.BuildPayload(licenseKey, fingerprintHashes, issuedAt, expiresAt);
        var signature = LicenseSignature.Sign(payload, secret);

        return new StoredLicense(
            LicenseKey: licenseKey,
            CustomerName: customerName,
            Edition: edition,
            FingerprintHashes: fingerprintHashes,
            IssuedAt: issuedAt,
            ExpiresAt: expiresAt,
            // Yeni uretilen lisans o an dogrulanmis sayilir; aksi halde
            // cevrimdisi tolerans daha ilk gunden tukenmis gorunurdu.
            LastValidatedAt: issuedAt,
            Signature: signature);
    }
}
