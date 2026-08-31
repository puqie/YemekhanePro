using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Sf300;

/// <summary>
/// Defines the documented SF300 protocol boundary. Implementations own transport framing and validation.
/// </summary>
public interface ISf300Protocol : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken);
    Task<DeviceStatus?> GetStatusAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<CardReadEvent> ReadCardsAsync(CancellationToken cancellationToken);
    Task<DeviceCommandResult?> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken);
    Task<DeviceCommandResult?> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken);
    Task<DeviceCommandResult?> SendUserAsync(DeviceUser user, CancellationToken cancellationToken);
    Task<DeviceCommandResult?> SendCardAsync(string cardNumber, string externalUserId, CancellationToken cancellationToken);
    Task<DeviceCommandResult?> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken);
    Task<DeviceCommandResult?> SyncCardAsync(string cardNumber, string externalUserId, CancellationToken cancellationToken);
    Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken);
    Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken);
}

/// <summary>Reports a protocol-level failure and whether repeating the request is safe.</summary>
public sealed class Sf300ProtocolException(string message, bool isTransient = false,
    string errorCode = "SF300_PROTOCOL_ERROR", Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
    public string ErrorCode { get; } = string.IsNullOrWhiteSpace(errorCode)
        ? "SF300_PROTOCOL_ERROR"
        : errorCode;
}
