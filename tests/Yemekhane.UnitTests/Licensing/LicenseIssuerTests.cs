using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Lisans URETME araci.
///
/// Uretilen lisans, uygulamanin dogrulayicisi tarafindan KABUL EDILMELIDIR.
/// Aksi halde arac yalnizca dosya uretir ve musteri "lisansim calismiyor"
/// diye geri doner -- bu yuzden testler uretimi degil, uygulamanin verdigi
/// KARARI dogrular.
/// </summary>
public sealed class LicenseIssuerTests
{
    private const string Secret = "test-imza-sirri-en-az-otuz-iki-karakter-olmali";

    private static readonly string[] Fingerprints =
        ["AA11BB22", "CC33DD44", "EE55FF66"];

    [Fact]
    public void AnIssuedLicenseIsAcceptedByTheApplication()
    {
        var license = LicenseIssuer.Issue(
            licenseKey: "YMK-0001-TEST",
            customerName: "Atatürk Anadolu Lisesi",
            edition: "Standart",
            fingerprintHashes: Fingerprints,
            issuedAt: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddYears(1),
            secret: Secret);

        Assert.True(LicenseSignature.Verify(license, Secret),
            "Üretilen lisansın imzası uygulamaca doğrulanamadı; müşteri açamaz.");
    }

    [Fact]
    public void ALicenseSignedWithAnotherSecretIsRejected()
    {
        // Sir sizmadikca kimse gecerli lisans uretememelidir.
        var license = LicenseIssuer.Issue(
            "YMK-0002-TEST", "Okul", "Standart", Fingerprints,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1),
            secret: "baska-bir-sir-en-az-otuz-iki-karakter-olmali");

        Assert.False(LicenseSignature.Verify(license, Secret),
            "Farklı sırla imzalanan lisans kabul edildi; lisans koruması anlamsız.");
    }

    [Fact]
    public void TamperingWithAnIssuedLicenseBreaksItsSignature()
    {
        var license = LicenseIssuer.Issue(
            "YMK-0003-TEST", "Okul", "Standart", Fingerprints,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), Secret);

        // Musteri suresini uzatmaya calisirsa imza TUTMAMALIDIR.
        var extended = license with { ExpiresAt = DateTimeOffset.UtcNow.AddYears(10) };

        Assert.False(LicenseSignature.Verify(extended, Secret),
            "Süresi elle uzatılan lisans hâlâ geçerli sayılıyor.");
    }

    [Fact]
    public void APerpetualLicenseCanBeIssued()
    {
        // Suresiz lisans (ExpiresAt null) da imzalanabilmelidir.
        var license = LicenseIssuer.Issue(
            "YMK-0004-TEST", "Okul", "Kurumsal", Fingerprints,
            DateTimeOffset.UtcNow, expiresAt: null, secret: Secret);

        Assert.Null(license.ExpiresAt);
        Assert.True(LicenseSignature.Verify(license, Secret));
    }

    [Fact]
    public void TheIssuedLicenseCarriesTheDetailsTheSupportDeskNeeds()
    {
        var issued = DateTimeOffset.UtcNow;
        var license = LicenseIssuer.Issue(
            "YMK-0005-TEST", "Şehit Öğretmen İlkokulu", "Standart",
            Fingerprints, issued, issued.AddYears(1), Secret);

        Assert.Equal("YMK-0005-TEST", license.LicenseKey);
        Assert.Equal("Şehit Öğretmen İlkokulu", license.CustomerName);
        Assert.Equal("Standart", license.Edition);
        Assert.Equal(Fingerprints, license.FingerprintHashes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankLicenseKeyIsRefused(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => LicenseIssuer.Issue(
            key, "Okul", "Standart", Fingerprints,
            DateTimeOffset.UtcNow, null, Secret));
    }

    [Fact]
    public void ABlankSecretIsRefusedSoAnUnsignableLicenseIsNeverProduced()
    {
        Assert.ThrowsAny<ArgumentException>(() => LicenseIssuer.Issue(
            "YMK-0006-TEST", "Okul", "Standart", Fingerprints,
            DateTimeOffset.UtcNow, null, secret: ""));
    }

    [Fact]
    public void FingerprintsAreRequiredSoALicenseIsNotValidOnEveryMachine()
    {
        Assert.ThrowsAny<ArgumentException>(() => LicenseIssuer.Issue(
            "YMK-0007-TEST", "Okul", "Standart", fingerprintHashes: [],
            DateTimeOffset.UtcNow, null, Secret));
    }
}
