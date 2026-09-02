using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Balances;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Access;

public sealed class EfTurnstileEventStore(YemekhaneDbContext dbContext) : ITurnstileEventStore
{
    public async Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent,
        bool compensateConsumption, CancellationToken cancellationToken)
    {
        await using var transaction = compensateConsumption
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var accessLog = turnstileEvent.OperationId.HasValue
            ? await dbContext.AccessLogs.SingleOrDefaultAsync(
                x => x.OperationId == turnstileEvent.OperationId.Value, cancellationToken)
            : null;
        var compensated = false;

        if (compensateConsumption && accessLog is not null)
        {
            var usage = await dbContext.MealUsages.SingleOrDefaultAsync(
                x => x.AccessLogId == accessLog.Id, cancellationToken);
            if (usage is not null)
            {
                var changed = await dbContext.MealEntitlements
                    .Where(x => x.Id == usage.EntitlementId && x.ConsumedQuantity > 0)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.ConsumedQuantity, x => x.ConsumedQuantity - 1)
                        .SetProperty(x => x.Version, x => x.Version + 1)
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
                if (changed == 1)
                {
                    dbContext.MealUsages.Remove(usage);
                    accessLog.Decision = "ERROR";
                    accessLog.Reason = "Turnike komutu başarısız; yemek hakkı iade edildi";
                    compensated = true;
                }
            }
            else if (await FindUnrefundedDeductionAsync(accessLog.Id, cancellationToken) is { } deduction)
            {
                // Gecis bakiyeden odenmisti (hakedis yok, MealUsage yok): para iade edilir.
                // Aksi halde turnike acilmadigi halde ogrencinin bakiyesi eksilmis kalir.
                dbContext.StudentBalanceEntries.Add(new StudentBalanceEntry
                {
                    StudentId = deduction.StudentId, AmountCents = -deduction.AmountCents, Kind = StudentBalanceEntryKinds.Refund,
                    ReferenceType = StudentBalanceReferenceTypes.AccessLog, ReferenceId = accessLog.Id,
                    Note = "Turnike komutu başarısız; öğün ücreti iade edildi", OccurredAt = turnstileEvent.Timestamp
                });
                accessLog.Decision = "ERROR";
                accessLog.Reason = "Turnike komutu başarısız; bakiye iade edildi";
                compensated = true;
            }
        }

        dbContext.TurnstileEvents.Add(new TurnstileEvent
        {
            DeviceId = turnstileEvent.DeviceId,
            AccessLogId = accessLog?.Id,
            Timestamp = turnstileEvent.Timestamp,
            Command = turnstileEvent.Command,
            Result = compensated ? "COMPENSATED_RETRY_REQUIRED" : turnstileEvent.Result,
            Error = turnstileEvent.Error
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new TurnstileEventWriteResult(compensated);
    }

    private async Task<StudentBalanceEntry?> FindUnrefundedDeductionAsync(Guid accessLogId, CancellationToken cancellationToken)
    {
        var deduction = await dbContext.StudentBalanceEntries.AsNoTracking().SingleOrDefaultAsync(
            x => x.ReferenceType == StudentBalanceReferenceTypes.AccessLog && x.ReferenceId == accessLogId && x.Kind == StudentBalanceEntryKinds.Deduction,
            cancellationToken);
        if (deduction is null) return null;
        var refunded = await dbContext.StudentBalanceEntries.AnyAsync(
            x => x.ReferenceId == accessLogId && x.Kind == StudentBalanceEntryKinds.Refund, cancellationToken);
        return refunded ? null : deduction;
    }
}
