using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Yemekhane.Application.Realtime;

namespace Yemekhane.Api.Infrastructure;

public sealed partial class RealtimeEventPublisher(
    IHubContext<RealtimeHub, IRealtimeClient> hubContext,
    ILogger<RealtimeEventPublisher> logger) : BackgroundService, IRealtimeEventPublisher
{
    private const int QueueCapacity = 1024;
    private readonly Channel<QueuedEvent> _queue = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask PublishAsync(AccessDecisionCommittedEvent realtimeEvent,
        CancellationToken cancellationToken = default) => EnqueueAsync(
            new QueuedEvent(RealtimeChannels.AccessDecisions, realtimeEvent), cancellationToken);

    public ValueTask PublishAsync(TurnstileResultEvent realtimeEvent,
        CancellationToken cancellationToken = default) => EnqueueAsync(
            new QueuedEvent(RealtimeChannels.TurnstileResults, realtimeEvent), cancellationToken);

    public ValueTask PublishAsync(DeviceStatusChangedEvent realtimeEvent,
        CancellationToken cancellationToken = default) => EnqueueAsync(
            new QueuedEvent(RealtimeChannels.DeviceStatuses, realtimeEvent), cancellationToken);

    public ValueTask PublishAsync(NotificationEvent realtimeEvent,
        CancellationToken cancellationToken = default) => EnqueueAsync(
            new QueuedEvent(RealtimeChannels.Notifications, realtimeEvent), cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queuedEvent in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await BroadcastAsync(queuedEvent, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogBroadcastFailure(logger, exception, queuedEvent.Payload.GetType().Name);
            }
        }
    }

    private const int DropWarningThreshold = QueueCapacity - 1;
    private const int DropLogInterval = 100;
    private int _droppedEstimate;

    private ValueTask EnqueueAsync(QueuedEvent queuedEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Kanal DropOldest modunda oldugu icin TryWrite hep true doner; dolulukta en eski olay
        // sessizce dusurulur. Kaybi gorunur kilmak icin doluluk esigi asilinca uyariyoruz.
        if (!_queue.Writer.TryWrite(queuedEvent))
        {
            LogQueueClosed(logger, queuedEvent.Payload.GetType().Name);
            return ValueTask.CompletedTask;
        }

        if (_queue.Reader.Count >= DropWarningThreshold)
        {
            var dropped = Interlocked.Increment(ref _droppedEstimate);
            if (dropped % DropLogInterval == 1)
            {
                LogQueueSaturated(logger, _queue.Reader.Count, dropped);
            }
        }

        return ValueTask.CompletedTask;
    }

    private Task BroadcastAsync(QueuedEvent queuedEvent, CancellationToken cancellationToken) =>
        queuedEvent.Payload switch
        {
            AccessDecisionCommittedEvent value => hubContext.Clients.Group(queuedEvent.Channel)
                .AccessDecisionCommitted(value).WaitAsync(cancellationToken),
            TurnstileResultEvent value => hubContext.Clients.Group(queuedEvent.Channel)
                .TurnstileResult(value).WaitAsync(cancellationToken),
            DeviceStatusChangedEvent value => hubContext.Clients.Group(queuedEvent.Channel)
                .DeviceStatusChanged(value).WaitAsync(cancellationToken),
            NotificationEvent value => hubContext.Clients.Group(NotificationGroup(value))
                .Notification(value).WaitAsync(cancellationToken),
            _ => throw new InvalidOperationException($"Desteklenmeyen real-time event: {queuedEvent.Payload.GetType().Name}")
        };

    private static string NotificationGroup(NotificationEvent value) => value.AudienceUserId is { } userId
        ? RealtimeHub.UserGroup(userId.ToString("D"))
        : !string.IsNullOrWhiteSpace(value.AudiencePermission)
            ? RealtimeHub.PermissionGroup(value.AudiencePermission)
            : RealtimeChannels.Notifications;

    private sealed record QueuedEvent(string Channel, object Payload);

    [LoggerMessage(1, LogLevel.Warning, "Real-time event {EventType} yayınlanamadı.")]
    private static partial void LogBroadcastFailure(ILogger logger, Exception exception, string eventType);

    [LoggerMessage(2, LogLevel.Warning, "Real-time event kuyruğu kapalı; {EventType} atlandı.")]
    private static partial void LogQueueClosed(ILogger logger, string eventType);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning,
        Message = "Real-time olay kuyrugu doldu; en eski olaylar dusuruluyor. Kuyruk={QueueLength}, tahmini dusen={DroppedEstimate}")]
    private static partial void LogQueueSaturated(ILogger logger, int queueLength, int droppedEstimate);
}
