using System.Security.Cryptography;
using System.Text;

namespace Yemekhane.Licensing;

/// <param name="PrivateKey">Lisans imzalamak icin gereken anahtar. SATICIDA KALIR.</param>
/// <param name="PublicKey">Lisansi dogrulamak icin yeten anahtar. Kuruluma gomulur.</param>
public sealed record LicenseKeyPair(string PrivateKey, string PublicKey);

/// <summary>
/// ASIMETRIK lisans imzalama (ECDSA P-256).
///
/// <para>
/// Neden gerekli: HMAC ile imzalarken AYNI sir hem imzalar hem dogrular, dolayisiyla
/// sirrin musterinin bilgisayarinda bulunmasi ZORUNLUDUR. Olculdu -- musteri kurulum
/// klasorundeki appsettings.json'i acip sirri okuyabiliyor ve kendine sinirsiz
/// gecerli lisans uretebiliyordu.
/// </para>
/// <para>
/// Asimetrik imzada ozel anahtar SATICIDA kalir; kuruluma yalnizca acik anahtar girer.
/// Acik anahtarla lisans DOGRULANIR ama URETILEMEZ (olculdu: imzalama denemesi
/// CryptographicException ile reddediliyor). Musteri dosyayi okusa bile isine yaramaz.
/// </para>
/// <para>
/// P-256 secildi: .NET'te yerlesiktir, ek paket gerektirmez ve acik anahtar 124
/// karakterdir -- yapilandirma dosyasina rahat sigar.
/// </para>
/// </summary>
public static class LicenseKeyPairFactory
{
    /// <summary>Yeni bir anahtar cifti uretir. Satici bunu BIR KEZ yapar.</summary>
    public static LicenseKeyPair Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new LicenseKeyPair(
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// Ozel anahtarla imzalar. Yalnizca saticinin arac(lar)inda cagrilir.
    /// </summary>
    public static string Sign(string payload, string privateKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(privateKey);

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        return Convert.ToBase64String(
            key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// Acik anahtarla dogrular. Musterinin kurulumunda cagrilir.
    ///
    /// Bozuk anahtar veya imza <c>false</c> doner: cokme yerine "lisans gecersiz"
    /// demek dogrudur, cunku kurcalanmis bir dosya tam olarak bunu uretir.
    /// </summary>
    public static bool Verify(string payload, string signature, string publicKey)
    {
        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(signature)) return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(payload),
                Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verilen degerin acik anahtar olup olmadigi. Satici yanlislikla OZEL anahtari
    /// kuruluma gomerse bu felakettir; kurulum betigi bunu kontrol eder.
    /// </summary>
    public static bool IsPublicKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value), out _);
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
