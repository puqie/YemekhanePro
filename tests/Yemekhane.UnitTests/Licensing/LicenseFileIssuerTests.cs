using Yemekhane.KeyTool;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Lisans dosyasi uretimi: her iki imzalama yolu da calismalidir.
///
/// Ilk surumde on kosul yalnizca HMAC sirrini soruyordu; anahtar cifti uretmis bir
/// satici "Dosya uret"e bastiginda "once imza sirrini kaydedin" hatasi aliyor ve
/// HIC dosya uretemiyordu. Arayuzun icine gomulu oldugu icin hicbir test goremedi.
/// </summary>
public sealed class LicenseFileIssuerTests
{
    /// <summary>
    /// ASIL REGRESYON: anahtar cifti VAR, HMAC sirri YOK -- sunucusuz modun normal hali.
    /// Bu tutmazsa satici tek bir lisans dosyasi bile uretemez.
    /// </summary>
    [Fact]
    public void AnahtarCiftiVarkenSirOlmadanDosyaUretilir()
    {
        var pair = LicenseKeyPairFactory.Create();

        var result = LicenseFileIssuer.Issue(Hashes("A"), "Ornek Okulu", pair,
            secret: null, DateTimeOffset.Now);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Content);
        Assert.EndsWith(".lic", result.SuggestedFileName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Uretilen dosya, kuruluma gomulu ACIK anahtarla dogrulanabilmelidir --
    /// aksi halde musteri gecersiz dosya alirdi.
    /// </summary>
    [Fact]
    public void UretilenDosyaAcikAnahtarlaDogrulanir()
    {
        var pair = LicenseKeyPairFactory.Create();
        var fingerprint = new HardwareFingerprint([.. Hashes("A")]);

        var result = LicenseFileIssuer.Issue(Hashes("A"), "Ornek Okulu", pair, null, DateTimeOffset.Now)!;

        var service = new LicenseService(new MemoryStore(), new FixedReader(fingerprint),
            new OfflineLicenseActivationClient("kullanilmayan-hmac-sirri-32-bayt-abc"),
            TimeProvider.System, "kullanilmayan-hmac-sirri-32-bayt-abc",
            enforceOfflineGracePeriod: false, publicKey: pair.PublicKey);

        Assert.Equal(LicenseStatus.Valid, service.ImportFile(result.Content).Status);
    }

    /// <summary>Dosya BASKA makinede calismamalidir: kopyalayarak cogaltmayi engeller.</summary>
    [Fact]
    public void UretilenDosyaBaskaMakinedeCalismaz()
    {
        var pair = LicenseKeyPairFactory.Create();
        var result = LicenseFileIssuer.Issue(Hashes("A"), "Ornek Okulu", pair, null, DateTimeOffset.Now)!;

        var service = new LicenseService(new MemoryStore(),
            new FixedReader(new HardwareFingerprint([.. Hashes("B")])),
            new OfflineLicenseActivationClient("kullanilmayan-hmac-sirri-32-bayt-abc"),
            TimeProvider.System, "kullanilmayan-hmac-sirri-32-bayt-abc",
            enforceOfflineGracePeriod: false, publicKey: pair.PublicKey);

        Assert.Equal(LicenseStatus.WrongMachine, service.ImportFile(result.Content).Status);
    }

    /// <summary>Eski HMAC yolu korunur: sunucu modu ve daha once satilmis lisanslar.</summary>
    [Fact]
    public void AnahtarCiftiYokkenHmacSirriylaUretilir()
    {
        const string secret = "eski-hmac-sirri-en-az-32-bayt-1234567890";

        var result = LicenseFileIssuer.Issue(Hashes("A"), "Ornek Okulu",
            keyPair: null, secret, DateTimeOffset.Now);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Content);
    }

    /// <summary>Imzalama yolu YOKSA uretilmemelidir: imzasiz lisans zaten reddedilirdi.</summary>
    [Fact]
    public void HicbirImzaYoluYoksaUretilmez() =>
        Assert.Null(LicenseFileIssuer.Issue(Hashes("A"), "Ornek Okulu", null, null, DateTimeOffset.Now));

    /// <summary>
    /// Musteri adi bos gecilememelidir: sunucusuz modda kime ne sattiginizi
    /// baska hicbir kayit bilmez.
    /// </summary>
    [Fact]
    public void MusteriAdiBossaUretilmez()
    {
        var pair = LicenseKeyPairFactory.Create();

        Assert.Null(LicenseFileIssuer.Issue(Hashes("A"), "   ", pair, null, DateTimeOffset.Now));
    }

    /// <summary>Makine kimligi, musterinin ekraninda yazan ile ayni olmalidir.</summary>
    [Fact]
    public void MakineKimligiParmakIziyleAynidir()
    {
        var pair = LicenseKeyPairFactory.Create();
        var expected = new HardwareFingerprint([.. Hashes("A")]).MachineId;

        var result = LicenseFileIssuer.Issue(Hashes("A"), "Ornek Okulu", pair, null, DateTimeOffset.Now)!;

        Assert.Equal(expected, result.MachineId);
    }

    private static string[] Hashes(string id) =>
        [FingerprintHasher.Hash(id + "-anakart"), FingerprintHasher.Hash(id + "-disk"),
         FingerprintHasher.Hash(id + "-guid")];

    private sealed class FixedReader(HardwareFingerprint fingerprint) : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => fingerprint;
    }

    private sealed class MemoryStore : ILicenseStore
    {
        private StoredLicense? saved;
        public StoredLicense? Load() => saved;
        public void Save(StoredLicense license) => saved = license;
        public void Clear() => saved = null;
    }
}
