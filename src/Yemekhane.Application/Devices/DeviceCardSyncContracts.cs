namespace Yemekhane.Application.Devices;

/// <summary>Bir kartin tek bir cihazdaki senkronizasyon durumu.</summary>
public static class DeviceCardSyncStatus
{
    /// <summary>Cihaza yuklenmeyi bekliyor.</summary>
    public const string Pending = "Pending";

    /// <summary>Cihazda kayitli ve dogrulandi.</summary>
    public const string Loaded = "Loaded";

    /// <summary>Cihazdan silinmeyi bekliyor (kart pasife alindi veya degistirildi).</summary>
    public const string PendingRemoval = "PendingRemoval";

    /// <summary>Kalici hata; yeniden denenmez, operator mudahalesi gerekir.</summary>
    public const string Failed = "Failed";

    /// <summary>Cihazdan basariyla silindi.</summary>
    public const string Removed = "Removed";
}

/// <summary>Cihaza gonderilmeyi bekleyen tek bir kart islemi.</summary>
public sealed record PendingDeviceCard(Guid CardId, Guid StudentId, string CardNumber, string StudentName,
    bool IsRemoval, int AttemptCount);

/// <summary>Bir kartin tek bir cihazdaki durumunun operator gorunumu.</summary>
public sealed record DeviceCardStatusRow(Guid DeviceId, string DeviceName, string Status,
    DateTimeOffset? LastSyncedAt, int AttemptCount, string? LastError);

/// <summary>Bir cihazin kart yukleme ozeti.</summary>
public sealed record DeviceCardSummary(Guid DeviceId, string DeviceName, int Loaded, int Pending, int Failed);

public interface IDeviceCardSyncService
{
    /// <summary>Karti tum aktif cihazlara yuklenmek uzere kuyruga alir.</summary>
    Task QueueCardAsync(Guid cardId, CancellationToken cancellationToken);

    /// <summary>Karti tum cihazlardan silinmek uzere kuyruga alir.</summary>
    Task QueueRemovalAsync(Guid cardId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit, CancellationToken cancellationToken);
    Task MarkLoadedAsync(Guid deviceId, Guid cardId, CancellationToken cancellationToken);
    Task MarkRemovedAsync(Guid deviceId, Guid cardId, CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid deviceId, Guid cardId, string failure, bool isPermanent, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceCardSummary>> GetDeviceSummariesAsync(CancellationToken cancellationToken);
}
