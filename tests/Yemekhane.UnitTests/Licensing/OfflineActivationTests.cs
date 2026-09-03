using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// SUNUCUSUZ aktivasyonun uctan uca akisi: anahtar gir -> lisans uretilsin ->
/// gecerli olsun -> BASKA makinede gecersiz olsun.
///
/// Bu mod, aylik sunucu maliyetini ortadan kaldirir. Feda edilen tek yetenek
/// UZAKTAN IPTAL'dir; diger tum korumalar (imza, donanim bagi, saat kontrolu)
/// aynen calisir ve bu testler bunu kanitlar.
/// </summary>
public sealed class OfflineActivationTests
{
    private const string Secret = "test-imza-sirri-en-az-32-bayt-olmali-1234567890";

    [Fact]
    public async Task GecerliAnahtarSunucusuzAktivasyonuTamamlar()
    {
        var store = new MemoryStore();
        var service = CreateService(store, Machine("makine-a"));
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret);

        var result = await service.ActivateAsync(key);

        Assert.Equal(LicenseStatus.Valid, result.Status);
        Assert.NotNull(store.Saved);
        // Suresiz: sunucusuz modda aboneligi yenileyecek bir merci yoktur.
        Assert.Null(store.Saved!.ExpiresAt);
    }

    [Fact]
    public async Task UydurmaAnahtarReddedilir()
    {
        var service = CreateService(new MemoryStore(), Machine("makine-a"));

        var result = await service.ActivateAsync("YMK-2026-AAAA-BBBB-CCCC");

        Assert.Equal(LicenseStatus.NotActivated, result.Status);
        Assert.Contains("gecersiz", result.Message, StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    /// ANAHTAR YALNIZCA BIR KEZ GIRILIR.
    ///
    /// Kullanicinin her acilista yapmasi gereken sey kullanici adi/sifre ile
    /// GIRIS'tir; lisans anahtari degil. Anahtar bir kez girilir, lisans diske
    /// kaydedilir ve sonraki acilislarda oradan okunur.
    ///
    /// Bu davranis daha once testle korunmuyordu: biri kaydi kaldirsa ya da
    /// Check() mantigini bozsa, okul her sabah anahtar aramak zorunda kalir ve
    /// bunu ancak sikayet gelince ogrenirdiniz.
    /// </summary>
    [Fact]
    public async Task AnahtarYalnizcaBirKezGirilir()
    {
        var store = new MemoryStore();
        var machine = Machine("makine-a");

        // Ilk acilis: lisans yok -> aktivasyon ekrani acilir.
        Assert.Equal(LicenseStatus.NotActivated, CreateService(store, machine).Check().Status);

        // Kullanici anahtari BIR KEZ girer.
        await CreateService(store, machine).ActivateAsync(
            OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret));

        // Sonraki her acilis: aktivasyon ekrani ACILMAZ.
        for (var launch = 2; launch <= 10; launch++)
            Assert.Equal(LicenseStatus.Valid, CreateService(store, machine).Check().Status);
    }

    /// <summary>
    /// Aktivasyon lisansi DISKE yazmalidir: yalnizca bellekte tutulsaydi program
    /// kapaninca kaybolur ve her acilista anahtar sorulurdu.
    /// </summary>
    [Fact]
    public async Task AktivasyonLisansiDiskeYazar()
    {
        var store = new MemoryStore();
        await CreateService(store, Machine("makine-a")).ActivateAsync(
            OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret));

        Assert.NotNull(store.Saved);
        Assert.False(string.IsNullOrWhiteSpace(store.Saved!.Signature));
    }

    /// <summary>
    /// ASIL KOPYALAMA KORUMASI: lisans dosyasi baska bilgisayara kopyalansa bile
    /// calismamalidir. Sunucusuz modda uzaktan iptal olmadigi icin bu koruma daha
    /// da kritiktir -- tek engel budur.
    /// </summary>
    [Fact]
    public async Task LisansBaskaMakineyeKopyalanirsaCalismaz()
    {
        var store = new MemoryStore();
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret);
        await CreateService(store, Machine("makine-a")).ActivateAsync(key);

        // Ayni lisans dosyasi, BASKA bir makinede.
        var onOtherMachine = CreateService(store, Machine("makine-b")).Check();

        Assert.Equal(LicenseStatus.WrongMachine, onOtherMachine.Status);
    }

    /// <summary>
    /// Sunucusuz modda 30 gunluk cevrimdisi tolerans UYGULANMAZ: dogrulanacak
    /// sunucu yokken programi kilitlemek, musteriyi cozumu olmayan bir duruma sokardi.
    /// </summary>
    [Fact]
    public async Task UzunSureSonraBileCalismayaDevamEder()
    {
        var store = new MemoryStore();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var machine = Machine("makine-a");
        var key = OfflineLicenseKey.Create(start, Secret);

        await CreateService(store, machine, clock).ActivateAsync(key);

        // Bir yil sonra: sunucu hic yok, hicbir dogrulama yapilmadi.
        clock.Now = start.AddDays(365);
        var check = CreateService(store, machine, clock).Check();

        Assert.Equal(LicenseStatus.Valid, check.Status);
    }

    /// <summary>
    /// Saat geri alinirsa yine de yakalanir: sunucusuz olmak, korumasiz olmak degildir.
    /// </summary>
    [Fact]
    public async Task SaatGeriAlinirsaYakalanir()
    {
        var store = new MemoryStore();
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var machine = Machine("makine-a");

        await CreateService(store, machine, clock).ActivateAsync(
            OfflineLicenseKey.Create(start, Secret));

        // Saat iki gun geri alindi (25 saatlik tolerans penceresinin disi).
        clock.Now = start.AddDays(-2);
        var check = CreateService(store, machine, clock).Check();

        Assert.Equal(LicenseStatus.Tampered, check.Status);
    }

    /// <summary>
    /// Lisans dosyasindaki GUVENLIK alanlari kurcalanirsa yakalanir; imza kontrolu
    /// sunucudan bagimsizdir.
    ///
    /// Imza anahtari, parmak izlerini ve tarihleri kapsar -- yani "hangi makinede
    /// gecerli" ve "ne zaman biter" sorularini. Musteri adi gibi bilgi amacli alanlar
    /// bilerek kapsam disidir: onlari degistirmek saldirgana hicbir sey kazandirmaz.
    /// </summary>
    [Theory]
    [InlineData("parmak izi")]
    [InlineData("bitis tarihi")]
    [InlineData("anahtar")]
    public async Task KurcalanmisLisansYakalanir(string field)
    {
        var store = new MemoryStore();
        var machine = Machine("makine-a");
        await CreateService(store, machine).ActivateAsync(
            OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret));

        store.Saved = field switch
        {
            // Baska bir makinenin parmak izini yazmak: lisansi calmaya calismak.
            "parmak izi" => store.Saved! with { FingerprintHashes = [FingerprintHasher.Hash("baska-makine")] },
            // Suresi dolmus lisansi uzatmak.
            "bitis tarihi" => store.Saved! with { ExpiresAt = DateTimeOffset.UtcNow.AddYears(10) },
            _ => store.Saved! with { LicenseKey = "YMK-2026-AAAA-BBBB-CCCC" }
        };

        var status = CreateService(store, machine).Check().Status;
        Assert.True(status is LicenseStatus.Tampered or LicenseStatus.WrongMachine,
            $"{field} degistirildi ama yakalanmadi: {status}");
    }

    private static LicenseService CreateService(
        MemoryStore store, IHardwareFingerprintReader reader, TimeProvider? clock = null) =>
        new(store, reader, new OfflineLicenseActivationClient(Secret, timeProvider: clock),
            clock ?? TimeProvider.System, Secret, enforceOfflineGracePeriod: false);

    private static IHardwareFingerprintReader Machine(string id) => new FakeReader(id);

    private sealed class FakeReader(string id) : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() =>
            new([FingerprintHasher.Hash(id + "-a"), FingerprintHasher.Hash(id + "-b"),
                 FingerprintHasher.Hash(id + "-c")]);
    }

    private sealed class MemoryStore : ILicenseStore
    {
        public StoredLicense? Saved { get; set; }
        public StoredLicense? Load() => Saved;
        public void Save(StoredLicense license) => Saved = license;
        public void Clear() => Saved = null;
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
