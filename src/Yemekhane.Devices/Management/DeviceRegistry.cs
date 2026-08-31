using System.Collections.Concurrent;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Management;

public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<Guid, IDevice> _devices = new();

    public IReadOnlyCollection<IDevice> Devices => _devices.Values.ToArray();

    public void Register(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[device.Id] = device;
    }

    public bool Unregister(Guid deviceId) => _devices.TryRemove(deviceId, out _);

    public bool TryResolve(Guid deviceId, out IDevice? device) => _devices.TryGetValue(deviceId, out device);
}
