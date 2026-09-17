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

/// <summary>
/// Turnikenin zamanlama kisiti. Ayri arayuz: ITurnstile sahteleri testlerde yasiyor, oraya uye
/// eklemek onlari kirardi. Uygulamayan turnike icin bekleme yapilmaz.
/// </summary>
public interface ITurnstileTiming
{
    /// <summary>Iki acma komutu arasinda birakilmasi gereken en az sure (turnikenin fiziksel dongusu).</summary>
    TimeSpan MinimumCommandInterval { get; }
}
public sealed record TurnstileResult(AccessDecision? AccessDecision, HardwareCommandOutcome HardwareOutcome,
    string Message, DeviceCommandResult? CommandResult = null);
