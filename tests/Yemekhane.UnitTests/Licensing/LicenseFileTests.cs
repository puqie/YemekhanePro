using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// MAKINEYE KILITLI LISANS DOSYASI.
///
/// Sunucusuz modda "bu anahtar daha once kullanildi mi" sorusunu soracak merci
/// yoktur; ayni anahtar ikinci, ucuncu bilgisayarda da aktive edilebilirdi.
/// Lisans DOSYASI ise uretilirken hedef makineye kilitlenir: kopyalanabilir ama
/// baska makinede matematiksel olarak calismaz.
/// </summary>
public sealed class LicenseFileTests
{
    private const string Secret = "dosya-testi-sirri-en-az-32-bayt-1234567890";

    /// <summary>
    /// ASIL KORUMA: dosya baska bilgisayara tasinirsa REDDEDILIR.
    /// Bu tutmazsa tek bir lisans dosyasi tum okullara dagitilabilirdi.
    /// </summary>
    [Fact]
    public void DosyaBaskaBilgisayardaCalismaz()
    {
        var machineA = Fingerprint("A");
        var machineB = Fingerprint("B");
        var content = IssueFor(machineA);

        Assert.Equal(LicenseStatus.Valid, Service(machineA).ImportFile(content).Status);
        Assert.Equal(LicenseStatus.WrongMachine, Service(machineB).ImportFile(content).Status);
    }

    [Fact]
    public void DogruMakinedeYuklenirVeKaydedilir()
    {
        var machine = Fingerprint("A");
        var store = new MemoryStore();
        var service = Service(machine, store);

        Assert.Equal(LicenseStatus.Valid, service.ImportFile(IssueFor(machine)).Status);
        Assert.NotNull(store.Saved);
        // Yuklendikten sonra program yeniden acilinca lisans orada olmalidir.
        Assert.Equal(LicenseStatus.Valid, service.Check().Status);
    }

    /// <summary>
    /// Gecersiz dosya, GECERLI olani ezmemelidir: musteri yanlis dosyayi secerse
    /// calisan kurulumunu kaybetmemeli.
    /// </summary>
    [Fact]
    public void GecersizDosyaMevcutLisansiBozmaz()
    {
        var machine = Fingerprint("A");
        var store = new MemoryStore();
        var service = Service(machine, store);
        service.ImportFile(IssueFor(machine));
        var before = store.Saved;

        var result = service.ImportFile(IssueFor(Fingerprint("B")));

        Assert.Equal(LicenseStatus.WrongMachine, result.Status);
        Assert.Same(before, store.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bu json degil")]
    [InlineData("{}")]
    public void BozukDosyaAnlasilirHataVerir(string? content)
    {
        var result = Service(Fingerprint("A")).ImportFile(content);

        Assert.Equal(LicenseStatus.NotActivated, result.Status);
        Assert.Contains("dosya", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kurcalanmis dosya yakalanir: bitis tarihini elle uzatmak imzayi bozar.
    /// </summary>
    [Fact]
    public void KurcalanmisDosyaReddedilir()
    {
        var machine = Fingerprint("A");
        var license = LicenseFile.Read(IssueFor(machine))!;
        var tampered = LicenseFile.Write(license with { ExpiresAt = DateTimeOffset.UtcNow.AddYears(10) });

        Assert.Equal(LicenseStatus.Tampered, Service(machine).ImportFile(tampered).Status);
    }

    /// <summary>
    /// Dosya adi makine kimligini tasir: bir okula iki bilgisayar satildiginda
    /// yanlis dosyayi gondermek en sik hatadir.
    /// </summary>
    [Fact]
    public void DosyaAdiMakineKimligiIcerir()
    {
        var name = LicenseFile.SuggestFileName("Atatürk İlkokulu", "7266C28AA6B4");

        Assert.EndsWith("-7266C28AA6B4.lic", name, StringComparison.Ordinal);
        // Dosya adinda gecersiz karakter kalmamalidir: Windows kaydetmeyi reddederdi.
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), name.Contains);
    }

    private static string IssueFor(HardwareFingerprint fingerprint) =>
        LicenseFile.Write(LicenseIssuer.Issue("YMK-2026-TEST-TEST", "Okul", "Standart",
            [.. fingerprint.Hashes], DateTimeOffset.UtcNow, null, Secret));

    private static HardwareFingerprint Fingerprint(string id) =>
        new([FingerprintHasher.Hash(id + "-anakart"), FingerprintHasher.Hash(id + "-disk"),
             FingerprintHasher.Hash(id + "-guid")]);

    private static LicenseService Service(HardwareFingerprint fingerprint, MemoryStore? store = null) =>
        new(store ?? new MemoryStore(), new FixedReader(fingerprint),
            new OfflineLicenseActivationClient(Secret), TimeProvider.System, Secret,
            enforceOfflineGracePeriod: false);

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
