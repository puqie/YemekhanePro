using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Api.Infrastructure;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/settings/configuration")]
public sealed class ConfigurationDiagnosticsController(IConfiguration configuration, IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.SettingsRead)]
    public IActionResult Get()
    {
        var deployment = configuration.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>() ?? new();
        var database = configuration.GetSection("LocalDatabase").Get<LocalDatabaseOptions>() ?? new();
        return Ok(new
        {
            Environment = environment.EnvironmentName,
            deployment.Mode,
            deployment.TimeZone,
            ApiUrls = new { Http = Mask(configuration["Kestrel:Endpoints:Http:Url"]), Https = Mask(configuration["Kestrel:Endpoints:Https:Url"]) },
            DataDirectory = Mask(database.DataDirectory),
            Jwt = new { Issuer = configuration["Authentication:Jwt:Issuer"], Audience = configuration["Authentication:Jwt:Audience"], SigningKey = "***" },
            Sms = new { Provider = configuration["Sms:Provider"], Endpoint = Mask(configuration["Sms:Endpoint"]), Secret = "***" },
            Sync = new { Endpoint = Mask(configuration["Sync:Endpoint"]), Secret = "***" },
            Tls = new { CertificatePath = Mask(configuration["Kestrel:Endpoints:Https:Certificate:Path"]), Password = "***" }
        });
    }

    private static string? Mask(string? value) => string.IsNullOrWhiteSpace(value) ? null : "configured";
}
