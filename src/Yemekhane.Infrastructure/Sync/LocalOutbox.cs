using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Sync;

public static class LocalOutbox
{
    public const string CreateAccessLog = "CREATE_ACCESS_LOG";
    public const string UpdateStudent = "UPDATE_STUDENT";
    public const string CreateMealEntitlement = "CREATE_MEAL_ENTITLEMENT";
    public const string UpdateCard = "UPDATE_CARD";
    public const string CreateIncomeTransaction = "CREATE_INCOME_TRANSACTION";
    public const string QueueSms = "QUEUE_SMS";

    public static void Enqueue(YemekhaneDbContext db, Entity entity, string operationType,
        object payload, Guid? operationId = null, DateTimeOffset? timestamp = null, string? deviceId = null)
    {
        var id = operationId ?? Guid.NewGuid();
        if (db.ChangeTracker.Entries<SyncOperation>().Any(x => x.Entity.OperationId == id)) return;

        db.SyncOperations.Add(new SyncOperation
        {
            OperationId = id,
            EntityName = entity.GetType().Name,
            EntityId = entity.Id.ToString("D"),
            OperationType = operationType,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? Environment.MachineName : deviceId,
            Payload = JsonSerializer.Serialize(payload),
            SyncStatus = SyncOperationStatuses.Pending
        });
    }
}
