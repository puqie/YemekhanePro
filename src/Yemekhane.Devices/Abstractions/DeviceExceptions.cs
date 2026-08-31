namespace Yemekhane.Devices.Abstractions;

public sealed class DeviceConnectionException(string deviceName, string message, string? errorCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string DeviceName { get; } = deviceName;
    public string? ErrorCode { get; } = errorCode;
}

public sealed class DeviceCapabilityException(string deviceName, DeviceCapability capability)
    : Exception($"{deviceName} cihazı {capability} özelliğini desteklemiyor.");
