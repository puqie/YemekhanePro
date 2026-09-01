namespace Yemekhane.Devices.Abstractions;

public enum DeviceConnectionState { Disconnected, Connecting, Connected, Reconnecting, Faulted }
public enum TurnstileDirection { Entry, Exit }
public enum DeviceCapability { ReadCard, ReadUser, SendCard, SendUser, SyncCard, SyncUser, DeleteCard, GrantAccess, DenyAccess, DeviceInfo, Status }

public sealed record DeviceEndpoint(string ConnectionType, string? ComPort = null, int? BaudRate = null,
    string? IpAddress = null, int? IpPort = null);
public sealed record DeviceInfo(string Model, string? SerialNumber, string? Firmware, IReadOnlySet<DeviceCapability> Capabilities);
public sealed record DeviceStatus(DeviceConnectionState State, DateTimeOffset CheckedAt, string? Message = null, string? ErrorCode = null);
public sealed record CardReadEvent(string CardNumber, DateTimeOffset Timestamp, string ReaderSource);
public sealed record DeviceCommandResult(bool Succeeded, string Message, string? ErrorCode = null);
public sealed record DeviceUser(string ExternalId, string Name, string? CardNumber, string? FingerprintId, string? Pid);

public interface IDevice : IAsyncDisposable
{
    Guid Id { get; }
    string Name { get; }
    DeviceEndpoint Endpoint { get; }
    DeviceConnectionState ConnectionState { get; }
    Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken);
}

public interface ICardReader : IDevice
{
    IAsyncEnumerable<CardReadEvent> ReadCardsAsync(CancellationToken cancellationToken);
}

public interface ITurnstile : IDevice
{
    Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken);
    Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken);
}

public interface IDeviceCapabilityProvider
{
    IReadOnlySet<DeviceCapability> Capabilities { get; }
}

public interface IAccessController : ICardReader, ITurnstile, IDeviceCapabilityProvider
{
    Task<DeviceCommandResult> SendUserAsync(DeviceUser user, CancellationToken cancellationToken);
    Task<DeviceCommandResult> SendCardAsync(string cardNumber, string externalUserId, CancellationToken cancellationToken);
    Task<DeviceCommandResult> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken);
    Task<DeviceCommandResult> SyncCardAsync(string cardNumber, string externalUserId, CancellationToken cancellationToken);
    /// <summary>Karti cihazdan siler. Iptal edilen kart cihazda kalirsa turnikeden gecmeye devam eder.</summary>
    Task<DeviceCommandResult> DeleteCardAsync(string cardNumber, CancellationToken cancellationToken);
    Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken);
    Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken);
}
