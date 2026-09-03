using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// ASIMETRIK IMZALAMA: kurulumdan okunan anahtar lisans URETEMEZ.
///
/// HMAC ile imzalarken ayni sir hem imzalar hem dogrular, dolayisiyla sirrin
/// musterinin bilgisayarinda bulunmasi zorunluydu. Olculdu: musteri kurulum
/// klasorundeki appsettings.json'i acip sirri okuyup kendine sinirsiz gecerli
/// lisans uretebiliyordu. Asimetrik imzada bu mumkun degildir.
/// </summary>
public sealed class AsymmetricLicenseTests
{
    /// <summary>
    /// ASIL KORUMA: kurulumdaki acik anahtar dogrular ama IMZALAYAMAZ.
    /// Bu tutmazsa asimetrik imzalamanin tamami anlamsizdir.
    /// </summary>
    [Fact]
    public void AcikAnahtarlaLisansUretilemez()
    {
        var pair = LicenseKeyPairFactory.Create();
        var machine = Fingerprint("A");

        // Satici ozel anahtarla uretir.
        var license = Issue(machine, pair.PrivateKey);
        Assert.True(LicenseSignature.VerifyWithPublicKey(license, pair.PublicKey));

        // Musteri kurulumdan ACIK anahtari okur ve imzalamaya calisir.
        Assert.ThrowsAny<Exception>(() =>
            LicenseKeyPairFactory.Sign("uydurma yuk", pair.PublicKey));
    }

    /// <summary>
    /// Musteri kendi urettigi anahtar ciftiyle lisans imzalarsa REDDEDILMELIDIR:
    /// aksi halde herkes kendine lisans yazardi.
    /// </summary>
    [Fact]
    public void BaskaAnahtarlaImzalananLisansReddedilir()
    {
        var seller = LicenseKeyPairFactory.Create();
        var attacker = LicenseKeyPairFactory.Create();

        var forged = Issue(Fingerprint("A"), attacker.PrivateKey);

        Assert.False(LicenseSignature.VerifyWithPublicKey(forged, seller.PublicKey));
    }

    /// <summary>
    /// Uctan uca: acik anahtarli kurulumda lisans dosyasi yuklenir ve calisir,
    /// ama BASKA makinede calismaz.
    /// </summary>
    [Fact]
    public void AcikAnahtarliKurulumdaDosyaCalisirAmaTasinamaz()
    {
        var pair = LicenseKeyPairFactory.Create();
        var machineA = Fingerprint("A");
        var machineB = Fingerprint("B");
        var content = LicenseFile.Write(Issue(machineA, pair.PrivateKey));

        Assert.Equal(LicenseStatus.Valid, Service(machineA, pair.PublicKey).ImportFile(content).Status);
        Assert.Equal(LicenseStatus.WrongMachine, Service(machineB, pair.PublicKey).ImportFile(content).Status);
    }

    /// <summary>
    /// Kurcalanan lisans acik anahtarla da yakalanir: bitis tarihini uzatmak imzayi bozar.
    /// </summary>
    [Fact]
    public void KurcalanmisLisansAcikAnahtarlaYakalanir()
    {
        var pair = LicenseKeyPairFactory.Create();
        var machine = Fingerprint("A");
        var tampered = Issue(machine, pair.PrivateKey) with { ExpiresAt = DateTimeOffset.UtcNow.AddYears(10) };

        Assert.Equal(LicenseStatus.Tampered,
            Service(machine, pair.PublicKey).ImportFile(LicenseFile.Write(tampered)).Status);
    }

    /// <summary>
    /// Acik anahtar YOKSA eski HMAC yoluna dusulur: sunucu modu ve daha once
    /// satilmis lisanslar kirilmamalidir.
    /// </summary>
    [Fact]
    public void AcikAnahtarYoksaHmacYoluCalismayaDevamEder()
    {
        const string secret = "eski-hmac-sirri-en-az-32-bayt-1234567890";
        var machine = Fingerprint("A");
        var license = LicenseIssuer.Issue("YMK-2026-TEST-TEST", "Okul", "Standart",
            [.. machine.Hashes], DateTimeOffset.UtcNow, null, secret);

        var service = new LicenseService(new MemoryStore(), new FixedReader(machine),
            new OfflineLicenseActivationClient(secret), TimeProvider.System, secret,
            enforceOfflineGracePeriod: false, publicKey: null);

        Assert.Equal(LicenseStatus.Valid, service.ImportFile(LicenseFile.Write(license)).Status);
    }

    /// <summary>
    /// Satici yanlislikla OZEL anahtari kuruluma gomerse felakettir; kurulum betigi
    /// bunu ayirt edebilmelidir.
    /// </summary>
    [Fact]
    public void OzelVeAcikAnahtarAyirtEdilir()
    {
        var pair = LicenseKeyPairFactory.Create();

        Assert.True(LicenseKeyPairFactory.IsPublicKey(pair.PublicKey));
        Assert.False(LicenseKeyPairFactory.IsPublicKey(pair.PrivateKey));
        Assert.False(LicenseKeyPairFactory.IsPublicKey("rastgele metin"));
        Assert.False(LicenseKeyPairFactory.IsPublicKey(null));
    }

    [Fact]
    public void HerCiftFarklidir()
    {
        var first = LicenseKeyPairFactory.Create();
        var second = LicenseKeyPairFactory.Create();

        Assert.NotEqual(first.PrivateKey, second.PrivateKey);
        Assert.NotEqual(first.PublicKey, second.PublicKey);
    }

    private static StoredLicense Issue(HardwareFingerprint fingerprint, string privateKey)
    {
        // TEK zaman damgasi: imza IssuedAt'i kapsar, dolayisiyla imzalanan zaman ile
        // kaydedilen zaman BIREBIR ayni olmalidir. Iki ayri UtcNow cagrisi mikrosaniyelik
        // fark yaratir ve imza gecersiz olur -- ilk denemede tam bu yasandi.
        var issuedAt = DateTimeOffset.UtcNow;
        var payload = LicenseSignature.BuildPayload("YMK-2026-TEST-TEST",
            [.. fingerprint.Hashes], issuedAt, null);
        return new StoredLicense("YMK-2026-TEST-TEST", "Okul", "Standart",
            [.. fingerprint.Hashes], issuedAt, null, issuedAt,
            LicenseKeyPairFactory.Sign(payload, privateKey));
    }

    private static HardwareFingerprint Fingerprint(string id) =>
        new([FingerprintHasher.Hash(id + "-anakart"), FingerprintHasher.Hash(id + "-disk"),
             FingerprintHasher.Hash(id + "-guid")]);

    private static LicenseService Service(HardwareFingerprint fingerprint, string publicKey) =>
        new(new MemoryStore(), new FixedReader(fingerprint),
            new OfflineLicenseActivationClient("kullanilmayan-hmac-sirri-32-bayt-abc"),
            TimeProvider.System, "kullanilmayan-hmac-sirri-32-bayt-abc",
            enforceOfflineGracePeriod: false, publicKey: publicKey);

    private sealed class FixedReader(HardwareFingerprint fingerprint) : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => fingerprint;
    }

    private sealed class MemoryStore : ILicenseStore
    {
        public StoredLicense? Saved { get; set; }
        public StoredLicense? Load() => Saved;
        public void Save(StoredLicense license) => Saved = license;
        public void Clear() => Saved = null;
    }
}
