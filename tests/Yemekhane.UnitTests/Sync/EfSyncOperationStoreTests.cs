using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.UnitTests.Sync;

public sealed class EfSyncOperationStoreTests
{
    [Fact]
    public async Task DuplicateEnqueueAndSuccessfulUpdatePreserveSingleAuditRecord()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new YemekhaneDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var store = new EfSyncOperationStore(context);
        var operationId = Guid.NewGuid();

        await store.EnqueueAsync(CreateOperation(operationId, "{\"value\":1}"), CancellationToken.None);
        await store.EnqueueAsync(CreateOperation(operationId, "{\"value\":2}"), CancellationToken.None);
        await store.UpdateAttemptAsync(operationId, 1, SyncOperationStatuses.Succeeded, null,
            CancellationToken.None);

        context.ChangeTracker.Clear();
        var persisted = await context.SyncOperations.AsNoTracking().SingleAsync();
        Assert.Equal("{\"value\":1}", persisted.Payload);
        Assert.Equal(SyncOperationStatuses.Succeeded, persisted.SyncStatus);
        Assert.Equal(1, persisted.AttemptCount);
    }

    private static SyncOperation CreateOperation(Guid operationId, string payload) => new()
    {
        OperationId = operationId,
        EntityName = "Student",
        EntityId = "student-1",
        OperationType = "Update",
        Timestamp = DateTimeOffset.UtcNow,
        DeviceId = "device-1",
        Payload = payload,
        SyncStatus = SyncOperationStatuses.Pending
    };
}
