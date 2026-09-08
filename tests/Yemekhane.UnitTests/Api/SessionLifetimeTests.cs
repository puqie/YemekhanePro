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

    /// <summary>
    /// Acilis dogrulamasi 12 saati kabul etmeli. Onceki ust sinir 60 dakikaydi: kod ve ayar 720'ye
    /// cekilince yayimlanan API "1 ile 60 arasinda olmali" diye HIC BASLAMADI; birim testleri
    /// Program.cs'i gormedigi icin bunu yalnizca kurulum smoke'u yakaladi.
    /// </summary>
    [Fact]
    public void StartupValidationAcceptsTwelveHoursAndRejectsOutOfRange()
    {
        new JwtOptions().EnsureValid();
        new JwtOptions { AccessTokenMinutes = JwtOptions.MaxAccessTokenMinutes }.EnsureValid();
        Assert.Throws<InvalidOperationException>(() => new JwtOptions { AccessTokenMinutes = 0 }.EnsureValid());
        Assert.Throws<InvalidOperationException>(() => new JwtOptions { AccessTokenMinutes = JwtOptions.MaxAccessTokenMinutes + 1 }.EnsureValid());
    }

    /// <summary>Dagitilan ayar dosyasindaki deger acilis dogrulamasindan gecmeli; iki kaynak birlikte kayarsa yine yakalanir.</summary>
    [Fact]
    public void ShippedSettingsPassStartupValidation()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Yemekhane.sln"))) root = Path.GetDirectoryName(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "src", "Yemekhane.Api", "appsettings.json")));
        var minutes = document.RootElement.GetProperty("Authentication").GetProperty("Jwt").GetProperty("AccessTokenMinutes").GetInt32();

        new JwtOptions { AccessTokenMinutes = minutes }.EnsureValid();
        // Sinir JwtOptions'ta durur ama Program.cs onu cagirmazsa hic uygulanmaz.
        Assert.Contains("jwtOptions.EnsureValid();", File.ReadAllText(Path.Combine(root!, "src", "Yemekhane.Api", "Program.cs")), StringComparison.Ordinal);
    }

    /// <summary>Kilit suresi oturum suresinden bagimsizdir; 12 saat kilit olmamali.</summary>
    [Fact]
    public void LockoutDurationIsNotStretchedWithTheSession() =>
        Assert.Equal(15, new LoginLockoutOptions().DurationMinutes);
}
