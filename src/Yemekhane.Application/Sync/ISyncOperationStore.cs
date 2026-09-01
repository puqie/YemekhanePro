using Yemekhane.Domain.Entities;

namespace Yemekhane.Application.Sync;

public interface ISyncOperationStore
{
    Task<SyncOperation> EnqueueAsync(SyncOperation operation, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncOperation>> GetPendingAsync(int batchSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncOperation>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken) =>
        GetPendingAsync(batchSize, cancellationToken);
    Task UpdateAttemptAsync(Guid operationId, int attemptCount, string status, string? failure,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cakisma nedeniyle duran islemler. Motor bunlari kendiliginden cozemez: hangi tarafin
    /// kazanacagi is kararidir. Gorunur olmazlarsa kuyrukta sessizce olu kalirlar.
    /// </summary>
    Task<IReadOnlyList<SyncOperation>> GetConflictsAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Cakisan bir islemi yeniden kuyruga alir. Yalnizca Conflict durumundakiler kabul edilir:
    /// gonderilmis bir islemi yeniden kuyruga almak ayni degisikligi ikinci kez gondermek olurdu.
    /// </summary>
    Task RequeueAsync(Guid operationId, CancellationToken cancellationToken);
}

public static class SyncOperationStatuses
{
    public const string Pending = "Pending";
    public const string RetryPending = "RetryPending";
    public const string Processing = "Processing";
    public const string Synced = "Synced";
    public const string Succeeded = Synced;
    public const string PermanentFailure = "PermanentFailure";
    public const string Conflict = "Conflict";
}
