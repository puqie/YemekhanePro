using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.Licensing;

/// <summary>
/// Lisans imzasini uretir ve dogrular.
///
/// Imza, lisansin ANLAMLI alanlarini kapsar: anahtar, parmak izleri, bitis tarihi.
/// Kullanici dosyadaki bitis tarihini ileri alirsa imza tutmaz ve lisans kurcalanmis
/// sayilir. Imza olmadan bu alanlar duz metin gibi degistirilebilirdi.
/// </summary>
public static class LicenseSignature
{
    /// <summary>
    /// Alan ayiricisi. Hash'ler onaltilik, tarihler ISO-8601 oldugundan bu karakter
    /// hicbir alanin icinde gecemez.
    /// </summary>
    private const char Separator = '|';

    /// <summary>Imzalanacak alanlari tek ve KESIN bir metne cevirir.</summary>
    /// <remarks>
    /// Alan sirasi ve ayirici sabittir. Ayirici olmasaydi ("AB" + "C") ile ("A" + "BC")
    /// ayni metni uretir, farkli iki lisans ayni imzayi tasirdi.
    /// </remarks>
    public static string BuildPayload(
        string licenseKey,
        IReadOnlyList<string> fingerprintHashes,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(fingerprintHashes);

        var builder = new StringBuilder();
        builder.Append(licenseKey).Append(Separator);

        // Bilesen SAYISI da imzaya girer: aksi halde bos bir hash silinerek 2/3
        // kuralinin karsilastirdigi dizi kaydirilabilirdi.
        builder.Append(fingerprintHashes.Count).Append(Separator);
        foreach (var hash in fingerprintHashes) builder.Append(hash).Append(Separator);

        builder.Append(issuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(Separator);
        builder.Append(expiresAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "SURESIZ");
        return builder.ToString();
    }

    /// <summary>Verilen gizli anahtarla imza uretir. Sunucu tarafinda kullanilir.</summary>
    public static string Sign(string payload, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Lisansi ACIK ANAHTARLA dogrular (asimetrik).
    ///
    /// Tercih edilen yoldur: acik anahtar lisansi dogrular ama URETEMEZ, dolayisiyla
    /// musterinin kurulumundan okunmasi bir ise yaramaz. HMAC yolunda ayni sir hem
    /// imzalar hem dogrular ve musteri onu okuyup kendine lisans uretebilirdi.
    /// </summary>
    public static bool VerifyWithPublicKey(StoredLicense license, string publicKey)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(license.Signature)) return false;

        return LicenseKeyPairFactory.Verify(
            BuildPayload(license.LicenseKey, license.FingerprintHashes, license.IssuedAt, license.ExpiresAt),
            license.Signature, publicKey);
    }

    /// <summary>Lisans kaydinin imzasinin gecerli olup olmadigi (HMAC).</summary>
    public static bool Verify(StoredLicense license, string secret)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(license.Signature)) return false;

        var expected = Sign(
            BuildPayload(license.LicenseKey, license.FingerprintHashes, license.IssuedAt, license.ExpiresAt),
            secret);

        // Sabit zamanli karsilastirma: normal string esitligi ilk farkli karakterde
        // durur ve gecen sureden dogru imza karakter karakter tahmin edilebilir.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(license.Signature));
    }
}
