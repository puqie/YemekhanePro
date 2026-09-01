using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Yemekhane.Application.Notifications;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Api.Infrastructure;

public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";
    public string Mode { get; init; } = "Local";
    public string TimeZone { get; init; } = "Europe/Istanbul";
    public string[] CorsOrigins { get; init; } = [];
    public string[] KnownProxies { get; init; } = [];
    public bool ForwardedHeadersEnabled { get; init; }
}

public sealed class DeviceRuntimeOptions
{
    public int HealthIntervalSeconds { get; init; } = 30;
    public int OperationTimeoutSeconds { get; init; } = 15;
}

public sealed class SchedulerOptions
{
    public int NotificationRetentionHours { get; init; } = 24;
}

public sealed class FileLoggingOptions
{
    public string? Directory { get; init; }
    public int RetentionDays { get; init; } = 30;
    public long FileSizeLimitBytes { get; init; } = 52_428_800;
}

public static class ProductionConfiguration
{
    public const string CorsPolicy = "ProductionCors";

    public static (DeploymentOptions Deployment, DeviceRuntimeOptions Devices, SchedulerOptions Schedulers,
        FileLoggingOptions Logging) Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var deployment = configuration.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>() ?? new();
        var devices = configuration.GetSection("Devices").Get<DeviceRuntimeOptions>() ?? new();
        var schedulers = configuration.GetSection("Schedulers").Get<SchedulerOptions>() ?? new();
        var logging = configuration.GetSection("Logging:File").Get<FileLoggingOptions>() ?? new();
        var errors = new List<string>();
        // Remote modun guvenlik kurallari (HTTPS, sertifika, proxy allowlist) ortamdan bagimsiz zorunludur:
        // yanlis ASPNETCORE_ENVIRONMENT ile acilan bir sunucunun duz HTTP kabul etmesi kabul edilemez.
        var securityErrors = new List<string>();

        if (deployment.Mode is not ("Local" or "Remote")) errors.Add("Deployment:Mode Local veya Remote olmalıdır.");
        if (!string.Equals(deployment.TimeZone, "Europe/Istanbul", StringComparison.Ordinal))
            errors.Add("Deployment:TimeZone Europe/Istanbul olmalıdır.");
        if (devices.HealthIntervalSeconds is < 5 or > 3600) errors.Add("Devices:HealthIntervalSeconds 5-3600 olmalıdır.");
        if (devices.OperationTimeoutSeconds is < 1 or > 300) errors.Add("Devices:OperationTimeoutSeconds 1-300 olmalıdır.");
        if (schedulers.NotificationRetentionHours is < 1 or > 168) errors.Add("Schedulers:NotificationRetentionHours 1-168 olmalıdır.");
        if (logging.RetentionDays is < 1 or > 3650) errors.Add("Logging:File:RetentionDays 1-3650 olmalıdır.");
        if (logging.FileSizeLimitBytes is < 1_048_576 or > 1_073_741_824) errors.Add("Logging:File:FileSizeLimitBytes 1 MiB-1 GiB olmalıdır.");

        var localDb = configuration.GetSection("LocalDatabase").Get<LocalDatabaseOptions>() ?? new();
        if (localDb.BusyTimeoutSeconds is < 1 or > 300) errors.Add("LocalDatabase:BusyTimeoutSeconds 1-300 olmalıdır.");
        var backup = configuration.GetSection("Backup");
        if (backup.GetValue<int>("RetentionCount") is < 1 or > 365) errors.Add("Backup:RetentionCount 1-365 olmalıdır.");
        if (!Enum.TryParse<Yemekhane.Infrastructure.Backup.BackupScheduleFrequency>(backup["Schedule"], true, out _))
            errors.Add("Backup:Schedule Daily veya Weekly olmalıdır.");
        if (!TimeOnly.TryParse(backup["Time"], out _)) errors.Add("Backup:Time geçerli bir saat olmalıdır.");
        var sms = configuration.GetSection("Sms");
        if (sms.GetValue<int>("TimeoutSeconds") is < 1 or > 300) errors.Add("Sms:TimeoutSeconds 1-300 olmalıdır.");
        if (string.Equals(sms["Provider"], "Http", StringComparison.OrdinalIgnoreCase) &&
            (!Uri.TryCreate(sms["Endpoint"], UriKind.Absolute, out var smsUri) || smsUri.Scheme != Uri.UriSchemeHttps))
            errors.Add("Http SMS provider geçerli bir HTTPS endpoint gerektirir.");
        var sync = configuration.GetSection("Sync");
        if (sync.GetValue<int>("IntervalMinutes") is < 1 or > 1440) errors.Add("Sync:IntervalMinutes 1-1440 olmalıdır.");
        if (sync.GetValue<int>("TimeoutSeconds") is < 1 or > 300) errors.Add("Sync:TimeoutSeconds 1-300 olmalıdır.");
        if (sync.GetValue<bool>("Enabled") &&
            (!Uri.TryCreate(sync["Endpoint"], UriKind.Absolute, out var syncUri) || syncUri.Scheme != Uri.UriSchemeHttps))
            errors.Add("Etkin sync geçerli bir HTTPS endpoint gerektirir.");
        try { _ = LocalDatabaseConnection.ResolveDataDirectory(localDb.DataDirectory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            errors.Add("LocalDatabase:DataDirectory geçerli bir mutlak veri yolu olmalıdır.");
        }

        if (deployment.Mode == "Remote")
        {
            var certificatePath = configuration["Kestrel:Endpoints:Https:Certificate:Path"];
            var certificatePassword = configuration["Kestrel:Endpoints:Https:Certificate:Password"];
            if (string.IsNullOrWhiteSpace(certificatePath)) securityErrors.Add("Remote mod Kestrel HTTPS sertifika yolu gerektirir.");
            if (string.IsNullOrWhiteSpace(certificatePassword)) securityErrors.Add("Remote mod sertifika parolasını environment üzerinden gerektirir.");
            if (configuration["Kestrel:Endpoints:Https:Url"] is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                securityErrors.Add("Remote mod yalnız HTTPS Kestrel endpoint kabul eder.");
            if (deployment.ForwardedHeadersEnabled && deployment.KnownProxies.Length == 0)
                securityErrors.Add("Forwarded headers etkinse Deployment:KnownProxies allowlist gereklidir.");
        }
        else if (configuration["Kestrel:Endpoints:Http:Url"] is { } localUrl &&
                 (!Uri.TryCreate(localUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
                  !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)))
            errors.Add("Local mod Kestrel HTTP endpoint'i sayısal loopback adresi olmalıdır.");

        if (securityErrors.Count > 0)
            throw new OptionsValidationException(DeploymentOptions.SectionName, typeof(DeploymentOptions), securityErrors);
        if (environment.IsProduction() && errors.Count > 0)
            throw new OptionsValidationException(DeploymentOptions.SectionName, typeof(DeploymentOptions), errors);
        return (deployment, devices, schedulers, logging);
    }

    public static void ConfigureLogging(WebApplicationBuilder builder, FileLoggingOptions options)
    {
        var directory = string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YemekhanePro", "logs")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.Directory));
        Directory.CreateDirectory(directory);
        builder.Host.UseSerilog((context, services, logger) => logger
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            // Serilog KENDI "Serilog:MinimumLevel" bolumunu okur; ASP.NET Core'un
            // "Logging:LogLevel" bolumu Serilog devredeyken YOK SAYILIR. Bu yuzden
            // gurultulu kaynaklar burada, kodda kisitlanir.
            //
            // EF Core her SQL komutunu Information seviyesinde yazar: olcumde
            // 60 istek 500 KB log uretmisti (istek basina ~8 KB). Kisitlanmazsa
            // okul bilgisayarinin diski dolar ve SQLite YAZAMAZ hale gelir.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "YemekhanePro.Api")
            .WriteTo.File(new JsonFormatter(), Path.Combine(directory, "api-.json"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetentionDays, fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true, shared: true));
    }

    public static void ConfigureNetwork(IServiceCollection services, DeploymentOptions options)
    {
        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy =>
        {
            if (options.CorsOrigins.Length > 0)
                policy.WithOrigins(options.CorsOrigins).AllowAnyHeader().AllowAnyMethod();
        }));
        if (!options.ForwardedHeadersEnabled) return;
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var value in options.KnownProxies)
                if (IPAddress.TryParse(value, out var address)) forwarded.KnownProxies.Add(address);
        });
    }

    /// <summary>Goc yedeklerinden en yenilerini tutar, eskileri siler.</summary>
    public const int RetainedMigrationBackups = 3;

    /// <summary>
    /// Eski goc yedeklerini siler. Her surum yukseltmesi bir kopya birakir;
    /// hicbiri silinmezse veritabani buyudukce disk dolar ve SQLite YAZAMAZ
    /// hale gelir -- yani sistem tamamen durur.
    ///
    /// En yeni <see cref="RetainedMigrationBackups"/> kopya KORUNUR: goc
    /// bozulursa geri donebilmek gerekir. Yalnizca "pre-migration-*.db"
    /// desenine uyan dosyalara dokunulur; canli veritabani ve WAL dosyalari
    /// bu desene uymaz.
    /// </summary>
    public static void PruneMigrationBackups(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            var stale = Directory.GetFiles(directory, "pre-migration-*.db")
                .OrderByDescending(path => path, StringComparer.Ordinal)   // ad = zaman damgasi
                .Skip(RetainedMigrationBackups);
            foreach (var path in stale)
            {
                // Silme hatasi gocu DURDURMAMALIDIR: yedek temizligi
                // yardimci bir istir, kritik yol degildir.
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class StartupReadiness
{
    public bool IsReady { get; set; }
}

public sealed partial class StartupDatabaseGuard(string connectionString, ILogger<StartupDatabaseGuard> logger)
{
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            return NullAsyncLock.Instance;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource))!;
        Directory.CreateDirectory(directory);
        var lockPath = Path.GetFullPath(dataSource) + ".migration.lock";
        FileStream migrationLock;
        try
        {
            migrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Veritabanı güncelleme kilidi alınamadı; başka bir YemekhanePro örneği çalışıyor olabilir.", exception);
        }
        if (File.Exists(dataSource))
        {
            var backup = Path.Combine(directory, $"pre-migration-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
            File.Copy(dataSource, backup, overwrite: false);
            LogMigrationBackup(logger, Path.GetFileName(backup));
            ProductionConfiguration.PruneMigrationBackups(directory);
        }
        await Task.CompletedTask;
        return new AsyncFileLock(migrationLock);
    }

    private sealed class AsyncFileLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private sealed class NullAsyncLock : IAsyncDisposable
    {
        public static NullAsyncLock Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [LoggerMessage(6301, LogLevel.Information, "Migration güvenlik yedeği oluşturuldu: {BackupFile}")]
    private static partial void LogMigrationBackup(Microsoft.Extensions.Logging.ILogger logger, string backupFile);
}

public sealed partial class NotificationRetentionWorker(IServiceScopeFactory scopes, TimeProvider timeProvider,
    SchedulerOptions options, ILogger<NotificationRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.NotificationRetentionHours), timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var count = await scope.ServiceProvider.GetRequiredService<INotificationRepository>()
                    .PurgeExpiredAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (count > 0) LogRetentionCompleted(logger, count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogRetentionFailure(logger, exception); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(6302, LogLevel.Information, "NotificationRetention tamamlandı; {Count} bildirim silindi.")]
    private static partial void LogRetentionCompleted(Microsoft.Extensions.Logging.ILogger logger, int count);

    [LoggerMessage(6303, LogLevel.Error, "NotificationRetention başarısız oldu.")]
    private static partial void LogRetentionFailure(Microsoft.Extensions.Logging.ILogger logger, Exception exception);
}
