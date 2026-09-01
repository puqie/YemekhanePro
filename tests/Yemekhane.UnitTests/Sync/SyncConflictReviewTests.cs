using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.UnitTests.Sync;

/// <summary>
/// Cakisan senkronizasyon islemleri sessizce kaybolmamalidir.
///
/// Motor bir cakismayi "Conflict" olarak isaretleyip durur; bu dogrudur, cunku
/// hangi tarafin kazanacagina motor karar veremez. Ancak o kayit hicbir yerde
/// GORUNMEZSE ve operator ona dokunamazsa, islem sonsuza kadar kuyrukta olu kalir:
/// kullanici "senkronize oldu" sanir, veri aslinda hic gitmemistir.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class SyncConflictReviewTests
{
    [Fact]
    public async Task ConflictedOperationsAreListedWithTheirReason()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.MigrateAsync();
        var conflicted = Operation(SyncOperationStatuses.Conflict, "{\"remoteVersion\":2}");
        db.AddRange(conflicted, Operation(SyncOperationStatuses.Synced), Operation(SyncOperationStatuses.RetryPending));
        await db.SaveChangesAsync();
        var store = new EfSyncOperationStore(db);

        var conflicts = await store.GetConflictsAsync(50, default);

        var single = Assert.Single(conflicts);
        Assert.Equal(conflicted.OperationId, single.OperationId);
        Assert.Equal("UpdateStudent", single.OperationType);
        // Operator NEDEN cakistigini gormeden karar veremez.
        Assert.Contains("remoteVersion", single.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequeueingAConflictPutsItBackInTheOutboxForAnotherAttempt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.MigrateAsync();
        var conflicted = Operation(SyncOperationStatuses.Conflict, "{\"remoteVersion\":2}");
        db.Add(conflicted);
        await db.SaveChangesAsync();
        var store = new EfSyncOperationStore(db);

        await store.RequeueAsync(conflicted.OperationId, default);

        var stored = await db.SyncOperations.AsNoTracking()
            .SingleAsync(x => x.OperationId == conflicted.OperationId);
        Assert.Equal(SyncOperationStatuses.RetryPending, stored.SyncStatus);
        // Kuyruga geri alinan islem bir sonraki turda GERCEKTEN secilmelidir.
        var pending = await store.GetPendingAsync(50, default);
        Assert.Contains(pending, x => x.OperationId == conflicted.OperationId);
    }

    [Fact]
    public async Task OnlyConflictedOperationsCanBeRequeued()
    {
        // Basariyla gonderilmis bir islemi yeniden kuyruga almak, ayni degisikligi
        // ikinci kez gondermek demektir; bu sessizce veri bozar.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.MigrateAsync();
        var synced = Operation(SyncOperationStatuses.Synced);
        db.Add(synced);
        await db.SaveChangesAsync();
        var store = new EfSyncOperationStore(db);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => store.RequeueAsync(synced.OperationId, default));

        var stored = await db.SyncOperations.AsNoTracking().SingleAsync(x => x.OperationId == synced.OperationId);
        Assert.Equal(SyncOperationStatuses.Synced, stored.SyncStatus);
    }

    private static SyncOperation Operation(string status, string? error = null) => new()
    {
        OperationId = Guid.NewGuid(),
        EntityName = "Student",
        EntityId = Guid.NewGuid().ToString("D"),
        OperationType = "UpdateStudent",
        Timestamp = DateTimeOffset.UtcNow,
        DeviceId = "test",
        Payload = "{}",
        SyncStatus = status,
        LastError = error
    };

    private static YemekhaneDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
