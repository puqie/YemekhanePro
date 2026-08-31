using Yemekhane.Application.Realtime;

namespace Yemekhane.UnitTests.Realtime;

internal sealed class RecordingRealtimeEventPublisher : IRealtimeEventPublisher
{
    public List<AccessDecisionCommittedEvent> AccessDecisions { get; } = [];
    public List<TurnstileResultEvent> TurnstileResults { get; } = [];
    public List<DeviceStatusChangedEvent> DeviceStatuses { get; } = [];
    public List<NotificationEvent> Notifications { get; } = [];

    public ValueTask PublishAsync(AccessDecisionCommittedEvent realtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccessDecisions.Add(realtimeEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(TurnstileResultEvent realtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TurnstileResults.Add(realtimeEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(DeviceStatusChangedEvent realtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeviceStatuses.Add(realtimeEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(NotificationEvent realtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Notifications.Add(realtimeEvent);
        return ValueTask.CompletedTask;
    }
}
