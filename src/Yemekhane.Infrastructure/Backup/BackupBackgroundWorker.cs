using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Yemekhane.Infrastructure.Backup;

public sealed class BackupBackgroundWorker(
    BackupService backupService,
    BackupOptions options,
    TimeProvider timeProvider,
    ILogger<BackupBackgroundWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogScheduledBackupFailure =
        LoggerMessage.Define(LogLevel.Error, new EventId(3601, nameof(LogScheduledBackupFailure)),
            "Zamanlanmış veritabanı backup işlemi başarısız oldu.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ScheduleEnabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var next = BackupSchedule.GetNextRun(now, options, IstanbulTimeZone);
            try
            {
                await Task.Delay(next - now, timeProvider, stoppingToken).ConfigureAwait(false);
                await backupService.CreateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogScheduledBackupFailure(logger, exception);
            }
        }
    }

    private static TimeZoneInfo IstanbulTimeZone
    {
        get
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        }
    }
}
