using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class LicenseSignatureTests
{
    private const string Secret = "sunucu-gizli-anahtari";
    private static readonly DateTimeOffset IssuedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] Hashes =
        [FingerprintHasher.Hash("A"), FingerprintHasher.Hash("B"), FingerprintHasher.Hash("C")];

    private static StoredLicense Signed(
        string key = "ANAHTAR-1", string[]? hashes = null, DateTimeOffset? expiresAt = null)
    {
        hashes ??= Hashes;
        var expiry = expiresAt ?? ExpiresAt;
        return new StoredLicense(key, "Okul", "Standart", hashes, IssuedAt, expiry, IssuedAt,
            LicenseSignature.Sign(LicenseSignature.BuildPayload(key, hashes, IssuedAt, expiry), Secret));
    }

    [Fact]
    public void AProperlySignedLicenseVerifies()
    {
        Assert.True(LicenseSignature.Verify(Signed(), Secret));
    }

    [Fact]
    public void ExtendingTheExpiryDateBreaksTheSignature()
    {
        Assert.False(LicenseSignature.Verify(Signed() with { ExpiresAt = ExpiresAt.AddYears(10) }, Secret));
    }

    [Fact]
    public void SwappingTheFingerprintsBreaksTheSignature()
    {
        // Lisansi baska bir makineye tasimak icin parmak izlerini degistirmek
        // yeterli olsaydi makineye baglama fikri anlamsiz olurdu.
        var other = new[] { FingerprintHasher.Hash("X"), FingerprintHasher.Hash("Y"), FingerprintHasher.Hash("Z") };

        Assert.False(LicenseSignature.Verify(Signed() with { FingerprintHashes = other }, Secret));
    }

    [Fact]
    public void ChangingTheLicenseKeyBreaksTheSignature()
    {
        Assert.False(LicenseSignature.Verify(Signed() with { LicenseKey = "BASKA-ANAHTAR" }, Secret));
    }

    [Fact]
    public void ASignatureFromADifferentSecretIsRejected()
    {
        // Istemci ikili dosyasi yamalansa bile, saldirgan ozel anahtari bilmeden
        // gecerli imza uretemez. Korumanin gercek kaynagi budur.
        Assert.False(LicenseSignature.Verify(Signed(), "sahte-anahtar"));
    }

    [Fact]
    public void AnEmptySignatureIsRejected()
    {
        Assert.False(LicenseSignature.Verify(Signed() with { Signature = string.Empty }, Secret));
    }

    [Fact]
    public void RemovingAFingerprintComponentBreaksTheSignature()
    {
        // Bilesen sayisi imzaya dahildir: bos bir hash silinerek 2/3 kuralinin
        // karsilastirdigi dizi kaydirilamaz.
        Assert.False(LicenseSignature.Verify(
            Signed() with { FingerprintHashes = [Hashes[0], Hashes[1]] }, Secret));
    }

    [Fact]
    public void TwoDifferentLicensesNeverShareASignature()
    {
        // Ayirici olmasaydi ("AB" + "C") ile ("A" + "BC") ayni metni uretir ve
        // farkli iki lisans ayni imzayi tasirdi.
        var first = LicenseSignature.BuildPayload("AB", ["C"], IssuedAt, ExpiresAt);
        var second = LicenseSignature.BuildPayload("A", ["BC"], IssuedAt, ExpiresAt);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void APerpetualLicenseSignsAndVerifies()
    {
        var perpetual = Signed(expiresAt: null);

        Assert.True(LicenseSignature.Verify(
            perpetual with
            {
                ExpiresAt = null,
                Signature = LicenseSignature.Sign(
                    LicenseSignature.BuildPayload(perpetual.LicenseKey, Hashes, IssuedAt, null), Secret)
            },
            Secret));
    }
}
