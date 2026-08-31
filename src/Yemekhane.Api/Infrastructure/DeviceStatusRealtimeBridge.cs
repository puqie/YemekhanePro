using Yemekhane.Application.Realtime;
using Yemekhane.Devices.Management;

namespace Yemekhane.Api.Infrastructure;

public sealed partial class DeviceStatusRealtimeBridge(
    DeviceManager deviceManager,
    IRealtimeEventPublisher realtimePublisher,
    ILogger<DeviceStatusRealtimeBridge> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        deviceManager.StateChanged += OnStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        deviceManager.StateChanged -= OnStateChanged;
        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs arguments)
    {
        var change = arguments.Change;
        _ = PublishAsync(new DeviceStatusChangedEvent(change.DeviceId,
            change.DeviceName, change.PreviousState.ToString(), change.State.ToString(), change.OccurredAt,
            change.Status?.CheckedAt, change.Status?.Message ?? change.Exception?.Message,
            change.Status?.ErrorCode, change.LastAttemptAt, change.NextRetryAt));
    }

    private async Task PublishAsync(DeviceStatusChangedEvent realtimeEvent)
    {
        try
        {
            await realtimePublisher.PublishAsync(realtimeEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogBridgeFailure(logger, exception);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Cihaz durum olayı real-time kuyruğuna aktarılamadı.")]
    private static partial void LogBridgeFailure(ILogger logger, Exception exception);
}
