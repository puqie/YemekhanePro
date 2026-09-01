using System.Collections.Concurrent;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Sync;

/// <summary>
/// Kalici olarak basarisiz olan islemlerin kuyrugu tikamamasini dogrular.
/// Sunucu surekli 503 dondururse (or. sertifika suresi dolmus), en eski islemler
/// her turda yeniden secilir ve arkalarindaki yeni kayitlar hic gonderilmez.
/// </summary>
public sealed class SyncRetryExhaustionTests
{
    [Fact]
    public async Task PermanentlyFailingOperationsDoNotBlockNewerOnesForever()
    {
        var store = new MemoryStore();
        var transport = new AlwaysTransientTransport();
        var engine = CreateEngine(store, transport, batchSize: 2);

        var oldest = await engine.EnqueueAsync(Request(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero)));
        var newer = await engine.EnqueueAsync(Request(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)));

        // Kuyruk boyutu 2; iki eski islem surekli basarisiz olur.
        for (var tick = 0; tick < 6; tick++) await engine.RunOnceAsync();

        // Tukenen islemler kalici hataya tasinmali ve bekleyen kuyruktan cikmalidir.
        var pending = await store.GetPendingAsync(100, default);
        Assert.DoesNotContain(pending, x => x.OperationId == oldest.OperationId);

        var exhausted = store.Operations.Single(x => x.OperationId == oldest.OperationId);
        Assert.Equal(SyncOperationStatuses.PermanentFailure, exhausted.SyncStatus);
        Assert.NotNull(newer);
    }

    [Fact]
    public async Task AttemptCountDoesNotGrowWithoutBound()
    {
        var store = new MemoryStore();
        var engine = CreateEngine(store, new AlwaysTransientTransport(), batchSize: 10);
        await engine.EnqueueAsync(Request(DateTimeOffset.UtcNow));

        for (var tick = 0; tick < 10; tick++) await engine.RunOnceAsync();

        var operation = store.Operations.Single();
        Assert.True(operation.AttemptCount <= 6,
            $"AttemptCount sinirsiz buyuyor: {operation.AttemptCount}");
    }

    private static SyncEngine CreateEngine(MemoryStore store, ISyncTransport transport, int batchSize) =>
        new(store, transport, new SyncEngineOptions
        {
            BatchSize = batchSize,
            MaxTransientRetries = 1,
            MaxTotalAttempts = 6,
            InitialRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero
        });

    private static EnqueueSyncOperation Request(DateTimeOffset timestamp) => new(
        Guid.NewGuid(), "AccessLog", "log-1", "Create", timestamp, "device-1", "{\"value\":1}");

    private sealed class AlwaysTransientTransport : ISyncTransport
    {
        public Task<SyncTransportResult> SendAsync(SyncRequestOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(new SyncTransportResult(SyncTransportOutcome.TransientFailure, "503"));
    }

    private sealed class MemoryStore : ISyncOperationStore
    {
        public Task<IReadOnlyList<SyncOperation>> GetConflictsAsync(int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncOperation>>(_operations.Values
                .Where(x => x.SyncStatus == SyncOperationStatuses.Conflict).Take(batchSize).ToList());

        public Task RequeueAsync(Guid operationId, CancellationToken cancellationToken)
        {
            if (!_operations.TryGetValue(operationId, out var operation) ||
                operation.SyncStatus != SyncOperationStatuses.Conflict)
                throw new Yemekhane.Application.Common.EntityNotFoundException("Çakışan işlem bulunamadı.");
            operation.SyncStatus = SyncOperationStatuses.RetryPending;
            return Task.CompletedTask;
        }

        private readonly ConcurrentDictionary<Guid, SyncOperation> _operations = new();
        public IReadOnlyCollection<SyncOperation> Operations => _operations.Values.ToArray();

        public Task<SyncOperation> EnqueueAsync(SyncOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(_operations.GetOrAdd(operation.OperationId, operation));

        public Task<IReadOnlyList<SyncOperation>> GetPendingAsync(int batchSize, CancellationToken cancellationToken)
        {
            IReadOnlyList<SyncOperation> result = _operations.Values
                .Where(x => x.SyncStatus is SyncOperationStatuses.Pending or SyncOperationStatuses.RetryPending)
                .OrderBy(x => x.Timestamp).ThenBy(x => x.OperationId).Take(batchSize).ToArray();
            return Task.FromResult(result);
        }

        public Task UpdateAttemptAsync(Guid operationId, int attemptCount, string status, string? failure,
            CancellationToken cancellationToken)
        {
            var operation = _operations[operationId];
            operation.AttemptCount = attemptCount;
            operation.SyncStatus = status;
            operation.LastError = failure;
            return Task.CompletedTask;
        }
    }
}
