using System.Text.Json.Serialization;

namespace Yemekhane.Sync;

public sealed record EnqueueSyncOperation(
    Guid OperationId,
    string Entity,
    string EntityId,
    string OperationType,
    DateTimeOffset Timestamp,
    string DeviceId,
    string Payload);

public sealed record SyncRequestOperation(
    Guid OperationId,
    string Entity,
    string EntityId,
    string OperationType,
    DateTimeOffset Timestamp,
    string DeviceId,
    [property: JsonPropertyName("payload")] object Payload);

[JsonConverter(typeof(JsonStringEnumConverter<SyncTransportOutcome>))]
public enum SyncTransportOutcome
{
    Success,
    Duplicate,
    TransientFailure,
    PermanentFailure,
    Conflict
}

public sealed record SyncServerResponse(
    Guid OperationId,
    SyncTransportOutcome Outcome,
    string? ErrorCode = null,
    string? Message = null,
    string? Conflict = null);

public sealed record SyncTransportResult(
    SyncTransportOutcome Outcome,
    string? ErrorCode = null,
    string? Message = null,
    string? Conflict = null);

public interface ISyncTransport
{
    Task<SyncTransportResult> SendAsync(SyncRequestOperation operation, CancellationToken cancellationToken);
}

public sealed class SyncEngineOptions
{
    public int BatchSize { get; init; } = 100;
    public int MaxTransientRetries { get; init; } = 3;
    /// <summary>
    /// Bir islemin tum calistirmalar boyunca toplam deneme siniri. Bu sinira ulasan islem
    /// kalici hataya tasinir; aksi halde surekli basarisiz olan islemler bekleyen kuyrukta
    /// kalir, her turda yeniden secilir ve arkalarindaki yeni kayitlarin gonderilmesini engeller.
    /// </summary>
    public int MaxTotalAttempts { get; init; } = 24;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPayloadBytes { get; init; } = 1_048_576;
}

public sealed record SyncRunResult(
    int Processed,
    int Succeeded,
    int DuplicateAccepted,
    int RetryPending,
    int PermanentFailures,
    int Conflicts,
    bool AlreadyRunning = false);
