using Microsoft.EntityFrameworkCore;

namespace Yemekhane.LicenseServer.Data;

/// <summary>
/// Satilan bir lisans. Bir lisans TEK makineye baglanir: ilk aktivasyonda o
/// makinenin parmak izleri kaydedilir, sonraki aktivasyon denemeleri 409 alir.
/// </summary>
public sealed class LicenseRecord
{
    public int Id { get; set; }

    /// <summary>Musteriye verilen anahtar (YMK-2026-0001). Buyuk harfe normalize edilir.</summary>
    public required string LicenseKey { get; set; }

    /// <summary>Okul adi; destek gorusmelerinde ve yonetim listesinde gorunur.</summary>
    public required string CustomerName { get; set; }

    /// <summary>Surum adi (Standart, Kurumsal...). Yalnizca goruntuleme.</summary>
    public required string Edition { get; set; }

    /// <summary>
    /// null ise SURESIZ lisans; doluysa yillik/abonelik. Masaustu bu degeri imzali
    /// alir, yani dosyada ileri alinamaz.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Satici lisansi iptal ettiyse dolu. Dolu olan lisans /validate'te 410 doner.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Iptal gerekcesi; yalnizca yonetim ekraninda gorunur.</summary>
    public string? RevokedReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ilk aktivasyon ani; null ise lisans henuz hicbir makinede kullanilmamis.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>
    /// Aktive edildigi makinenin parmak izi hash'leri, '|' ile birlestirilmis.
    /// HAM donanim bilgisi DEGILDIR (masaustu hash'leyip gonderir), bu yuzden
    /// sunucu sizsa bile musterinin donanim kimligi ele gecmez.
    /// </summary>
    public string? FingerprintHashes { get; set; }

    /// <summary>Son basarili /validate ani; "canli mi" sorusunun cevabi.</summary>
    public DateTimeOffset? LastValidatedAt { get; set; }

    /// <summary>Toplam dogrulama sayisi; sahada gercekten kullanilip kullanilmadigini gosterir.</summary>
    public int ValidationCount { get; set; }

    public string? Notes { get; set; }

    public bool IsRevoked => RevokedAt is not null;
    public bool IsPerpetual => ExpiresAt is null;
}

public sealed class LicenseDbContext(DbContextOptions<LicenseDbContext> options) : DbContext(options)
{
    public DbSet<LicenseRecord> Licenses => Set<LicenseRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var license = modelBuilder.Entity<LicenseRecord>();
        license.ToTable("licenses");
        // Anahtar BENZERSIZ: ayni anahtari iki kez satmak sessizce mumkun olmamali.
        license.HasIndex(x => x.LicenseKey).IsUnique();
        license.Property(x => x.LicenseKey).HasMaxLength(64);
        license.Property(x => x.CustomerName).HasMaxLength(200);
        license.Property(x => x.Edition).HasMaxLength(64);
        license.Property(x => x.RevokedReason).HasMaxLength(500);
        license.Property(x => x.Notes).HasMaxLength(1000);
    }
}
