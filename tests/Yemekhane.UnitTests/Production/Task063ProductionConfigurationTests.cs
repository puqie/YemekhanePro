using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Yemekhane.Api.Infrastructure;

namespace Yemekhane.UnitTests.Production;

public sealed class Task063ProductionConfigurationTests
{
    [Fact]
    public void ProductionArtifactsContainNoSecrets()
    {
        var root = FindRoot();
        foreach (var file in Directory.GetFiles(root, "appsettings*.json", SearchOption.AllDirectories)
                     .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                 !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !x.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")))
        {
            var json = File.ReadAllText(file);
            Assert.DoesNotMatch("(?i)\\\"SigningKey\\\"\\s*:\\s*\\\"[^\\\"\\s]+", json);
            Assert.DoesNotMatch("(?i)\\\"Password\\\"\\s*:\\s*\\\"[^\\\"\\s]+", json);
        }
    }

    [Fact]
    public void MissingRemoteCertificateSecretFailsFast()
    {
        var values = ValidValues();
        values["Deployment:Mode"] = "Remote";
        values["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:5255";
        values["Kestrel:Endpoints:Https:Certificate:Path"] = "server.pfx";
        Assert.Throws<OptionsValidationException>(() => ProductionConfiguration.Validate(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new ProductionEnvironment()));
    }

    [Fact]
    public void LocalAndCompleteRemoteProfilesValidate()
    {
        var local = ValidValues();
        ProductionConfiguration.Validate(new ConfigurationBuilder().AddInMemoryCollection(local).Build(), new ProductionEnvironment());

        var remote = ValidValues();
        remote["Deployment:Mode"] = "Remote";
        remote["Deployment:ForwardedHeadersEnabled"] = "true";
        remote["Deployment:KnownProxies:0"] = "192.0.2.10";
        remote["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:5255";
        remote["Kestrel:Endpoints:Https:Certificate:Path"] = "server.pfx";
        remote["Kestrel:Endpoints:Https:Certificate:Password"] = "environment-secret";
        ProductionConfiguration.Validate(new ConfigurationBuilder().AddInMemoryCollection(remote).Build(), new ProductionEnvironment());
    }

    [Fact]
    public void ProductionSchedulersAreRegisteredExactlyOnce()
    {
        var program = File.ReadAllText(Path.Combine(FindRoot(), "src", "Yemekhane.Api", "Program.cs"));
        Assert.Equal(1, Count(program, "AddHostedService<NotificationRetentionWorker>"));
        Assert.Equal(1, Count(program, "AddHostedService<SettingsSyncBackgroundWorker>"));
        Assert.Equal(1, Count(program, "AddHostedService<DeviceRuntimePersistenceService>"));
    }

    [Fact]
    public async Task MigrationLockIsExclusiveAndReleasedOnGracefulShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YemekhanePro-Task063-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connection = $"Data Source={Path.Combine(directory, "test.db")}";
            var guard = new StartupDatabaseGuard(connection, NullLogger<StartupDatabaseGuard>.Instance);
            await using (var first = await guard.AcquireAsync(CancellationToken.None))
                await Assert.ThrowsAsync<InvalidOperationException>(() => guard.AcquireAsync(CancellationToken.None));
            await using var afterShutdown = await guard.AcquireAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<string, string?> ValidValues() => new()
    {
        ["Deployment:Mode"] = "Local", ["Deployment:TimeZone"] = "Europe/Istanbul",
        ["Devices:HealthIntervalSeconds"] = "30", ["Devices:OperationTimeoutSeconds"] = "15",
        ["Schedulers:NotificationRetentionHours"] = "24", ["Logging:File:RetentionDays"] = "30",
        ["Logging:File:FileSizeLimitBytes"] = "52428800", ["LocalDatabase:BusyTimeoutSeconds"] = "5",
        ["Backup:RetentionCount"] = "14", ["Backup:Schedule"] = "Daily", ["Backup:Time"] = "02:00:00",
        ["Sms:Provider"] = "Http", ["Sms:Endpoint"] = "https://sms.invalid/", ["Sms:TimeoutSeconds"] = "30",
        ["Sync:Enabled"] = "false", ["Sync:IntervalMinutes"] = "5", ["Sync:TimeoutSeconds"] = "30"
    };

    private static int Count(string value, string token) => value.Split(token, StringSplitOptions.None).Length - 1;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
