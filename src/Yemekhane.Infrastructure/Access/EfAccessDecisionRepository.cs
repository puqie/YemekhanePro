using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Access;

public sealed class EfAccessDecisionRepository(
    YemekhaneDbContext dbContext,
    AccessSnapshotCache cache,
    IAccessCacheInvalidationSink invalidationSink,
    AccessPerformanceMetrics metrics) : IAccessDecisionRepository
{
    public EfAccessDecisionRepository(YemekhaneDbContext dbContext)
        : this(dbContext, CreateCache(), new NullInvalidationSink(), new AccessPerformanceMetrics()) { }

    public Task<AccessDecision?> FindDecisionAsync(Guid operationId, CancellationToken cancellationToken) =>
        dbContext.AccessLogs.AsNoTracking().Where(x => x.OperationId == operationId)
            // Ogrenci adi AccessLog'da tutulmaz; tekrar yanitinin ilk yanitla ayni olmasi icin buradan cozulur.
            .Select(x => new AccessDecision(x.Decision, x.Reason, x.StudentId,
                dbContext.Students.IgnoreQueryFilters().AsNoTracking()
                    .Where(student => student.Id == x.StudentId)
                    .Select(student => student.FirstName + " " + student.LastName)
                    .FirstOrDefault(),
                x.DeviceId, x.MealTypeId ?? Guid.Empty, x.Timestamp, x.OperationId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<AccessSnapshot> GetSnapshotAsync(string cardNumber, Guid deviceId, Guid mealTypeId, DateOnly calendarDate, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (cache.TryGet(cardNumber, deviceId, mealTypeId, calendarDate, out var cached))
        {
            metrics.RecordLookup(System.Diagnostics.Stopwatch.GetElapsedTime(started), true);
            return cached;
        }

        var snapshot = await (from card in dbContext.StudentCards.AsNoTracking()
            join studentValue in dbContext.Students.IgnoreQueryFilters().AsNoTracking() on card.StudentId equals studentValue.Id into students
            from student in students.DefaultIfEmpty()
            join rightValue in dbContext.MealEntitlements.AsNoTracking()
                    .Where(x => x.MealTypeId == mealTypeId && x.EntitlementDate == calendarDate)
                on card.StudentId equals rightValue.StudentId into rights
            from right in rights.DefaultIfEmpty()
            where card.CardNumber == cardNumber
            select new AccessSnapshot(true, card.IsActive, card.StudentId,
                student == null ? null : student.FirstName + " " + student.LastName,
                student == null ? null : student.ClassId, student != null && student.IsActive && !student.IsDeleted,
                dbContext.Devices.Any(x => x.Id == deviceId && x.IsActive), right == null ? null : right.Id,
                right == null ? 0 : right.Quantity, right == null ? 0 : right.ConsumedQuantity,
                right == null ? null : right.Status,
                dbContext.Set<StudentLeave>().Any(x => x.StudentId == card.StudentId && x.StartsOn <= calendarDate && x.EndsOn >= calendarDate),
                // Grup kapsamli tatil: ogrencinin uye oldugu HERHANGI bir grup icin o gune
                // tatil tanimliysa gun kapalidir. Bu kontrol anlik goruntunun icinde yapilir ki
                // her kart okutmasinda ek sorgu acilmasin ve mevcut onbellek/gecersizlestirme gecerli kalsin.
                dbContext.Set<Holiday>().Any(holiday => holiday.Date == calendarDate &&
                    dbContext.Set<HolidayScope>().Any(scope => scope.HolidayId == holiday.Id &&
                        scope.ScopeType == "Group" &&
                        dbContext.Set<StudentGroupMember>().Any(member =>
                            member.GroupId == scope.ScopeId && member.StudentId == card.StudentId)))))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new AccessSnapshot(false, false, null, null, null, false, false, null, 0, 0, null, false, false);
        cache.Set(cardNumber, deviceId, mealTypeId, calendarDate, snapshot);
        metrics.RecordLookup(System.Diagnostics.Stopwatch.GetElapsedTime(started), false);
        return snapshot;
    }

    public async Task<bool> TryConsumeAndLogAsync(Guid entitlementId, AccessCheckRequest request, AccessDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                if (await dbContext.AccessLogs.AsNoTracking().AnyAsync(
                        x => x.OperationId == decision.OperationId && x.Decision == "ALLOW", cancellationToken))
                    return true;

                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                var changed = await dbContext.MealEntitlements.Where(x => x.Id == entitlementId && x.Status == "Active" && x.ConsumedQuantity < x.Quantity)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedQuantity, x => x.ConsumedQuantity + 1)
                        .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
                if (changed != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await dbContext.AccessLogs.AsNoTracking().AnyAsync(
                        x => x.OperationId == decision.OperationId && x.Decision == "ALLOW", cancellationToken);
                }
                var log = CreateLog(request, decision);
                dbContext.Add(log);
                dbContext.Add(new MealUsage { EntitlementId = entitlementId, StudentId = decision.StudentId!.Value,
                    MealTypeId = request.MealTypeId, AccessLogId = log.Id, UsedAt = request.Timestamp });
                LocalOutbox.Enqueue(dbContext, log, LocalOutbox.CreateAccessLog, new
                {
                    AccessLog = log,
                    EntitlementId = entitlementId,
                    ConsumedQuantity = 1
                }, decision.OperationId, request.Timestamp, request.DeviceId.ToString("D"));
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                invalidationSink.Publish(new(StudentId: decision.StudentId));
                return true;
            }, dbContext.ChangeTracker.Clear, cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            if (await dbContext.AccessLogs.AsNoTracking().AnyAsync(
                    x => x.OperationId == decision.OperationId && x.Decision == "ALLOW", cancellationToken))
                return true;
            throw;
        }
    }

    public async Task LogDeniedAsync(AccessCheckRequest request, AccessDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                if (await dbContext.AccessLogs.AsNoTracking().AnyAsync(x => x.OperationId == decision.OperationId, cancellationToken))
                    return true;
                var log = CreateLog(request, decision);
                dbContext.Add(log);
                LocalOutbox.Enqueue(dbContext, log, LocalOutbox.CreateAccessLog, log,
                    decision.OperationId, request.Timestamp, request.DeviceId.ToString("D"));
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }, dbContext.ChangeTracker.Clear, cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            if (!await dbContext.AccessLogs.AsNoTracking().AnyAsync(x => x.OperationId == decision.OperationId, cancellationToken)) throw;
        }
    }

    private static AccessLog CreateLog(AccessCheckRequest request, AccessDecision decision) => new()
    {
        Timestamp = request.Timestamp, CardNumber = request.CardNumber, StudentId = decision.StudentId,
        DeviceId = request.DeviceId, MealTypeId = request.MealTypeId, Decision = decision.Decision,
        Reason = decision.Reason, Direction = request.Direction, ReaderSource = request.ReaderSource,
        OperatorId = request.OperatorId, OperationId = decision.OperationId
    };

    private static AccessSnapshotCache CreateCache()
    {
        var metrics = new AccessPerformanceMetrics();
        return new AccessSnapshotCache(TimeProvider.System, metrics);
    }

    private sealed class NullInvalidationSink : IAccessCacheInvalidationSink
    {
        public void Publish(AccessCacheInvalidation invalidation) { }
    }
}
