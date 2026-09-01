using System.Collections.Concurrent;
using System.Net;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;
using Yemekhane.Sync;

namespace Yemekhane.UnitTests.Sync;

public sealed class SyncEngineTests
{
    [Fact]
    public async Task EnqueueWithSameOperationIdIsIdempotent()
    {
        var store = new MemoryStore();
        var engine = CreateEngine(store, new QueueTransport(Array.Empty<SyncTransportOutcome>()));
        var request = CreateRequest();

        var first = await engine.EnqueueAsync(request);
        var second = await engine.EnqueueAsync(request with { Payload = "{\"changed\":true}" });

        Assert.Same(first, second);
        Assert.Single(store.Operations);
        Assert.Equal("{\"value\":1}", first.Payload);
    }

    [Fact]
    public async Task SuccessfulSyncPreservesAuditRecord()
    {
        var store = new MemoryStore();
        var engine = CreateEngine(store, new QueueTransport(SyncTransportOutcome.Success));
        var operation = await engine.EnqueueAsync(CreateRequest());

        var result = await engine.RunOnceAsync();

        Assert.Equal(1, result.Succeeded);
        Assert.Single(store.Operations);
        Assert.Equal(SyncOperationStatuses.Succeeded, operation.SyncStatus);
        Assert.Equal(1, operation.AttemptCount);
        Assert.Null(operation.LastError);
    }

    [Fact]
    public async Task PendingBatchIsSentInTimestampThenOperationIdOrder()
    {
        var store = new MemoryStore();
        var transport = new QueueTransport(SyncTransportOutcome.Success, SyncTransportOutcome.Success);
        var engine = CreateEngine(store, transport);
        var later = CreateRequest() with { OperationId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var earlier = CreateRequest() with
        {
            OperationId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Timestamp = later.Timestamp.AddMinutes(-1)
        };
        await engine.EnqueueAsync(later);
        await engine.EnqueueAsync(earlier);

        await engine.RunOnceAsync();

        Assert.Equal([earlier.OperationId, later.OperationId], transport.SentOperationIds);
    }

    [Fact]
    public async Task DuplicateServerResponseIsAcceptedAsSuccess()
    {
        var store = new MemoryStore();
        var engine = CreateEngine(store, new QueueTransport(SyncTransportOutcome.Duplicate));
        var operation = await engine.EnqueueAsync(CreateRequest());

        var result = await engine.RunOnceAsync();

        Assert.Equal(1, result.DuplicateAccepted);
        Assert.Equal(SyncOperationStatuses.Succeeded, operation.SyncStatus);
    }

    [Fact]
    public async Task TransientFailureUsesBoundedRetriesAndRemainsPending()
    {
        var store = new MemoryStore();
        var transport = new QueueTransport(
            SyncTransportOutcome.TransientFailure,
            SyncTransportOutcome.TransientFailure,
            SyncTransportOutcome.TransientFailure);
        var engine = CreateEngine(store, transport, maxRetries: 2);
        var operation = await engine.EnqueueAsync(CreateRequest());

        var result = await engine.RunOnceAsync();

        Assert.Equal(1, result.RetryPending);
        Assert.Equal(3, transport.CallCount);
        Assert.Equal(3, operation.AttemptCount);
        Assert.Equal(SyncOperationStatuses.RetryPending, operation.SyncStatus);
        Assert.NotNull(operation.LastError);
    }

    [Fact]
    public async Task PermanentFailureIsTerminal()
    {
        var store = new MemoryStore();
        var transport = new QueueTransport(new SyncTransportResult(
            SyncTransportOutcome.PermanentFailure, "invalid", "rejected"));
        var engine = CreateEngine(store, transport);
        var operation = await engine.EnqueueAsync(CreateRequest());

        var result = await engine.RunOnceAsync();

        Assert.Equal(1, result.PermanentFailures);
        Assert.Equal(SyncOperationStatuses.PermanentFailure, operation.SyncStatus);
        Assert.Equal("invalid: rejected", operation.LastError);
    }

    [Fact]
    public async Task ConflictIsRecordedWithoutOverwritingOperation()
    {
        var store = new MemoryStore();
        var transport = new QueueTransport(new SyncTransportResult(
            SyncTransportOutcome.Conflict, "version_conflict", "Server changed", "{\"serverVersion\":7}"));
        var engine = CreateEngine(store, transport);
        var operation = await engine.EnqueueAsync(CreateRequest());

        var result = await engine.RunOnceAsync();

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(SyncOperationStatuses.Conflict, operation.SyncStatus);
        Assert.Equal("{\"serverVersion\":7}", operation.LastError);
        Assert.Equal("{\"value\":1}", operation.Payload);
    }

    [Fact]
    public async Task CancellationStopsActiveRunAndReleasesLock()
    {
        var store = new MemoryStore();
        var transport = new BlockingTransport();
        var engine = CreateEngine(store, transport);
        await engine.EnqueueAsync(CreateRequest());
        using var cancellation = new CancellationTokenSource();

        var run = engine.RunOnceAsync(cancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        transport.Release.TrySetResult();
        var next = await engine.RunOnceAsync();
        Assert.False(next.AlreadyRunning);
    }

    [Fact]
    public async Task ConcurrentRunOnceDoesNotStartSecondLoop()
    {
        var store = new MemoryStore();
        var transport = new BlockingTransport();
        var engine = CreateEngine(store, transport);
        await engine.EnqueueAsync(CreateRequest());

        var first = engine.RunOnceAsync();
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await engine.RunOnceAsync();
        transport.Release.TrySetResult();
        await first;

        Assert.True(second.AlreadyRunning);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task HttpTransportSendsOperationIdAsIdempotencyHeader()
    {
        var handler = new RecordingHandler();
        var transport = new HttpSyncTransport(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://sync.example/")
        });
        var operationId = Guid.NewGuid();
        var request = new SyncRequestOperation(operationId, "Student", "1", "Update",
            DateTimeOffset.UtcNow, "device", new { value = 1 });

        var result = await transport.SendAsync(request, CancellationToken.None);

        Assert.Equal(SyncTransportOutcome.Success, result.Outcome);
        Assert.Equal(operationId.ToString("D"), handler.IdempotencyKey);
    }

    [Fact]
    public async Task EnqueueRejectsInvalidJsonPayload()
    {
        var engine = CreateEngine(new MemoryStore(), new QueueTransport(Array.Empty<SyncTransportOutcome>()));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.EnqueueAsync(CreateRequest() with { Payload = "not-json" }));
    }

    private static SyncEngine CreateEngine(MemoryStore store, ISyncTransport transport, int maxRetries = 0) =>
        new(store, transport, new SyncEngineOptions
        {
            MaxTransientRetries = maxRetries,
            InitialRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero
        });

    private static EnqueueSyncOperation CreateRequest() => new(
        Guid.NewGuid(), "Student", "student-1", "Update", DateTimeOffset.UtcNow,
        "device-1", "{\"value\":1}");

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

        public Task<SyncOperation> EnqueueAsync(SyncOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_operations.GetOrAdd(operation.OperationId, operation));
        }

        public Task<IReadOnlyList<SyncOperation>> GetPendingAsync(int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SyncOperation> result = _operations.Values
                .Where(x => x.SyncStatus is SyncOperationStatuses.Pending or SyncOperationStatuses.RetryPending)
                .OrderBy(x => x.Timestamp)
                .ThenBy(x => x.OperationId)
                .Take(batchSize)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task UpdateAttemptAsync(Guid operationId, int attemptCount, string status, string? failure,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = _operations[operationId];
            operation.AttemptCount = attemptCount;
            operation.SyncStatus = status;
            operation.LastError = failure;
            return Task.CompletedTask;
        }
    }

    private sealed class QueueTransport : ISyncTransport
    {
        private readonly Queue<SyncTransportResult> _results;

        public QueueTransport(params SyncTransportOutcome[] outcomes)
            : this(outcomes.Select(x => new SyncTransportResult(x)).ToArray())
        {
        }

        public QueueTransport(params SyncTransportResult[] results) => _results = new Queue<SyncTransportResult>(results);
        public int CallCount { get; private set; }
        public List<Guid> SentOperationIds { get; } = [];

        public Task<SyncTransportResult> SendAsync(SyncRequestOperation operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            SentOperationIds.Add(operation.OperationId);
            return Task.FromResult(_results.Count == 0
                ? new SyncTransportResult(SyncTransportOutcome.Success)
                : _results.Dequeue());
        }
    }

    private sealed class BlockingTransport : ISyncTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<SyncTransportResult> SendAsync(SyncRequestOperation operation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new SyncTransportResult(SyncTransportOutcome.Success);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? IdempotencyKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            IdempotencyKey = request.Headers.GetValues(HttpSyncTransport.IdempotencyHeaderName).Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
