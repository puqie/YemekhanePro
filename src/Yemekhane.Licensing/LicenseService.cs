namespace Yemekhane.Licensing;

/// <summary>Lisans denetimini yapan servis.</summary>
public interface ILicenseService
{
    /// <summary>Yerel lisansi denetler. Ag erisimi GEREKTIRMEZ.</summary>
    LicenseCheck Check();

    /// <summary>
    /// Sunucuyla dogrulamayi dener ve sonuca gore yerel kaydi tazeler.
    /// Ag yoksa cevrimdisi toleransa dusulur; iptal edilmisse lisans hemen gecersizlesir.
    /// </summary>
    Task<LicenseCheck> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Verilen anahtarla bu makineyi aktive etmeyi dener.</summary>
    Task<LicenseCheck> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lisans karar motoru.
///
/// Tasarimin iki zorunlu ayrintisi burada yasar:
///
/// 1. <c>LastValidatedAt</c> ASLA GERIYE GITMEZ. Kullanici sistem saatini geri alirsa
///    cevrimdisi sayaci sifirlanmamalidir; bu olmadan 30 gunluk tolerans pratikte
///    sonsuza doner.
///
/// 2. Ag hatasi ile "iptal edildi" AYRI SEYLERDIR. Karistirilirsa ya iptal hicbir ise
///    yaramaz ya da internet kesintisi okulu kilitler.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    /// <summary>Sunucuya hic ulasilamadan calisilabilecek en uzun sure.</summary>
    public static readonly TimeSpan OfflineGracePeriod = TimeSpan.FromDays(30);

    /// <summary>Bu sureden sonra kullanici uyarilir ama uygulama calismaya devam eder.</summary>
    public static readonly TimeSpan OfflineWarningThreshold = TimeSpan.FromDays(23);

    /// <summary>Bitis tarihine bu kadar kala kullanici uyarilir.</summary>
    public static readonly TimeSpan ExpiryWarningThreshold = TimeSpan.FromDays(15);

    private readonly ILicenseStore store;
    private readonly IHardwareFingerprintReader fingerprintReader;
    private readonly ILicenseActivationClient activationClient;
    private readonly TimeProvider timeProvider;
    private readonly string signingSecret;

    /// <summary>
    /// Cevrimdisi tolerans uygulanir mi?
    ///
    /// SUNUCUSUZ modda FALSE olmalidir: dogrulanacak bir sunucu yokken 30 gun sonra
    /// "internete baglayin" deyip programi kilitlemek, musteriyi cozumu olmayan bir
    /// duruma sokar. Diger tum kontroller (imza, donanim, saat, bitis tarihi) her iki
    /// modda da AYNEN calisir -- sunucusuz olmak korumasiz olmak degildir.
    /// </summary>
    private readonly bool enforceOfflineGracePeriod;

    public LicenseService(
        ILicenseStore store,
        IHardwareFingerprintReader fingerprintReader,
        ILicenseActivationClient activationClient,
        TimeProvider timeProvider,
        string signingSecret,
        bool enforceOfflineGracePeriod = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fingerprintReader);
        ArgumentNullException.ThrowIfNull(activationClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrEmpty(signingSecret);

        this.store = store;
        this.fingerprintReader = fingerprintReader;
        this.activationClient = activationClient;
        this.timeProvider = timeProvider;
        this.signingSecret = signingSecret;
        this.enforceOfflineGracePeriod = enforceOfflineGracePeriod;
    }

    public LicenseCheck Check()
    {
        var license = store.Load();
        if (license is null)
            return new(LicenseStatus.NotActivated,
                "Bu bilgisayar icin lisans bulunamadi. Devam etmek icin lisans anahtarinizi girin.");

        return Evaluate(license);
    }

    /// <summary>Kayitli bir lisansi, ag erisimi olmadan degerlendirir.</summary>
    private LicenseCheck Evaluate(StoredLicense license)
    {
        var now = timeProvider.GetUtcNow();

        // Imza once dogrulanir: kurcalanmis bir dosyadaki tarihlere ve parmak izlerine
        // guvenip onlarla karar vermek, kontrolun tamamini anlamsiz kilardi.
        if (!LicenseSignature.Verify(license, signingSecret))
            return new(LicenseStatus.Tampered,
                "Lisans dosyasi gecerli degil. Lutfen lisansinizi yeniden etkinlestirin.");

        // Saat geri alinmis mi? Kayitli dogrulama ani gelecekte gorunuyorsa ya saat
        // geri alinmistir ya da dosya baska bir makineden kopyalanmistir.
        if (license.LastValidatedAt > now + ClockToleranceWindow)
            return new(LicenseStatus.Tampered,
                "Bilgisayarin saati gecerli gorunmuyor. Lutfen tarih ve saati duzeltip uygulamayi yeniden baslatin.");

        var fingerprint = fingerprintReader.Read();
        if (!fingerprint.IsUsable)
            return new(LicenseStatus.WrongMachine,
                "Bilgisayarin donanim bilgisi okunamadi. Lisans dogrulanamiyor.");

        if (!FingerprintMatcher.Matches(license.FingerprintHashes, fingerprint.Hashes))
            return new(LicenseStatus.WrongMachine,
                "Bu lisans baska bir bilgisayara ait. Yeni bilgisayar icin lisansinizi yeniden etkinlestirin.");

        if (license.ExpiresAt is { } expiresAt && expiresAt <= now)
            return new(LicenseStatus.Expired,
                $"Lisans suresi {expiresAt.ToLocalTime():dd.MM.yyyy} tarihinde doldu. Lutfen aboneliginizi yenileyin.");

        var offlineFor = now - license.LastValidatedAt;
        if (enforceOfflineGracePeriod && offlineFor > OfflineGracePeriod)
            return new(LicenseStatus.OfflineGracePeriodExceeded,
                $"Lisans {OfflineGracePeriod.Days} gundur dogrulanamadi. Lutfen bilgisayari internete baglayin.");

        return new(LicenseStatus.Valid, "Lisans gecerli.", BuildWarning(license, now, offlineFor), license);
    }

    /// <summary>
    /// Saat farki toleransi. Zaman sunucusuyla esitleme ve yaz saati gecisleri kucuk
    /// ileri sapmalar yaratir; bunlari kurcalama saymak durust musteriyi kilitler.
    /// </summary>
    private static readonly TimeSpan ClockToleranceWindow = TimeSpan.FromHours(25);

    private static string? BuildWarning(StoredLicense license, DateTimeOffset now, TimeSpan offlineFor)
    {
        // Kullanici sorunu OLMADAN ONCE ogrenmelidir: bir sabah uygulamanin acilmamasi,
        // yemek dagitiminin durmasi demektir.
        if (offlineFor > OfflineWarningThreshold)
        {
            var remaining = Math.Max(0, (OfflineGracePeriod - offlineFor).Days);
            return $"Lisans {remaining} gun icinde dogrulanmali. Lutfen bilgisayari internete baglayin.";
        }

        if (license.ExpiresAt is { } expiresAt && expiresAt - now <= ExpiryWarningThreshold)
            return $"Lisans suresi {expiresAt.ToLocalTime():dd.MM.yyyy} tarihinde doluyor. Lutfen aboneliginizi yenileyin.";

        return null;
    }

    public async Task<LicenseCheck> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var license = store.Load();
        if (license is null)
            return new(LicenseStatus.NotActivated,
                "Bu bilgisayar icin lisans bulunamadi. Devam etmek icin lisans anahtarinizi girin.");

        // Once yerel denetim: kurcalanmis veya yanlis makinedeki bir lisansi sunucuya
        // sormanin anlami yok, cevap ne olursa olsun gecersizdir.
        var local = Evaluate(license);
        if (local.Status is LicenseStatus.Tampered or LicenseStatus.WrongMachine) return local;

        var validation = await activationClient
            .ValidateAsync(license, cancellationToken)
            .ConfigureAwait(false);

        switch (validation.Outcome)
        {
            case ValidationOutcome.Valid:
                store.Save(Refresh(license, validation));
                return Evaluate(store.Load()!);

            case ValidationOutcome.Revoked:
                // Iptal ANINDA etkilidir; cevrimdisi toleransa dusulmez.
                store.Clear();
                return new(LicenseStatus.Revoked,
                    "Lisansiniz iptal edilmis. Lutfen satici ile iletisime gecin.");

            // Sunucuya ulasilamadi: bu bir ihlal DEGILDIR. Okul internetsiz kalabilir;
            // cevrimdisi tolerans tam olarak bunun icin vardir.
            case ValidationOutcome.Unreachable:
            default:
                return local;
        }
    }

    /// <summary>Sunucu dogrulamasi sonrasi yerel kaydi tazeler.</summary>
    private StoredLicense Refresh(StoredLicense license, ValidationResult validation)
    {
        var now = timeProvider.GetUtcNow();

        // LastValidatedAt ASLA GERIYE GITMEZ. Saati geri alinmis bir makinede simdiki
        // zamani yazmak, cevrimdisi sayacini uzatarak toleransi sonsuza cevirirdi.
        var lastValidatedAt = now > license.LastValidatedAt ? now : license.LastValidatedAt;

        return license with
        {
            ExpiresAt = validation.ExpiresAt ?? license.ExpiresAt,
            LastValidatedAt = lastValidatedAt,
            Signature = validation.Signature ?? license.Signature
        };
    }

    public async Task<LicenseCheck> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new(LicenseStatus.NotActivated, "Lutfen lisans anahtarinizi girin.");

        var fingerprint = fingerprintReader.Read();

        // Donanim hic okunamiyorsa aktivasyon REDDEDILIR. Sessizce gecmek, lisansi
        // makineye baglama fikrinin tamamini ortadan kaldirirdi.
        if (!fingerprint.IsUsable)
            return new(LicenseStatus.WrongMachine,
                "Bilgisayarin donanim bilgisi okunamadi. Lisans bu bilgisayara baglanamiyor.");

        var result = await activationClient
            .ActivateAsync(licenseKey.Trim(), fingerprint, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.License is null)
            return new(LicenseStatus.NotActivated, result.Message ?? "Lisans etkinlestirilemedi.");

        store.Save(result.License);

        // Kaydedilen lisans HEMEN yeniden degerlendirilir: sunucudan gelen kayit
        // imzasiz veya baska bir makineye aitse bunu simdi ogrenmek, kullanicinin
        // bir sonraki acilista kilitlenmesinden iyidir.
        return Evaluate(result.License);
    }
}
