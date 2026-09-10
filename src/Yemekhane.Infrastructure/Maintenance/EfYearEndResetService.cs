using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Maintenance;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Maintenance;

/// <summary>Guvenlik yedegini mevcut yedekleme servisiyle alir (ayni klasor, ayni bicim, geri yuklenebilir).</summary>
public sealed class BackupServiceYearEndBackup(BackupService backups) : IYearEndBackup
{
    public async Task<string> CreateSafetyBackupAsync(CancellationToken cancellationToken) =>
        (await backups.CreateAsync(cancellationToken).ConfigureAwait(false)).FileName;
}

public sealed class EfYearEndResetService(
    YemekhaneDbContext db, IYearEndBackup backup, IAuditService audit, TimeProvider timeProvider) : IYearEndResetService
{
    private sealed record Step(string Key, string Label, string Action,
        Func<YemekhaneDbContext, CancellationToken, Task<int>> Count,
        Func<YemekhaneDbContext, DateTimeOffset, CancellationToken, Task<int>> Apply);

    private static Step Delete<T>(string key, string label, Func<YemekhaneDbContext, IQueryable<T>> set) where T : class =>
        new(key, label, YearEndResetActions.Delete,
            (context, cancellationToken) => set(context).CountAsync(cancellationToken),
            (context, _, cancellationToken) => set(context).ExecuteDeleteAsync(cancellationToken));

    /// <summary>
    /// Sira yabanci anahtarlara gore: once bagimli kayitlar. SQLite yabanci anahtarlari zorlar;
    /// ters sira Restrict iliskilerinde patlar.
    ///
    /// SILINMEZ: ogrenciler, veliler, tahsilatlar ve bakiye hareketleri. Veli 1-2 yil onceki
    /// odemesini sorabilir; Raporlar → Gelir ve ogrencinin Ödemeler sekmesi bu kayitlari
    /// okur. Ogrenci sicili bu yuzden kalir ama PASIFE alinir: yeni yilin listesi Sicil Aktar
    /// ile yuklenince ayni numarali ogrenci yeniden aktif olur, mezunlar pasif kalir.
    ///
    /// Kartlar SILINIR: kart numarasi tum kayitlar icinde tekildir (pasif dahil); silinmezse
    /// mezunun karti bir sonraki yil yeni ogrenciye verilemezdi. Donen ogrencinin karti Sicil
    /// Aktar'daki kart sutunundan yeniden yazilir.
    /// Cihazlar, ogunler, siniflar, kullanicilar, tatiller, ayarlar bu listede YOKTUR.
    /// </summary>
    private static readonly Step[] Steps =
    [
        Delete("meal-usages", "Yemek kullanımları", context => context.MealUsages),
        Delete("turnstile-events", "Turnike olayları", context => context.TurnstileEvents),
        Delete("access-logs", "Geçiş kayıtları", context => context.AccessLogs),
        Delete("device-card-states", "Cihazdaki kart kayıtları", context => context.DeviceCardStates),
        Delete("meal-transfers", "Hak devirleri", context => context.MealTransfers),
        Delete("entitlements", "Yemek hakedişleri", context => context.MealEntitlements),
        Delete("leaves", "İzinler", context => context.Set<StudentLeave>()),
        Delete("group-members", "Grup üyelikleri", context => context.Set<StudentGroupMember>()),
        Delete("sms-logs", "SMS kayıtları", context => context.SmsLogs),
        Delete("bulk-operations", "Toplu işlem geçmişi", context => context.BulkOperations),
        Delete("cards", "Öğrenci kartları", context => context.StudentCards),
        new("students", "Öğrenciler (pasife alınır, silinmez)", YearEndResetActions.Deactivate,
            (context, cancellationToken) => context.Students.CountAsync(x => x.IsActive, cancellationToken),
            (context, now, cancellationToken) => context.Students.Where(x => x.IsActive)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.IsActive, false)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken)),
    ];

    public async Task<YearEndResetPreview> PreviewAsync(CancellationToken cancellationToken)
    {
        var items = new List<YearEndResetItem>(Steps.Length);
        foreach (var step in Steps)
            items.Add(new YearEndResetItem(step.Key, step.Label, await step.Count(db, cancellationToken).ConfigureAwait(false), step.Action));

        // ODENMIS ama KULLANILMAMIS haklar ayrica sayilir. Bunlar silinince karsiligi
        // olan tahsilat kasada AKTIF kalir -- hak yok, para var, iade yok. Kullanici
        // bunu gormeden onay veremesin; once yalnizca satir sayisi gosteriliyordu.
        var unusedPaid = await db.MealEntitlements.AsNoTracking()
            .Where(x => x.Status == "Active" && x.ConsumedQuantity < x.Quantity
                && db.Set<IncomeTransaction>().Any(income => !income.IsVoided
                    && income.StudentId == x.StudentId && income.MealTypeId == x.MealTypeId))
            .Select(x => new { x.StudentId, Remaining = x.Quantity - x.ConsumedQuantity })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new YearEndResetPreview(items,
            unusedPaid.Sum(x => x.Remaining),
            unusedPaid.Select(x => x.StudentId).Distinct().Count());
    }

    public async Task<YearEndResetResult> ResetAsync(string? confirmation, CancellationToken cancellationToken)
    {
        if (!YearEndReset.IsConfirmed(confirmation))
        {
            throw new RequestValidationException(
                $"Sıfırlama için onay kutusuna tam olarak {YearEndReset.ConfirmationPhrase} yazın (Türkçe karakter kullanmadan, büyük harfle).");
        }

        // Yedek ONCE alinir; alinamazsa istisna yukselir ve hicbir sey silinmez.
        var backupFile = await backup.CreateSafetyBackupAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        var applied = new List<YearEndResetItem>(Steps.Length);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var step in Steps)
            applied.Add(new YearEndResetItem(step.Key, step.Label, await step.Apply(db, now, cancellationToken).ConfigureAwait(false), step.Action));

        var deleted = applied.Where(item => item.Action == YearEndResetActions.Delete).Sum(item => item.Count);
        var deactivated = applied.Where(item => item.Action == YearEndResetActions.Deactivate).Sum(item => item.Count);
        audit.Record(new AuditEntry("YearEndReset", "Database", null,
            $"Yıl sonu sıfırlaması: {deleted} kayıt silindi, {deactivated} öğrenci pasife alındı; tahsilat, bakiye ve veli kayıtları korundu. Önce {backupFile} güvenlik yedeği alındı.",
            deleted + deactivated, After: applied));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();

        return new YearEndResetResult(backupFile, applied, now);
    }
}
