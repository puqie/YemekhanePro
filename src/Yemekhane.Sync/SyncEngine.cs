using System.Text;
using System.Text.Json;
using Yemekhane.Application.Sync;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Sync;

public sealed class SyncEngine : IDisposable
{
    private readonly ISyncOperationStore _store;
    private readonly ISyncTransport _transport;
    private readonly SyncEngineOptions _options;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public SyncEngine(ISyncOperationStore store, ISyncTransport transport, SyncEngineOptions? options = null)
    {
        _store = store;
        _transport = transport;
        _options = options ?? new SyncEngineOptions();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxTransientRetries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxTotalAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxRetryDelay, _options.InitialRetryDelay);
        if (_options.InitialRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "InitialRetryDelay negatif olamaz.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxPayloadBytes);
    }

    public Task<SyncOperation> EnqueueAsync(EnqueueSyncOperation request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var operation = new SyncOperation
        {
            OperationId = request.OperationId,
            EntityName = request.Entity.Trim(),
            EntityId = request.EntityId.Trim(),
            OperationType = request.OperationType.Trim(),
            Timestamp = request.Timestamp,
            DeviceId = request.DeviceId.Trim(),
            Payload = request.Payload,
            SyncStatus = SyncOperationStatuses.Pending
        };

        return _store.EnqueueAsync(operation, cancellationToken);
    }

    public async Task<SyncRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new SyncRunResult(0, 0, 0, 0, 0, 0, AlreadyRunning: true);

        try
        {
            var operations = await _store.ClaimPendingAsync(_options.BatchSize, cancellationToken)
                .ConfigureAwait(false);
            var succeeded = 0;
            var duplicates = 0;
            var retryPending = 0;
            var permanentFailures = 0;
            var conflicts = 0;

            foreach (var operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await SendWithRetryAsync(operation, cancellationToken).ConfigureAwait(false);
                switch (result)
                {
                    case SyncTransportOutcome.Success: succeeded++; break;
                    case SyncTransportOutcome.Duplicate: duplicates++; break;
                    case SyncTransportOutcome.TransientFailure: retryPending++; break;
                    case SyncTransportOutcome.PermanentFailure: permanentFailures++; break;
                    case SyncTransportOutcome.Conflict: conflicts++; break;
                }
            }

            return new SyncRunResult(operations.Count, succeeded, duplicates, retryPending,
                permanentFailures, conflicts);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<SyncTransportOutcome> SendWithRetryAsync(SyncOperation operation,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(operation.Payload);
        var request = new SyncRequestOperation(operation.OperationId, operation.EntityName,
            operation.EntityId, operation.OperationType, operation.Timestamp, operation.DeviceId,
            payload.RootElement.Clone());
        var previousAttempts = operation.AttemptCount;

        for (var retry = 0; ; retry++)
        {
            var attempt = previousAttempts + retry + 1;
            await _store.UpdateAttemptAsync(operation.OperationId, attempt,
                SyncOperationStatuses.Processing, null, cancellationToken).ConfigureAwait(false);
            var result = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.Outcome is SyncTransportOutcome.Success or SyncTransportOutcome.Duplicate)
            {
                await _store.UpdateAttemptAsync(operation.OperationId, attempt,
                    SyncOperationStatuses.Synced, null, cancellationToken).ConfigureAwait(false);
                return result.Outcome;
            }

            var error = FormatError(result);
            if (result.Outcome == SyncTransportOutcome.Conflict)
            {
                await _store.UpdateAttemptAsync(operation.OperationId, attempt,
                    SyncOperationStatuses.Conflict, error, cancellationToken).ConfigureAwait(false);
                return result.Outcome;
            }

            if (result.Outcome == SyncTransportOutcome.PermanentFailure)
            {
                await _store.UpdateAttemptAsync(operation.OperationId, attempt,
                    SyncOperationStatuses.PermanentFailure, error, cancellationToken).ConfigureAwait(false);
                return result.Outcome;
            }

            // Toplam deneme siniri asildiysa islem kalici hataya tasinir. Aksi halde bekleyen
            // kuyrukta kalir, her turda yeniden secilir ve arkasindaki yeni kayitlar hic gonderilmez.
            if (attempt >= _options.MaxTotalAttempts)
            {
                await _store.UpdateAttemptAsync(operation.OperationId, attempt,
                    SyncOperationStatuses.PermanentFailure,
                    $"{error} (toplam {attempt} deneme sonrasi vazgecildi)", cancellationToken).ConfigureAwait(false);
                return SyncTransportOutcome.PermanentFailure;
            }

            await _store.UpdateAttemptAsync(operation.OperationId, attempt,
                SyncOperationStatuses.RetryPending, error, cancellationToken).ConfigureAwait(false);
            if (retry >= _options.MaxTransientRetries)
                return SyncTransportOutcome.TransientFailure;

            await Task.Delay(GetRetryDelay(retry), cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetRetryDelay(int retry)
    {
        var multiplier = Math.Pow(2, retry);
        var ticks = Math.Min(_options.InitialRetryDelay.Ticks * multiplier, _options.MaxRetryDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static string FormatError(SyncTransportResult result)
    {
        if (result.Outcome == SyncTransportOutcome.Conflict && !string.IsNullOrWhiteSpace(result.Conflict))
            return result.Conflict;
        if (string.IsNullOrWhiteSpace(result.ErrorCode)) return result.Message ?? result.Outcome.ToString();
        return string.IsNullOrWhiteSpace(result.Message)
            ? result.ErrorCode
            : $"{result.ErrorCode}: {result.Message}";
    }

    private void Validate(EnqueueSyncOperation request)
    {
        if (request.OperationId == Guid.Empty) throw new ArgumentException("OperationId boş olamaz.", nameof(request));
        ValidateRequired(request.Entity, nameof(request.Entity), 128);
        ValidateRequired(request.EntityId, nameof(request.EntityId), 128);
        ValidateRequired(request.OperationType, nameof(request.OperationType), 64);
        ValidateRequired(request.DeviceId, nameof(request.DeviceId), 128);
        if (request.Timestamp == default) throw new ArgumentException("Timestamp zorunludur.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Payload)) throw new ArgumentException("Payload zorunludur.", nameof(request));
        if (Encoding.UTF8.GetByteCount(request.Payload) > _options.MaxPayloadBytes)
            throw new ArgumentException("Payload izin verilen boyutu aşıyor.", nameof(request));

        try
        {
            using var document = JsonDocument.Parse(request.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Payload bir JSON nesnesi olmalıdır.", nameof(request));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Payload geçerli JSON olmalıdır.", nameof(request), exception);
        }
    }

    private static void ValidateRequired(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} zorunludur.", name);
        if (value.Trim().Length > maxLength) throw new ArgumentException($"{name} en fazla {maxLength} karakter olabilir.", name);
    }

    public void Dispose() => _runLock.Dispose();
}
