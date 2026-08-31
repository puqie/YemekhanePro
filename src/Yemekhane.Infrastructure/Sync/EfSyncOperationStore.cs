using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Sync;

public sealed class EfSyncOperationStore(YemekhaneDbContext dbContext) : ISyncOperationStore
{
    public async Task<SyncOperation> EnqueueAsync(SyncOperation operation, CancellationToken cancellationToken)
    {
        var existing = await dbContext.SyncOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == operation.OperationId, cancellationToken);
        if (existing is not null) return existing;

        dbContext.SyncOperations.Add(operation);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(operation).State = EntityState.Detached;
            existing = await dbContext.SyncOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operation.OperationId, cancellationToken);
            if (existing is not null) return existing;
            throw;
        }
    }

    public async Task<IReadOnlyList<SyncOperation>> GetPendingAsync(int batchSize,
        CancellationToken cancellationToken) => await dbContext.SyncOperations.AsNoTracking()
        .Where(x => x.SyncStatus == SyncOperationStatuses.Pending ||
                    x.SyncStatus == SyncOperationStatuses.RetryPending)
        .OrderBy(x => YemekhaneDbContext.JulianDay(x.Timestamp))
        .ThenBy(x => x.OperationId)
        .Take(batchSize)
        .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SyncOperation>> ClaimPendingAsync(int batchSize,
        CancellationToken cancellationToken)
    {
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE sync_operations SET SyncStatus = {{SyncOperationStatuses.RetryPending}}
                WHERE SyncStatus = {{SyncOperationStatuses.Processing}}
                  AND julianday(UpdatedAt) < julianday({{staleBefore}})
                """, cancellationToken);

            var candidates = await GetPendingAsync(batchSize, cancellationToken);
            var claimed = new List<SyncOperation>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var updated = await dbContext.SyncOperations
                    .Where(x => x.OperationId == candidate.OperationId &&
                        (x.SyncStatus == SyncOperationStatuses.Pending || x.SyncStatus == SyncOperationStatuses.RetryPending))
                    .ExecuteUpdateAsync(x => x
                        .SetProperty(y => y.SyncStatus, SyncOperationStatuses.Processing)
                        .SetProperty(y => y.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
                if (updated == 1)
                {
                    candidate.SyncStatus = SyncOperationStatuses.Processing;
                    claimed.Add(candidate);
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlyList<SyncOperation>)claimed;
        }, dbContext.ChangeTracker.Clear, cancellationToken);
    }

    public async Task UpdateAttemptAsync(Guid operationId, int attemptCount, string status, string? failure,
        CancellationToken cancellationToken)
    {
        var updated = await dbContext.SyncOperations
            .Where(x => x.OperationId == operationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AttemptCount, attemptCount)
                .SetProperty(x => x.SyncStatus, status)
                .SetProperty(x => x.LastError, failure)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (updated != 1) throw new InvalidOperationException($"Sync operation bulunamadı: {operationId}");
    }
}
