using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
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
}
