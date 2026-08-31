using Yemekhane.Application.Access;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Turnstiles;

public interface ITurnstileResolver
{
    bool TryResolve(Guid deviceId, out ITurnstile? turnstile);
    bool Supports(Guid deviceId, DeviceCapability capability);
}

public enum HardwareCommandOutcome
{
    Succeeded,
    Skipped,
    DeviceNotFound,
    Disconnected,
    CapabilityNotSupported,
    TimedOut,
    Cancelled,
    Failed,
    ReviewRequired,
    CompensatedRetryRequired
}

public sealed record TurnstileResult(AccessDecision? AccessDecision, HardwareCommandOutcome HardwareOutcome,
    string Message, DeviceCommandResult? CommandResult = null);
