namespace Yemekhane.Licensing;

/// <summary>
/// Lisans denetiminin sonucu. <see cref="Valid"/> disindaki her deger aktivasyon
/// ekranini acar; ekran kullaniciya SEBEBI soyler, "bir hata olustu" demez.
/// </summary>
public enum LicenseStatus
{
    /// <summary>Lisans gecerli; uygulama normal calisir.</summary>
    Valid,

    /// <summary>Hic lisans dosyasi yok. Ilk kurulumun normal hali.</summary>
    NotActivated,

    /// <summary>Imza tutmuyor veya saat geriye alinmis: dosya kurcalanmis.</summary>
    Tampered,

    /// <summary>Parmak izi 2/3 tutmuyor: lisans baska bir makineye ait.</summary>
    WrongMachine,

    /// <summary>Abonelik suresi dolmus.</summary>
    Expired,

    /// <summary>Satici lisansi iptal etmis.</summary>
    Revoked,

    /// <summary>Cevrimdisi tolerans suresi asilmis; sunucuya baglanilmali.</summary>
    OfflineGracePeriodExceeded
}

/// <summary>
/// Lisans denetiminin tam sonucu: durum, kullaniciya gosterilecek Turkce aciklama ve
/// suresi dolmak uzereyken gosterilecek uyari.
/// </summary>
/// <param name="Status">Karar.</param>
/// <param name="Message">Kullaniciya gosterilecek Turkce aciklama.</param>
/// <param name="Warning">
/// <see cref="LicenseStatus.Valid"/> iken doldurulabilir: uygulama calisir ama
/// kullanici yaklasan sorundan haberdar edilir. Uyari yoksa null.
/// </param>
/// <param name="License">Gecerli ise lisans bilgisi; degilse null.</param>
public sealed record LicenseCheck(
    LicenseStatus Status,
    string Message,
    string? Warning = null,
    StoredLicense? License = null)
{
    /// <summary>Uygulamanin acilmasina izin verilip verilmedigi.</summary>
    public bool IsValid => Status == LicenseStatus.Valid;
}

/// <summary>
/// Diske yazilan lisans kaydi. Donanim bilesenleri HAM HALDE saklanmaz; her biri ayri
/// ayri hash'lenir, boylece dosya calinsa bile donanim kimligi sizmaz.
/// </summary>
/// <param name="LicenseKey">Lisans anahtari.</param>
/// <param name="CustomerName">Musteri adi (yalnizca goruntuleme).</param>
/// <param name="Edition">Surum adi (yalnizca goruntuleme).</param>
/// <param name="FingerprintHashes">Uc donanim bileseninin hash'leri; 2/3 eslesme icin.</param>
/// <param name="IssuedAt">Lisansin verildigi an.</param>
/// <param name="ExpiresAt">Abonelik bitisi; null ise suresiz.</param>
/// <param name="LastValidatedAt">
/// Sunucuyla en son basarili dogrulama ani. Cevrimdisi sayacinin baslangicidir ve
/// ASLA GERIYE GITMEZ - aksi halde saati geri alan kullanici icin 30 gunluk tolerans
/// sonsuza donerdi.
/// </param>
/// <param name="Signature">Sunucu imzasi; yerel kurcalamayi ele verir.</param>
public sealed record StoredLicense(
    string LicenseKey,
    string CustomerName,
    string Edition,
    IReadOnlyList<string> FingerprintHashes,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset LastValidatedAt,
    string Signature)
{
    /// <summary>
    /// Kayit esitligi ICERIGE gore hesaplanir.
    ///
    /// Varsayilan record esitligi <see cref="FingerprintHashes"/> icin REFERANS
    /// karsilastirmasi yapar: diskten okunan lisans, ayni degerleri tasisa bile
    /// kaydedilenle esit sayilmaz (biri dizi, digeri List olur). Bu sessiz tuzak,
    /// "lisans degisti mi?" turu her karsilastirmayi hep "evet" yapardi.
    /// </summary>
    public bool Equals(StoredLicense? other) =>
        other is not null
        && LicenseKey == other.LicenseKey
        && CustomerName == other.CustomerName
        && Edition == other.Edition
        && IssuedAt == other.IssuedAt
        && ExpiresAt == other.ExpiresAt
        && LastValidatedAt == other.LastValidatedAt
        && Signature == other.Signature
        && FingerprintHashes.SequenceEqual(other.FingerprintHashes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(LicenseKey);
        hash.Add(CustomerName);
        hash.Add(Edition);
        hash.Add(IssuedAt);
        hash.Add(ExpiresAt);
        hash.Add(LastValidatedAt);
        hash.Add(Signature);
        foreach (var fingerprint in FingerprintHashes) hash.Add(fingerprint);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Makinenin donanim parmak izi: uc bagimsiz bilesenin hash'i.
/// Okunamayan bilesen bos birakilir ve eslesme sayilmaz.
/// </summary>
/// <param name="Hashes">Bilesen hash'leri; okunamayanlar bos dizedir.</param>
public sealed record HardwareFingerprint(IReadOnlyList<string> Hashes)
{
    /// <summary>Okunabilmis bilesen sayisi.</summary>
    public int ReadableComponentCount => Hashes.Count(hash => !string.IsNullOrEmpty(hash));

    /// <summary>
    /// Hicbir bilesen okunamadiysa parmak izi anlamsizdir. Bu durumda aktivasyon
    /// REDDEDILIR; sessizce "her makine gecerli" durumuna DUSULMEZ.
    /// </summary>
    public bool IsUsable => ReadableComponentCount > 0;

    /// <summary>Destek ekraninda gosterilecek kisa makine kimligi.</summary>
    public string MachineId => Hashes.Count == 0
        ? "BILINMIYOR"
        : string.Concat(Hashes.Where(hash => !string.IsNullOrEmpty(hash)))
            is { Length: > 0 } combined
            ? FingerprintHasher.Hash(combined)[..12].ToUpperInvariant()
            : "BILINMIYOR";
}
