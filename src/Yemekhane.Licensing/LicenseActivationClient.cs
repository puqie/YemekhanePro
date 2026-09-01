namespace Yemekhane.Licensing;

/// <summary>
/// Sunucu dogrulamasinin sonucu.
///
/// <see cref="Unreachable"/> ile <see cref="Revoked"/> AYRI tutulur. Bu ayrim
/// kritiktir: karistirilirsa ya iptal hicbir ise yaramaz (ag hatasi gecerli sayilirsa)
/// ya da internet kesintisi okulu kilitler (ag hatasi iptal sayilirsa).
/// </summary>
public enum ValidationOutcome
{
    /// <summary>Sunucu lisansi onayladi.</summary>
    Valid,

    /// <summary>Sunucu lisansin iptal edildigini bildirdi. Aninda gecersizlesir.</summary>
    Revoked,

    /// <summary>Sunucuya ulasilamadi. Ihlal DEGILDIR; cevrimdisi toleransa dusulur.</summary>
    Unreachable
}

/// <param name="Outcome">Sunucunun karari.</param>
/// <param name="ExpiresAt">Sunucudan gelen yeni bitis tarihi; yoksa mevcut korunur.</param>
/// <param name="Signature">Yeni imza; yoksa mevcut korunur.</param>
public sealed record ValidationResult(
    ValidationOutcome Outcome,
    DateTimeOffset? ExpiresAt = null,
    string? Signature = null);

/// <param name="Succeeded">Aktivasyonun basarili olup olmadigi.</param>
/// <param name="License">Basarili ise kaydedilecek lisans.</param>
/// <param name="Message">
/// Basarisiz ise kullaniciya gosterilecek SOMUT Turkce sebep.
/// "Bir hata olustu" gibi bir mesaj kullaniciyi destege mahkum eder.
/// </param>
public sealed record ActivationResult(bool Succeeded, StoredLicense? License, string? Message);

/// <summary>Aktivasyon sunucusuyla konusan istemci.</summary>
public interface ILicenseActivationClient
{
    /// <summary>Bu makineyi verilen anahtarla aktive eder.</summary>
    Task<ActivationResult> ActivateAsync(
        string licenseKey, HardwareFingerprint fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Kayitli lisansin hala gecerli olup olmadigini sorar.</summary>
    Task<ValidationResult> ValidateAsync(StoredLicense license, CancellationToken cancellationToken = default);
}
