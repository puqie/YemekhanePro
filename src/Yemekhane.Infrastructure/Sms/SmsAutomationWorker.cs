using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yemekhane.Application.Sms;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Her dakika Istanbul saatini kontrol eder; hak uyarisi saati geldiyse ve bugun kosulmadiysa
/// <see cref="SmsAutomationService.RunScheduledAsync"/> ile veli uyarilarini kuyruklar.
/// Karar mantigi serviste (saf, test edilebilir); burada yalnizca dongu ve hata yalitimi var.
/// </summary>
public sealed class SmsAutomationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SmsAutomationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2952, nameof(CycleFailed)),
        "Otomatik SMS zamanlayıcı turu başarısız; bir sonraki dakikada yeniden denenecek.");

    private static readonly Action<ILogger, Exception?> RunCompleted = LoggerMessage.Define(
        LogLevel.Information, new EventId(2953, nameof(RunCompleted)),
        "Günlük yemek hakkı uyarısı SMS'leri kuyruğa alındı.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<SmsAutomationService>();
                    if (await service.RunScheduledAsync(stoppingToken)) RunCompleted(logger, null);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { CycleFailed(logger, exception); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
