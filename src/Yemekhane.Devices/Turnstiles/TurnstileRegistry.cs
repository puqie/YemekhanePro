using System.Collections.Concurrent;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Turnstiles;

public sealed class TurnstileRegistry : ITurnstileResolver
{
    private readonly ConcurrentDictionary<Guid, Registration> _turnstiles = new();

    public void Register(ITurnstile turnstile, IReadOnlySet<DeviceCapability>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(turnstile);
        _turnstiles[turnstile.Id] = new Registration(turnstile, capabilities);
    }

    public bool Unregister(Guid deviceId) => _turnstiles.TryRemove(deviceId, out _);

    public bool TryResolve(Guid deviceId, out ITurnstile? turnstile)
    {
        if (_turnstiles.TryGetValue(deviceId, out var registration))
        {
            turnstile = registration.Turnstile;
            return true;
        }

        turnstile = null;
        return false;
    }

    public bool Supports(Guid deviceId, DeviceCapability capability)
    {
        if (!_turnstiles.TryGetValue(deviceId, out var registration))
        {
            return false;
        }

        return registration.Turnstile is IDeviceCapabilityProvider provider
            ? provider.Capabilities.Contains(capability)
            : registration.Capabilities?.Contains(capability) ?? capability == DeviceCapability.GrantAccess;
    }

    private sealed record Registration(ITurnstile Turnstile, IReadOnlySet<DeviceCapability>? Capabilities);
}
