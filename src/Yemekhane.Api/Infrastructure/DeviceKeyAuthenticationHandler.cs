using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Yemekhane.Api.Infrastructure;

public sealed class DeviceKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public IReadOnlyList<string> DeviceKeys { get; set; } = [];
}

public sealed class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<DeviceKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<DeviceKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceKey";
    public const string HeaderName = "X-Device-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || provided.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());

        var candidate = provided[0];
        if (string.IsNullOrWhiteSpace(candidate) || !IsKnownKey(candidate))
            return Task.FromResult(AuthenticateResult.Fail("Cihaz anahtarı geçersiz."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "device"), new Claim(ClaimTypes.Role, "Device")],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    private bool IsKnownKey(string candidate)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var matched = false;
        foreach (var key in Options.DeviceKeys)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            if (CryptographicOperations.FixedTimeEquals(candidateBytes, keyBytes)) matched = true;
        }
        return matched;
    }
}
