using System.Text.Json;
using Yemekhane.Api.Authentication;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Oturum 12 saat acik kalmali: memur makineyi ogle arasinda ve bos derslerde kullanmadan
/// birakir; 15 dakikalik belirtec her donuste "oturum sona erdi" penceresi cikariyordu.
/// Iki kaynak birden korunur: kod varsayilani (ayar dosyasi eksikse) ve dagitilan
/// appsettings.json (kurulumla giden deger).
/// </summary>
public sealed class SessionLifetimeTests
{
    private const int TwelveHours = 720;

    [Fact]
    public void CodeDefaultIsTwelveHours() => Assert.Equal(TwelveHours, new JwtOptions().AccessTokenMinutes);

    [Fact]
    public void ShippedApiSettingsKeepTwelveHours()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Yemekhane.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var path = Path.Combine(root!, "src", "Yemekhane.Api", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var minutes = document.RootElement.GetProperty("Authentication").GetProperty("Jwt")
            .GetProperty("AccessTokenMinutes").GetInt32();

        Assert.Equal(TwelveHours, minutes);
    }

    /// <summary>Kilit suresi oturum suresinden bagimsizdir; 12 saat kilit olmamali.</summary>
    [Fact]
    public void LockoutDurationIsNotStretchedWithTheSession() =>
        Assert.Equal(15, new LoginLockoutOptions().DurationMinutes);
}
