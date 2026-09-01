using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class LicenseServiceTests
{
    private const string Secret = "test-imza-anahtari";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] MachineHashes =
    [
        FingerprintHasher.Hash("ANAKART-1"),
        FingerprintHasher.Hash("DISK-1"),
        FingerprintHasher.Hash("GUID-1")
    ];

    /// <summary>Gecerli, imzali bir lisans uretir.</summary>
    private static StoredLicense Licensed(
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? lastValidatedAt = null,
        string[]? hashes = null)
    {
        hashes ??= MachineHashes;
        var issuedAt = Now.AddDays(-10);
        expiresAt ??= Now.AddYears(1);

        return new StoredLicense(
            "ANAHTAR-1", "Test Okulu", "Standart", hashes,
            issuedAt, expiresAt, lastValidatedAt ?? Now.AddDays(-1),
            LicenseSignature.Sign(
                LicenseSignature.BuildPayload("ANAHTAR-1", hashes, issuedAt, expiresAt), Secret));
    }

    private static LicenseService Build(
        StoredLicense? stored,
        ILicenseActivationClient? client = null,
        DateTimeOffset? now = null,
        string[]? machineHashes = null) =>
        new(new FakeStore(stored),
            new FakeFingerprintReader(machineHashes ?? MachineHashes),
            client ?? new FakeActivationClient(new ValidationResult(ValidationOutcome.Unreachable)),
            new FixedTimeProvider(now ?? Now),
            Secret);

    [Fact]
    public void AValidLicenseOpensTheApplication()
    {
        Assert.Equal(LicenseStatus.Valid, Build(Licensed()).Check().Status);
    }

    [Fact]
    public void WithNoLicenseFileTheUserIsAskedToActivate()
    {
        var check = Build(null).Check();

        Assert.Equal(LicenseStatus.NotActivated, check.Status);
        Assert.False(check.IsValid);
    }

    [Fact]
    public void EditingTheExpiryDateInTheFileIsDetected()
    {
        // Dosyadaki bitis tarihini ileri almak en bariz saldiridir. Imza bu alani
        // kapsadigi icin degisiklik imzayi bozar.
        var tampered = Licensed() with { ExpiresAt = Now.AddYears(50) };

        Assert.Equal(LicenseStatus.Tampered, Build(tampered).Check().Status);
    }

    [Fact]
    public void TurningTheClockBackDoesNotExtendTheOfflineGracePeriod()
    {
        // 30 gunluk toleransi sonsuza cevirmenin en kolay yolu sistem saatini geri
        // almaktir. Kayitli dogrulama ani "gelecekte" gorunuyorsa bu kurcalamadir.
        var license = Licensed(lastValidatedAt: Now);
        var service = Build(license, now: Now.AddDays(-10));

        Assert.Equal(LicenseStatus.Tampered, service.Check().Status);
    }

    [Fact]
    public void ASmallForwardClockDriftIsToleratedBecauseTimeSyncCausesIt()
    {
        // Zaman sunucusuyla esitleme kucuk ileri sapmalar yaratir; bunu kurcalama
        // saymak durust musteriyi sebepsiz kilitlerdi.
        var license = Licensed(lastValidatedAt: Now.AddHours(2));

        Assert.Equal(LicenseStatus.Valid, Build(license, now: Now).Check().Status);
    }

    [Fact]
    public void ALicenseFromAnotherComputerIsRejected()
    {
        var otherMachine = new[]
        {
            FingerprintHasher.Hash("BASKA-ANAKART"),
            FingerprintHasher.Hash("BASKA-DISK"),
            FingerprintHasher.Hash("GUID-1")
        };

        Assert.Equal(LicenseStatus.WrongMachine, Build(Licensed(hashes: otherMachine)).Check().Status);
    }

    [Fact]
    public void ReplacingASingleDiskKeepsTheLicenseWorking()
    {
        // Disk degistiren musteri lisansini kaybetmemelidir.
        var afterDiskSwap = new[] { MachineHashes[0], FingerprintHasher.Hash("YENI-DISK"), MachineHashes[2] };

        Assert.Equal(LicenseStatus.Valid, Build(Licensed(), machineHashes: afterDiskSwap).Check().Status);
    }

    [Fact]
    public void AnExpiredSubscriptionLocksTheApplication()
    {
        var check = Build(Licensed(expiresAt: Now.AddDays(-1))).Check();

        Assert.Equal(LicenseStatus.Expired, check.Status);
        Assert.Contains("yenileyin", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PassingThirtyDaysOfflineLocksTheApplication()
    {
        var check = Build(Licensed(lastValidatedAt: Now.AddDays(-31))).Check();

        Assert.Equal(LicenseStatus.OfflineGracePeriodExceeded, check.Status);
    }

    [Fact]
    public void AtTwentyFourDaysOfflineTheApplicationStillWorksButWarns()
    {
        // Kullanici kilitlenmeden ONCE uyarilmalidir: bir sabah uygulamanin acilmamasi
        // yemek dagitiminin durmasi demektir.
        var check = Build(Licensed(lastValidatedAt: Now.AddDays(-24))).Check();

        Assert.Equal(LicenseStatus.Valid, check.Status);
        Assert.NotNull(check.Warning);
        Assert.Contains("gun icinde", check.Warning);
    }

    [Fact]
    public void ExactlyThirtyDaysOfflineIsStillAllowed()
    {
        // Sinir degeri: tolerans "30 gune kadar" demektir, 30. gunde kilitlemek
        // musteriye bir gun eksik verirdi.
        Assert.Equal(LicenseStatus.Valid, Build(Licensed(lastValidatedAt: Now.AddDays(-30))).Check().Status);
    }

    [Fact]
    public async Task ANetworkOutageDoesNotLockAWorkingLicense()
    {
        // Okulun interneti kesildiginde yemek dagitimi DURMAMALIDIR.
        var service = Build(Licensed(),
            new FakeActivationClient(new ValidationResult(ValidationOutcome.Unreachable)));

        Assert.Equal(LicenseStatus.Valid, (await service.ValidateAsync()).Status);
    }

    [Fact]
    public async Task ARevokedLicenseIsInvalidatedImmediatelyEvenThoughItLooksValidLocally()
    {
        // Ag hatasi ile iptal AYRI seylerdir. Karistirilirsa iptal hicbir ise yaramaz.
        var store = new FakeStore(Licensed());
        var service = new LicenseService(store, new FakeFingerprintReader(MachineHashes),
            new FakeActivationClient(new ValidationResult(ValidationOutcome.Revoked)),
            new FixedTimeProvider(Now), Secret);

        var check = await service.ValidateAsync();

        Assert.Equal(LicenseStatus.Revoked, check.Status);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task ASuccessfulValidationResetsTheOfflineCounter()
    {
        var store = new FakeStore(Licensed(lastValidatedAt: Now.AddDays(-20)));
        var service = new LicenseService(store, new FakeFingerprintReader(MachineHashes),
            new FakeActivationClient(new ValidationResult(ValidationOutcome.Valid)),
            new FixedTimeProvider(Now), Secret);

        await service.ValidateAsync();

        Assert.Equal(Now, store.Current!.LastValidatedAt);
    }

    [Fact]
    public async Task ValidatingOnAMachineWithARolledBackClockNeverMovesTheCounterBackwards()
    {
        // Saati geri alip sunucuya baglanmak, cevrimdisi sayacini uzatmak icin
        // kullanilabilirdi. LastValidatedAt asla geriye gitmez.
        //
        // Saat, kurcalama esiginin (25 saat) ICINDE geri alinir: daha buyuk bir sapma
        // zaten Tampered ile erkenden reddedilir ve Refresh'e hic ulasilmazdi. Asil
        // tehlike, esigin altinda kalarak sayaci her seferinde biraz geri itmektir.
        var recorded = Now;
        var store = new FakeStore(Licensed(lastValidatedAt: recorded));
        var service = new LicenseService(store, new FakeFingerprintReader(MachineHashes),
            new FakeActivationClient(new ValidationResult(ValidationOutcome.Valid)),
            new FixedTimeProvider(Now.AddHours(-20)), Secret);

        await service.ValidateAsync();

        Assert.Equal(recorded, store.Current!.LastValidatedAt);
    }

    [Fact]
    public async Task ActivationIsRefusedWhenNoHardwareComponentCanBeRead()
    {
        // Sessizce "her makine gecerli" durumuna dusmek lisansi anlamsiz kilardi.
        var service = new LicenseService(new FakeStore(null),
            new FakeFingerprintReader([string.Empty, string.Empty, string.Empty]),
            new FakeActivationClient(new ValidationResult(ValidationOutcome.Valid)),
            new FixedTimeProvider(Now), Secret);

        var check = await service.ActivateAsync("ANAHTAR-1");

        Assert.Equal(LicenseStatus.WrongMachine, check.Status);
    }

    [Fact]
    public async Task AnEmptyKeyProducesAClearMessageRatherThanCallingTheServer()
    {
        var client = new FakeActivationClient(new ValidationResult(ValidationOutcome.Valid));
        var check = await Build(null, client).ActivateAsync("   ");

        Assert.Equal(LicenseStatus.NotActivated, check.Status);
        Assert.Equal(0, client.ActivationCallCount);
    }

    [Fact]
    public async Task ASuccessfulActivationSavesTheLicenseAndOpensTheApplication()
    {
        var store = new FakeStore(null);
        var client = new FakeActivationClient(new ValidationResult(ValidationOutcome.Valid))
        {
            ActivationResult = new ActivationResult(true, Licensed(lastValidatedAt: Now), null)
        };
        var service = new LicenseService(store, new FakeFingerprintReader(MachineHashes),
            client, new FixedTimeProvider(Now), Secret);

        var check = await service.ActivateAsync("ANAHTAR-1");

        Assert.Equal(LicenseStatus.Valid, check.Status);
        Assert.NotNull(store.Current);
    }

    [Fact]
    public async Task AFailedActivationShowsTheServerReasonNotAGenericError()
    {
        // Kullanici anahtarini mi yanlis yazdigini, yoksa lisansin baska bir
        // bilgisayarda mi oldugunu bilmelidir.
        var client = new FakeActivationClient(new ValidationResult(ValidationOutcome.Unreachable))
        {
            ActivationResult = new ActivationResult(false, null, "Lisans anahtari bulunamadi.")
        };

        var check = await Build(null, client).ActivateAsync("YANLIS-ANAHTAR");

        Assert.Equal("Lisans anahtari bulunamadi.", check.Message);
    }

    [Fact]
    public async Task ATamperedLicenseIsNotSentToTheServer()
    {
        // Kurcalanmis bir lisansi sunucuya sormanin anlami yoktur; cevap ne olursa
        // olsun gecersizdir. Gereksiz ag cagrisi da yapilmaz.
        var client = new FakeActivationClient(new ValidationResult(ValidationOutcome.Valid));
        var tampered = Licensed() with { ExpiresAt = Now.AddYears(50) };

        var check = await Build(tampered, client).ValidateAsync();

        Assert.Equal(LicenseStatus.Tampered, check.Status);
        Assert.Equal(0, client.ValidationCallCount);
    }

    private sealed class FakeStore(StoredLicense? initial) : ILicenseStore
    {
        public StoredLicense? Current { get; private set; } = initial;

        public StoredLicense? Load() => Current;
        public void Save(StoredLicense license) => Current = license;
        public void Clear() => Current = null;
    }

    private sealed class FakeFingerprintReader(string[] hashes) : IHardwareFingerprintReader
    {
        public HardwareFingerprint Read() => new(hashes);
    }

    private sealed class FakeActivationClient(ValidationResult validation) : ILicenseActivationClient
    {
        public ActivationResult ActivationResult { get; set; } = new(false, null, "Yapilandirilmadi.");
        public int ActivationCallCount { get; private set; }
        public int ValidationCallCount { get; private set; }

        public Task<ActivationResult> ActivateAsync(
            string licenseKey, HardwareFingerprint fingerprint, CancellationToken cancellationToken = default)
        {
            ActivationCallCount++;
            return Task.FromResult(ActivationResult);
        }

        public Task<ValidationResult> ValidateAsync(
            StoredLicense license, CancellationToken cancellationToken = default)
        {
            ValidationCallCount++;
            return Task.FromResult(validation);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
