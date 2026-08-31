using System.Net.Http.Headers;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sync;
using Yemekhane.Infrastructure.Settings;
using Yemekhane.Sync;
using Yemekhane.Application.Notifications;
using Yemekhane.Application.Common;

namespace Yemekhane.Api.Infrastructure;

public sealed class SettingsSyncRunner(ISettingsService settings, ISyncOperationStore store, IHttpClientFactory clients,
    NotificationService notifications, IConfiguration configuration)
{
    public async Task<SyncRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var document = await settings.GetAsync(cancellationToken);
        if (!document.Sync.Enabled)
            throw new InvalidOperationException("Sync etkin değil veya endpoint geçersiz.");
        var endpoint = await OutboundEndpointPolicy.ValidateAsync(document.Sync.Endpoint,
            configuration.GetValue<bool>("Security:OutboundEndpoints:AllowHttp"),
            configuration.GetValue<bool>("Security:OutboundEndpoints:AllowPrivateNetworks"),
            cancellationToken: cancellationToken);
        using var client = clients.CreateClient("outbound-secure"); client.BaseAddress = endpoint;
        var secret = await settings.GetSecretAsync(SettingsService.SyncSecretKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(secret)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var engine = new SyncEngine(store, new HttpSyncTransport(client));
        var result = await engine.RunOnceAsync(cancellationToken);
        if (result.Conflicts > 0 || result.PermanentFailures > 0 || result.RetryPending > 0)
            await notifications.CreateAsync(new CreateNotification(NotificationSeverities.Warning, "SyncAttentionRequired",
                "Senkronizasyon inceleme bekliyor",
                $"{result.Conflicts} çakışma, {result.PermanentFailures} kalıcı hata, {result.RetryPending} yeniden deneme.",
                RelatedRoute: "settings", AudiencePermission: "settings.read", DeduplicationKey: "sync:attention"), cancellationToken);
        return result;
    }
}

public sealed class SettingsSyncBackgroundWorker(IServiceScopeFactory scopes, TimeProvider timeProvider,
    ILogger<SettingsSyncBackgroundWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(5001, nameof(SettingsSyncBackgroundWorker)),
        "Zamanlanmış senkronizasyon başarısız oldu.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var document = await settings.GetAsync(stoppingToken);
                if (document.Sync.Enabled) await scope.ServiceProvider.GetRequiredService<SettingsSyncRunner>().RunAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(document.Sync.Enabled ? document.Sync.IntervalMinutes : 1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                LogFailure(logger, exception);
                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
            }
        }
    }
}
