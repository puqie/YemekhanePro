using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Management;

public sealed record DeviceRegistrationOptions(bool IsActive = true, bool AutoConnect = true);

public sealed record DeviceStateChange(
    Guid DeviceId,
    string DeviceName,
    DeviceConnectionState PreviousState,
    DeviceConnectionState State,
    DateTimeOffset OccurredAt,
    DeviceStatus? Status = null,
    Exception? Exception = null,
    DeviceInfo? Info = null,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? NextRetryAt = null);

public sealed class DeviceStateChangedEventArgs(DeviceStateChange change) : EventArgs
{
    public DeviceStateChange Change { get; } = change;
}

public interface IDeviceDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemDeviceDelay : IDeviceDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
