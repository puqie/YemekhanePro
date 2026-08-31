using Yemekhane.Desktop;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Giris ekraninin ilk acilis ve mevcut kurulum senaryolarini dogru anlatmasini dogrular.
/// </summary>
public sealed class LoginWindowGuidanceTests
{
    [Fact]
    public void FreshInstallExplainsThePrefilledPassword()
    {
        var message = LoginWindow.BuildSetupMessage(
            new InitialAdminCredentials("admin", "gizli-parola-123"), hasExistingDatabase: false);

        Assert.Contains("otomatik dolduruldu", message);
    }

    [Fact]
    public void ExistingDatabaseExplainsWhyThePasswordIsBlank()
    {
        // Kullanici parolanin neden dolu gelmedigini bilmezse ne yapacagini da bilemez.
        var message = LoginWindow.BuildSetupMessage(initialAdmin: null, hasExistingDatabase: true);

        Assert.Contains("mevcut bir YemekhanePro veritaban", message);
    }

    [Fact]
    public void NormalStartupShowsNoSetupMessage()
    {
        Assert.Null(LoginWindow.BuildSetupMessage(initialAdmin: null, hasExistingDatabase: false));
    }

    [Fact]
    public void VersionIsShownOnTheLoginScreen()
    {
        Assert.Matches(@"^Sürüm \d+\.\d+\.\d+", LoginWindow.VersionText);
    }
}
