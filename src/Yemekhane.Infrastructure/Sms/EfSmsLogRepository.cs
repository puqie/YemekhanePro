using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Sms;

public sealed class EfSmsLogRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider) : ISmsLogRepository
{
    public async Task<SmsLogDetails> EnqueueAsync(string phone, string message, string idempotencyKey,
        Guid? studentId, Guid? templateId, CancellationToken cancellationToken)
    {
        var existing = await FindByKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null) return existing;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var log = new SmsLog
        {
            StudentId = studentId,
            TemplateId = templateId,
            Phone = phone,
            Message = message,
            Status = SmsLogStatuses.Pending,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            NextAttemptAt = now
        };
        dbContext.SmsLogs.Add(log);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Map(log);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.Entry(log).State = EntityState.Detached;
            existing = await FindByKeyAsync(idempotencyKey, cancellationToken);
            if (existing is not null) return existing;
            throw;
        }
    }

    public async Task<PagedResult<SmsLogDetails>> ListAsync(
        SmsHistoryFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.SmsLogs.FromSqlInterpolated($$"""
            SELECT * FROM sms_logs
            WHERE ({{filter.Status}} IS NULL OR Status = {{filter.Status}})
              AND ({{filter.Phone}} IS NULL OR Phone = {{filter.Phone}})
              AND ({{filter.From}} IS NULL OR CreatedAt >= {{filter.From}})
              AND ({{filter.To}} IS NULL OR CreatedAt <= {{filter.To}})
              AND ({{filter.StudentId}} IS NULL OR StudentId = {{filter.StudentId}})
              AND ({{filter.Provider}} IS NULL OR Provider = {{filter.Provider}})
              AND ({{filter.Source}} IS NULL
                   OR ({{filter.Source}} = 'AutoEntitlement' AND IdempotencyKey LIKE 'oto:hak:%')
                   OR ({{filter.Source}} = 'AutoIncome' AND IdempotencyKey LIKE 'oto:gelir:%')
                   OR ({{filter.Source}} = 'AutoCard' AND IdempotencyKey LIKE 'oto:kart:%')
                   OR ({{filter.Source}} = 'Bulk' AND length(IdempotencyKey) = 64 AND IdempotencyKey NOT GLOB '*[^0-9A-Fa-f]*')
                   OR ({{filter.Source}} = 'Manual' AND IdempotencyKey NOT LIKE 'oto:%'
                       AND NOT (length(IdempotencyKey) = 64 AND IdempotencyKey NOT GLOB '*[^0-9A-Fa-f]*')))
              AND ({{filter.Student}} IS NULL OR EXISTS (
                  SELECT 1 FROM students s WHERE s.Id = sms_logs.StudentId AND s.IsDeleted = 0
                    AND (s.student_no LIKE '%' || {{filter.Student}} || '%'
                      OR s.FirstName LIKE '%' || {{filter.Student}} || '%'
                      OR s.LastName LIKE '%' || {{filter.Student}} || '%')))
            """).AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var offset = (filter.Page - 1) * filter.PageSize;
        var pageQuery = dbContext.SmsLogs.FromSqlInterpolated($$"""
            SELECT * FROM sms_logs
            WHERE ({{filter.Status}} IS NULL OR Status = {{filter.Status}})
              AND ({{filter.Phone}} IS NULL OR Phone = {{filter.Phone}})
              AND ({{filter.From}} IS NULL OR CreatedAt >= {{filter.From}})
              AND ({{filter.To}} IS NULL OR CreatedAt <= {{filter.To}})
              AND ({{filter.StudentId}} IS NULL OR StudentId = {{filter.StudentId}})
              AND ({{filter.Provider}} IS NULL OR Provider = {{filter.Provider}})
              AND ({{filter.Source}} IS NULL
                   OR ({{filter.Source}} = 'AutoEntitlement' AND IdempotencyKey LIKE 'oto:hak:%')
                   OR ({{filter.Source}} = 'AutoIncome' AND IdempotencyKey LIKE 'oto:gelir:%')
                   OR ({{filter.Source}} = 'AutoCard' AND IdempotencyKey LIKE 'oto:kart:%')
                   OR ({{filter.Source}} = 'Bulk' AND length(IdempotencyKey) = 64 AND IdempotencyKey NOT GLOB '*[^0-9A-Fa-f]*')
                   OR ({{filter.Source}} = 'Manual' AND IdempotencyKey NOT LIKE 'oto:%'
                       AND NOT (length(IdempotencyKey) = 64 AND IdempotencyKey NOT GLOB '*[^0-9A-Fa-f]*')))
              AND ({{filter.Student}} IS NULL OR EXISTS (
                  SELECT 1 FROM students s WHERE s.Id = sms_logs.StudentId AND s.IsDeleted = 0
                    AND (s.student_no LIKE '%' || {{filter.Student}} || '%'
                      OR s.FirstName LIKE '%' || {{filter.Student}} || '%'
                      OR s.LastName LIKE '%' || {{filter.Student}} || '%')))
            ORDER BY CreatedAt DESC, Id DESC LIMIT {{filter.PageSize}} OFFSET {{offset}}
            """).AsNoTracking();
        var items = await pageQuery
            .Select(x => Map(x)).ToListAsync(cancellationToken);
        return new PagedResult<SmsLogDetails>(items, filter.Page, filter.PageSize, total);
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.SmsLogs.Where(x => x.Id == id && x.Status == SmsLogStatuses.Failed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, SmsLogStatuses.RetryScheduled)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.Error, (string?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken) == 1;
    }

    public Task<IReadOnlyList<SmsLog>> ClaimBatchAsync(
        DateTimeOffset now, TimeSpan staleAfter, int batchSize, CancellationToken cancellationToken)
        => SqliteBusyRetry.ExecuteAsync(() => ClaimBatchCoreAsync(now, staleAfter, batchSize, cancellationToken),
            dbContext.ChangeTracker.Clear, cancellationToken);

    private async Task<IReadOnlyList<SmsLog>> ClaimBatchCoreAsync(
        DateTimeOffset now, TimeSpan staleAfter, int batchSize, CancellationToken cancellationToken)
    {
        var staleBefore = now - staleAfter;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE sms_logs SET Status = {{SmsLogStatuses.RetryScheduled}}, NextAttemptAt = {{now}},
                SendingStartedAt = NULL, ClaimToken = NULL, Error = 'stale_sending_recovered',
                UpdatedAt = {{now}}
            WHERE Status = {{SmsLogStatuses.Sending}} AND SendingStartedAt <= {{staleBefore}}
            """, cancellationToken);

        var ids = await dbContext.SmsLogs.FromSqlInterpolated($$"""
            SELECT * FROM sms_logs
            WHERE (Status = {{SmsLogStatuses.Pending}} OR Status = {{SmsLogStatuses.RetryScheduled}})
              AND (NextAttemptAt IS NULL OR NextAttemptAt <= {{now}})
            ORDER BY CreatedAt, Id LIMIT {{batchSize}}
            """).AsNoTracking().Select(x => x.Id).ToListAsync(cancellationToken);
        var claimed = new List<SmsLog>(ids.Count);
        foreach (var id in ids)
        {
            var token = Guid.NewGuid().ToString("N");
            var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE sms_logs SET Status = {{SmsLogStatuses.Sending}}, SendingStartedAt = {{now}},
                    ClaimToken = {{token}}, AttemptCount = AttemptCount + 1, UpdatedAt = {{now}}
                WHERE Id = {{id}}
                  AND (Status = {{SmsLogStatuses.Pending}} OR Status = {{SmsLogStatuses.RetryScheduled}})
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= {{now}})
                """, cancellationToken);
            if (updated == 1)
                claimed.Add((await dbContext.SmsLogs.AsNoTracking()
                    .SingleAsync(x => x.Id == id && x.ClaimToken == token, cancellationToken)));
        }
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task CompleteAsync(Guid id, string claimToken, SmsSendResult result, string provider,
        DateTimeOffset now, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        var transientRetry = result.Outcome == SmsSendOutcome.TransientFailure;
        var current = await dbContext.SmsLogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.Status == SmsLogStatuses.Sending &&
                x.ClaimToken == claimToken, cancellationToken);
        if (current is null) return;

        var retry = transientRetry && current.AttemptCount < maxAttempts;
        var status = result.IsSuccess ? SmsLogStatuses.Sent : retry
            ? SmsLogStatuses.RetryScheduled : SmsLogStatuses.Failed;
        var error = result.IsSuccess ? null : Sanitize(result);
        await dbContext.SmsLogs.Where(x => x.Id == id && x.Status == SmsLogStatuses.Sending &&
                x.ClaimToken == claimToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.Provider, provider)
                .SetProperty(x => x.SentAt, result.IsSuccess ? now : (DateTimeOffset?)null)
                .SetProperty(x => x.ProviderMessageId, result.IsSuccess ? result.ProviderMessageId : null)
                .SetProperty(x => x.Error, error)
                .SetProperty(x => x.NextAttemptAt, retry ? now + retryDelay : (DateTimeOffset?)null)
                .SetProperty(x => x.SendingStartedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.ClaimToken, (string?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    private Task<SmsLogDetails?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        dbContext.SmsLogs.AsNoTracking().Where(x => x.IdempotencyKey == key)
            .Select(x => Map(x)).SingleOrDefaultAsync(cancellationToken);

    private static string Sanitize(SmsSendResult result) =>
        $"{result.ErrorCategory}:{result.ErrorCode ?? "unspecified"}" +
        (result.HttpStatusCode is { } status ? $":http_{status}" : string.Empty);

    private static SmsLogDetails Map(SmsLog x) => new(x.Id, x.StudentId, x.TemplateId, x.Phone,
        x.Message, x.Provider, x.Status, x.IdempotencyKey, x.AttemptCount, x.NextAttemptAt,
        x.SendingStartedAt, x.SentAt, x.ProviderMessageId, x.Error, x.CreatedAt);
}
