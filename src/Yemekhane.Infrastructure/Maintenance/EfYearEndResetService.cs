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
    private sealed record Step(string Key, string Label,
        Func<YemekhaneDbContext, CancellationToken, Task<int>> Count,
        Func<YemekhaneDbContext, CancellationToken, Task<int>> Delete);

    private static Step Of<T>(string key, string label, Func<YemekhaneDbContext, IQueryable<T>> set) where T : class =>
        new(key, label,
            (context, cancellationToken) => set(context).CountAsync(cancellationToken),
            (context, cancellationToken) => set(context).ExecuteDeleteAsync(cancellationToken));

    /// <summary>
    /// Silme SIRASI yabanci anahtarlara gore: once bagimli kayitlar, en son ogrenciler. SQLite
    /// yabanci anahtarlari zorlar; ters sira Restrict iliskilerinde (bakiye, tahsilat) patlar.
    /// Cihazlar, ogunler, siniflar, kullanicilar, tatiller, ayarlar bu listede YOKTUR.
    /// </summary>
    private static readonly Step[] Steps =
    [
        Of("meal-usages", "Yemek kullanımları", context => context.MealUsages),
        Of("turnstile-events", "Turnike olayları", context => context.TurnstileEvents),
        Of("access-logs", "Geçiş kayıtları", context => context.AccessLogs),
        Of("device-card-states", "Cihazdaki kart kayıtları", context => context.DeviceCardStates),
        Of("meal-transfers", "Hak devirleri", context => context.MealTransfers),
        Of("entitlements", "Yemek hakedişleri", context => context.MealEntitlements),
        Of("leaves", "İzinler", context => context.Set<StudentLeave>()),
        Of("group-members", "Grup üyelikleri", context => context.Set<StudentGroupMember>()),
        Of("balance-entries", "Bakiye hareketleri", context => context.StudentBalanceEntries),
        Of("income", "Tahsilatlar", context => context.Set<IncomeTransaction>()),
        Of("sms-logs", "SMS kayıtları", context => context.SmsLogs),
        Of("bulk-operations", "Toplu işlem geçmişi", context => context.BulkOperations),
        Of("parents", "Veliler", context => context.Parents),
        Of("cards", "Öğrenci kartları", context => context.StudentCards),
        Of("students", "Öğrenciler", context => context.Students),
    ];

    public async Task<YearEndResetPreview> PreviewAsync(CancellationToken cancellationToken)
    {
        var items = new List<YearEndResetItem>(Steps.Length);
        foreach (var step in Steps)
            items.Add(new YearEndResetItem(step.Key, step.Label, await step.Count(db, cancellationToken).ConfigureAwait(false)));
        return new YearEndResetPreview(items);
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

        var deleted = new List<YearEndResetItem>(Steps.Length);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var step in Steps)
            deleted.Add(new YearEndResetItem(step.Key, step.Label, await step.Delete(db, cancellationToken).ConfigureAwait(false)));

        var total = deleted.Sum(item => item.Count);
        audit.Record(new AuditEntry("YearEndReset", "Database", null,
            $"Yıl sonu sıfırlaması: {total} kayıt silindi. Önce {backupFile} güvenlik yedeği alındı.",
            total, After: deleted));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();

        return new YearEndResetResult(backupFile, deleted, timeProvider.GetUtcNow());
    }
}
