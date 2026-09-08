using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Yazilimci iletisimi (puyi.com.tr, 0552 999 96 96) programin gorunen her penceresinde olmali;
/// AI kilavuzu const oldugu icin degerler orada elle yazilidir, sabitle ayni kalmali.
/// </summary>
public sealed class DeveloperContactTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yemekhane.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Yemekhane.sln bulunamadı.");
    }

    [Fact]
    public void ContactValues()
    {
        Assert.Equal("puyi.com.tr", DeveloperContact.Website);
        Assert.Equal("0552 999 96 96", DeveloperContact.Phone);
        Assert.Equal("0507 609 66 91", DeveloperContact.Phone2);
        Assert.Equal("Yazılım desteği: puyi.com.tr • 0552 999 96 96 • 0507 609 66 91", DeveloperContact.Line);
    }

    [Theory]
    [InlineData("src/Yemekhane.Desktop/Views/LoginWindow.xaml", "DeveloperContact.Line")]
    [InlineData("src/Yemekhane.Desktop/Views/ActivationWindow.xaml", "DeveloperContact.Line")]
    [InlineData("src/Yemekhane.Desktop/Views/PasswordResetWindow.xaml", "DeveloperContact.Line")]
    [InlineData("src/Yemekhane.Desktop/Views/SettingsView.xaml", "DeveloperContact.Line")]
    [InlineData("src/Yemekhane.Desktop/MainWindow.xaml", "DeveloperContact.Website")]
    [InlineData("src/Yemekhane.Desktop/MainWindow.xaml", "DeveloperContact.Phone")]
    [InlineData("src/Yemekhane.Desktop/MainWindow.xaml", "DeveloperContact.Phone2")]
    [InlineData("src/Yemekhane.Desktop/MainWindow.xaml", "DeveloperContact.Line")]
    public void EveryWindowBindsTheContact(string file, string binding) =>
        Assert.Contains("{x:Static services:" + binding + "}", File.ReadAllText(Path.Combine(Root, file)), StringComparison.Ordinal);

    [Fact]
    public void GuideAndInstallerCarryTheSameContact()
    {
        Assert.Contains(DeveloperContact.Website, AiUserGuidePrompt.Text, StringComparison.Ordinal);
        Assert.Contains("0552 999 96 96", AiUserGuidePrompt.Text, StringComparison.Ordinal);
        Assert.Contains(DeveloperContact.Phone2, AiUserGuidePrompt.Text, StringComparison.Ordinal);
        var wxs = File.ReadAllText(Path.Combine(Root, "installer", "Package.wxs"));
        Assert.Contains("Value=\"" + DeveloperContact.Website + "\"", wxs, StringComparison.Ordinal);
        Assert.Contains(DeveloperContact.Phone + " / " + DeveloperContact.Phone2, wxs, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/YemekhanePro", wxs, StringComparison.Ordinal);
    }
}
