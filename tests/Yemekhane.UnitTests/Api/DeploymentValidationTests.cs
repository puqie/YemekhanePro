using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yemekhane.Api.Infrastructure;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Dagitim dogrulamasi guvenlik sinirlarini korur: Remote mod HTTPS ve sertifika zorunlu kilar.
/// Bu kurallarin yalnizca ASPNETCORE_ENVIRONMENT=Production iken degil, Remote modun kendisinde
/// gecerli olmasi gerekir; aksi halde yanlis ortam degiskeniyle acilan sunucu duz HTTP kabul eder.
/// </summary>
public sealed class DeploymentValidationTests
{
    [Fact]
    public void RemoteModeWithoutHttpsIsRejectedEvenOutsideProductionEnvironment()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Remote",
            ["Deployment:TimeZone"] = "Europe/Istanbul",
            ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5000"
        });

        Assert.Throws<OptionsValidationException>(
            () => ProductionConfiguration.Validate(configuration, new StubEnvironment("Development")));
    }

    [Fact]
    public void RemoteModeWithForwardedHeadersRequiresProxyAllowlistOutsideProduction()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Remote",
            ["Deployment:TimeZone"] = "Europe/Istanbul",
            ["Deployment:ForwardedHeadersEnabled"] = "true",
            ["Kestrel:Endpoints:Https:Url"] = "https://okul.example:5001",
            ["Kestrel:Endpoints:Https:Certificate:Path"] = "cert.pfx",
            ["Kestrel:Endpoints:Https:Certificate:Password"] = "secret"
        });

        Assert.Throws<OptionsValidationException>(
            () => ProductionConfiguration.Validate(configuration, new StubEnvironment("Development")));
    }

    [Fact]
    public void ValidRemoteConfigurationIsAcceptedOutsideProduction()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Remote",
            ["Deployment:TimeZone"] = "Europe/Istanbul",
            ["Kestrel:Endpoints:Https:Url"] = "https://okul.example:5001",
            ["Kestrel:Endpoints:Https:Certificate:Path"] = "cert.pfx",
            ["Kestrel:Endpoints:Https:Certificate:Password"] = "secret"
        });

        var result = ProductionConfiguration.Validate(configuration, new StubEnvironment("Development"));

        Assert.Equal("Remote", result.Deployment.Mode);
    }

    [Fact]
    public void LocalDevelopmentConfigurationRemainsPermissive()
    {
        // Local mod gelistiricinin isini engellememelidir: gecersiz ayarlar Production disinda tolere edilir.
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Local",
            ["Deployment:TimeZone"] = "Europe/Istanbul",
            ["Devices:HealthIntervalSeconds"] = "1"
        });

        var result = ProductionConfiguration.Validate(configuration, new StubEnvironment("Development"));

        Assert.Equal("Local", result.Deployment.Mode);
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["LocalDatabase:BusyTimeoutSeconds"] = "30",
            ["Backup:RetentionCount"] = "10",
            ["Sms:TimeoutSeconds"] = "30"
        };
        foreach (var pair in values) defaults[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Yemekhane.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
