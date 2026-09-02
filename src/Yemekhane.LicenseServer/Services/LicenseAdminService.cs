using Microsoft.EntityFrameworkCore;
using Yemekhane.LicenseServer.Data;

namespace Yemekhane.LicenseServer.Services;

/// <summary>Lisans satisi ve yonetimi. Yalnizca yonetici belirtecine sahip cagirana acilir.</summary>
public sealed class LicenseAdminService(LicenseDbContext db, TimeProvider clock)
{
    /// <summary>Suresiz lisansta kullanilan yil sayisi ust siniri (yillik lisans icin).</summary>
    public const int MaximumYears = 20;

    /// <summary>
    /// Yeni lisans satar.
    /// <paramref name="years"/> null ise SURESIZ, doluysa o kadar yillik abonelik.
    /// </summary>
    public async Task<LicenseRecord> CreateAsync(
        string customerName, string edition, int? years, string? notes, CancellationToken cancellationToken)
    {
        var name = (customerName ?? string.Empty).Trim();
        if (name.Length is < 2 or > 200)
            throw new ArgumentException("Müşteri adı 2-200 karakter olmalıdır.", nameof(customerName));

        var editionName = string.IsNullOrWhiteSpace(edition) ? "Standart" : edition.Trim();
        if (editionName.Length > 64)
            throw new ArgumentException("Sürüm adı en fazla 64 karakter olabilir.", nameof(edition));

        if (years is { } y && y is < 1 or > MaximumYears)
            throw new ArgumentException($"Yıl sayısı 1-{MaximumYears} aralığında olmalıdır.", nameof(years));

        var now = clock.GetUtcNow();

        // Anahtar cakismasi pratikte imkansiz (31^8) ama benzersiz indeks ihlali
        // musteriye "beklenmeyen hata" olarak yansimasin diye birkac kez denenir.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var key = LicenseKeyGenerator.Create(now);
            if (await db.Licenses.AnyAsync(x => x.LicenseKey == key, cancellationToken)) continue;

            var record = new LicenseRecord
            {
                LicenseKey = key,
                CustomerName = name,
                Edition = editionName,
                ExpiresAt = years is { } value ? now.AddYears(value) : null,
                CreatedAt = now,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
            };
            db.Licenses.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            return record;
        }

        throw new InvalidOperationException("Benzersiz lisans anahtarı üretilemedi; lütfen tekrar deneyin.");
    }

    /// <summary>
    /// Lisansi iptal eder. Iptal edilen lisans bir sonraki /validate cagrisinda
    /// sahada aninda gecersizlesir.
    /// </summary>
    public async Task<bool> RevokeAsync(string licenseKey, string? reason, CancellationToken cancellationToken)
    {
        var license = await FindAsync(licenseKey, cancellationToken);
        if (license is null) return false;
        license.RevokedAt = clock.GetUtcNow();
        license.RevokedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Iptali geri alir (yanlislikla iptal edilen lisans icin).</summary>
    public async Task<bool> RestoreAsync(string licenseKey, CancellationToken cancellationToken)
    {
        var license = await FindAsync(licenseKey, cancellationToken);
        if (license is null) return false;
        license.RevokedAt = null;
        license.RevokedReason = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Aboneligi uzatir. Suresi DOLMUS lisansta uzatma BUGUNDEN baslar; hala gecerli
    /// olanda mevcut bitisin uzerine eklenir, boylece erken yenileyen musteri gun kaybetmez.
    /// </summary>
    public async Task<LicenseRecord?> ExtendAsync(string licenseKey, int years, CancellationToken cancellationToken)
    {
        if (years is < 1 or > MaximumYears)
            throw new ArgumentException($"Yıl sayısı 1-{MaximumYears} aralığında olmalıdır.", nameof(years));

        var license = await FindAsync(licenseKey, cancellationToken);
        if (license is null) return null;

        var now = clock.GetUtcNow();
        var start = license.ExpiresAt is { } expires && expires > now ? expires : now;
        license.ExpiresAt = start.AddYears(years);
        await db.SaveChangesAsync(cancellationToken);
        return license;
    }

    /// <summary>Yillik lisansi suresiz yapar (musteri surumu satin aldiginda).</summary>
    public async Task<LicenseRecord?> MakePerpetualAsync(string licenseKey, CancellationToken cancellationToken)
    {
        var license = await FindAsync(licenseKey, cancellationToken);
        if (license is null) return null;
        license.ExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return license;
    }

    /// <summary>
    /// Lisansi makineden cozer: musteri bilgisayar degistirdiginde yeni makinede
    /// aktive edilebilsin diye. Anahtar ayni kalir.
    /// </summary>
    public async Task<bool> ReleaseMachineAsync(string licenseKey, CancellationToken cancellationToken)
    {
        var license = await FindAsync(licenseKey, cancellationToken);
        if (license is null) return false;
        license.FingerprintHashes = null;
        license.ActivatedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// En yeni lisans basta. Siralama ISTEMCIDE yapilir: SQLite ORDER BY icinde
    /// DateTimeOffset'i CEVIREMEZ ve sunucu 500 dondururdu (yonetim ekrani hic acilmazdi).
    /// Liste 500 kayitla sinirli oldugu icin bellekte siralamak sorun degildir.
    /// </summary>
    public async Task<List<LicenseRecord>> ListAsync(string? search, CancellationToken cancellationToken)
    {
        var query = db.Licenses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.CustomerName.Contains(term) || x.LicenseKey.Contains(term));
        }
        var rows = await query.Take(500).ToListAsync(cancellationToken);
        return [.. rows.OrderByDescending(x => x.CreatedAt)];
    }

    private Task<LicenseRecord?> FindAsync(string licenseKey, CancellationToken cancellationToken)
    {
        var key = LicenseKeyGenerator.Normalize(licenseKey);
        return db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, cancellationToken);
    }
}
