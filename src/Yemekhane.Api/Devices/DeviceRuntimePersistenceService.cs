using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Api.Devices;

public sealed partial class DeviceRuntimePersistenceService(
    DeviceManager manager, IDeviceAdapterFactory factory, IServiceScopeFactory scopeFactory,
    ILogger<DeviceRuntimePersistenceService> logger) : IHostedService
{
    private readonly ConcurrentDictionary<Task, byte> pendingWrites = new();
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var devices = await db.Devices.AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        foreach (var entity in devices)
        {
            try
            {
                manager.Register(factory.Create(DeviceAdministrationService.Configuration(entity)),
                    new DeviceRegistrationOptions(entity.IsActive, entity.AutoConnect));
            }
            catch (Exception exception)
            {
                LogRegistrationFailure(logger, entity.Id, exception);
                // Liste okundıktan sonra cihaz silinmis olabilir; SingleAsync burada firlatirsa
                // catch blogundan kacip IHostedService.StartAsync'i ve tum API baslangicini dusurur.
                var tracked = await db.Devices.SingleOrDefaultAsync(x => x.Id == entity.Id, cancellationToken);
                if (tracked is null) continue;
                tracked.ConnectionStatus = "Error";
                tracked.LastStatusAt = DateTimeOffset.UtcNow;
                db.DeviceEvents.Add(new DeviceEvent { DeviceId = entity.Id, Timestamp = tracked.LastStatusAt.Value,
                    EventType = "registration", Severity = "Error", Message = exception.Message });
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        manager.StateChanged += OnStateChanged;
        await manager.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        manager.StateChanged -= OnStateChanged;
        await manager.ShutdownAsync(cancellationToken);
        await Task.WhenAll(pendingWrites.Keys).WaitAsync(cancellationToken);
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs arguments)
    {
        var write = PersistAsync(arguments.Change);
        pendingWrites.TryAdd(write, 0);
        _ = write.ContinueWith(task => pendingWrites.TryRemove(task, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task PersistAsync(DeviceStateChange change)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
            var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == change.DeviceId);
            if (device is null) return;
            var status = DeviceAdministrationService.StatusName(change.State);
            var statusAt = change.Status?.CheckedAt ?? change.OccurredAt;
            // Yonetici bu arada cihazi yeniden yapilandirmis olabilir (UpdateAsync LastStatusAt'i ileri alir).
            // Bayat bir donanim sonucu, yoneticinin bilerek yazdigi durumu ezmemeli.
            var superseded = device.LastStatusAt > statusAt;
            if (!superseded)
            {
                device.ConnectionStatus = status;
                device.LastStatusAt = statusAt;
                if (change.State == DeviceConnectionState.Connected)
                    device.LastConnectedAt = change.OccurredAt;
                if (change.Info is not null)
                {
                    device.Model = change.Info.Model;
                    device.SerialNumber = change.Info.SerialNumber;
                    device.Firmware = change.Info.Firmware;
                }
            }

            var message = change.Status?.Message ?? change.Exception?.Message ?? $"Durum: {status}";
            db.DeviceEvents.Add(new DeviceEvent { DeviceId = change.DeviceId, Timestamp = change.OccurredAt,
                EventType = "status", Severity = change.State == DeviceConnectionState.Faulted ? "Error" : "Information",
                Message = message, PayloadJson = JsonSerializer.Serialize(new { Previous = change.PreviousState.ToString(),
                    Current = change.State.ToString(), ErrorCode = change.Status?.ErrorCode }) });
            await db.SaveChangesAsync();
            var notification = NotificationFor(change, device.Name, status, message);
            if (notification is not null)
                await scope.ServiceProvider.GetRequiredService<NotificationService>().CreateAsync(notification);
        }
        catch (Exception exception)
        {
            LogPersistenceFailure(logger, change.DeviceId, exception);
        }
    }

    private static CreateNotification? NotificationFor(DeviceStateChange change, string deviceName,
        string status, string message)
    {
        if (change.PreviousState == change.State) return null;
        var route = $"devices/{change.DeviceId:D}";
        return change.State switch
        {
            DeviceConnectionState.Connected when change.PreviousState is DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted or DeviceConnectionState.Reconnecting =>
                new(NotificationSeverities.Success, "DeviceReconnected", "Cihaz yeniden bağlandı",
                    $"{deviceName} bağlantısı yeniden kuruldu.", "Device", change.DeviceId.ToString("D"), route, AudiencePermission: "devices.read",
                    DeduplicationKey: $"device:{change.DeviceId:D}:connected"),
            DeviceConnectionState.Disconnected =>
                new(NotificationSeverities.Warning, "DeviceDisconnected", "Cihaz bağlantısı kesildi",
                    $"{deviceName} çevrimdışı oldu.", "Device", change.DeviceId.ToString("D"), route, AudiencePermission: "devices.read",
                    DeduplicationKey: $"device:{change.DeviceId:D}:disconnected"),
            DeviceConnectionState.Faulted =>
                new(NotificationSeverities.Error, "DeviceError", "Cihaz hatası",
                    $"{deviceName}: {message}", "Device", change.DeviceId.ToString("D"), route, AudiencePermission: "devices.read",
                    DeduplicationKey: $"device:{change.DeviceId:D}:error:{change.Status?.ErrorCode ?? "unknown"}"),
            _ => null
        };
    }

    [LoggerMessage(1, LogLevel.Error, "{DeviceId} cihaz adapter'ı kaydedilemedi.")]
    private static partial void LogRegistrationFailure(ILogger logger, Guid deviceId, Exception exception);

    [LoggerMessage(2, LogLevel.Warning, "{DeviceId} cihaz runtime durumu kalıcılaştırılamadı.")]
    private static partial void LogPersistenceFailure(ILogger logger, Guid deviceId, Exception exception);
}
