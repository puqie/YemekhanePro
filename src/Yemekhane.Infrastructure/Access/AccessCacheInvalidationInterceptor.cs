using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yemekhane.Application.Access;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Infrastructure.Access;

public sealed class AccessCacheInvalidationInterceptor(IAccessCacheInvalidationSink sink) : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<Guid, AccessCacheInvalidation[]> pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Remove(eventData.Context);
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Remove(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null) return;
        var invalidations = new List<AccessCacheInvalidation>();
        foreach (var entry in context.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            switch (entry.Entity)
            {
                case Student student: invalidations.Add(new(StudentId: student.Id)); break;
                case StudentCard card: invalidations.Add(new(card.StudentId, card.CardNumber)); break;
                case MealEntitlement entitlement: invalidations.Add(new(StudentId: entitlement.StudentId)); break;
                case StudentLeave leave: invalidations.Add(new(StudentId: leave.StudentId)); break;
                case MealTransfer transfer: invalidations.Add(new(StudentId: transfer.StudentId)); break;
                case Holiday or HolidayScope or ScheduleOverride or Device: invalidations.Add(new(ClearAll: true)); break;
            }
        }
        if (invalidations.Count > 0) pending[context.ContextId.InstanceId] = invalidations.ToArray();
    }

    private void Publish(DbContext? context)
    {
        if (context is not null && pending.TryRemove(context.ContextId.InstanceId, out var invalidations))
            foreach (var invalidation in invalidations) sink.Publish(invalidation);
    }

    private void Remove(DbContext? context)
    {
        if (context is not null) pending.TryRemove(context.ContextId.InstanceId, out _);
    }
}
