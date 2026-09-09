using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Yemekhane.Application.Sms;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Infrastructure.Sms;

public sealed class SmsDispatcher(
    EfSmsLogRepository repository,
    ISmsProvider provider,
    IOptions<SmsProviderOptions> options,
    TimeProvider timeProvider,
    SmsDispatchRunLock runLock,
    NotificationService? notifications = null,
    Yemekhane.Application.Settings.ISettingsService? settings = null)
{
    /// <summary>
    /// SMS ana anahtari kapaliysa hicbir mesaj GONDERILMEZ. Kuyruk temizlenmez: kurallar
    /// ve elle gonderim yazmaya devam eder, mesajlar bekler ve anahtar acilinca kaldigi
    /// yerden gider. Boylece "kapali" olmak veri kaybettirmez.
    /// </summary>
    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        if (settings is null) return true;
        try
        {
            var document = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
            return document.Sms.Enabled;
        }
        // Ayar okunamazsa gonderimi durdurmak sessiz veri kaybi gibi gorunur; acik varsayilir.
        catch (Exception exception) when (exception is not OperationCanceledException) { return true; }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken)) return 0;
        if (!await runLock.Gate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            var configuration = options.Value;
            var now = timeProvider.GetUtcNow();
            var batch = await repository.ClaimBatchAsync(now,
                TimeSpan.FromSeconds(configuration.StaleSendingSeconds), configuration.BatchSize, cancellationToken);
            var succeeded = 0;
            var failed = 0;
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SmsSendResult result;
                try
                {
                    result = await provider.SendAsync(new SmsSendRequest(item.Phone, item.Message), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    result = new SmsSendResult(SmsSendOutcome.TransientFailure,
                        ErrorCategory: SmsErrorCategory.Transport, ErrorCode: "provider_exception");
                }
                var delaySeconds = configuration.InitialRetrySeconds *
                    Math.Pow(2, Math.Max(0, item.AttemptCount - 1));
                var delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, configuration.MaxRetrySeconds));
                await repository.CompleteAsync(item.Id, item.ClaimToken!, result, configuration.Provider,
                    timeProvider.GetUtcNow(), configuration.MaxAttempts, delay, cancellationToken);
                if (result.IsSuccess) succeeded++; else failed++;
            }
            if (batch.Count > 0 && notifications is not null)
                await notifications.CreateAsync(new CreateNotification(
                    failed == 0 ? NotificationSeverities.Success : NotificationSeverities.Warning,
                    "SmsBatchCompleted", failed == 0 ? "SMS gönderimi tamamlandı" : "SMS gönderiminde hata var",
                    $"{succeeded} başarılı, {failed} başarısız sonuç.", RelatedRoute: "sms", AudiencePermission: "sms.read",
                    DeduplicationKey: $"sms-batch:{timeProvider.GetUtcNow():yyyyMMddHHmm}"), cancellationToken);
            return batch.Count;
        }
        finally
        {
            runLock.Gate.Release();
        }
    }
}

public sealed class SmsDispatchRunLock
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class SmsBackgroundDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<SmsProviderOptions> options,
    ILogger<SmsBackgroundDispatcher> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> DispatchCycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2901, nameof(DispatchCycleFailed)),
        "SMS dispatcher cycle failed; queued messages remain recoverable.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SmsDispatcher>().RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    DispatchCycleFailed(logger, null);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.Value.DispatchIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
