using System.Reflection;
using System.Xml.Linq;
using Yemekhane.Desktop;

namespace Yemekhane.UnitTests.Installer;

/// <summary>
/// Surum bilgisinin gorunur olmasini dogrular.
///
/// Sahada "hangi surumu kullaniyorsunuz?" sorusunun cevabi olmadan hata ayiklamak imkansizdir:
/// kullanicinin exe ozelliklerinden, uygulama basligindan ve Programlar listesinden surumu
/// gorebilmesi gerekir.
/// </summary>
public sealed class VersionVisibilityTests
{
    [Fact]
    public void AssemblyCarriesAProductVersion()
    {
        // Dosya ozelliklerinde ("Ayrintilar" sekmesi) surum gorunur olmali.
        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("1.0.0.0", version);
    }

    [Fact]
    public void AssemblyCarriesProductAndCompanyMetadata()
    {
        var assembly = typeof(App).Assembly;

        Assert.Equal("YemekhanePro", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.False(string.IsNullOrWhiteSpace(
            assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company));
    }

    [Fact]
    public void ApplicationExposesADisplayVersionForTheUi()
    {
        // Kullanici destek isterken surumu okuyabilmeli; yalnizca dosya ozelliklerinde olmasi yetmez.
        var version = AppVersion.Display;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }

    [Fact]
    public void BuildPropsDefineVersionSoEveryProjectStampsIt()
    {
        var props = XDocument.Load(Path.Combine(FindRoot(), "Directory.Build.props"));
        var names = props.Descendants().Select(element => element.Name.LocalName).ToArray();

        Assert.Contains("Version", names);
        Assert.Contains("Product", names);
        Assert.Contains("Company", names);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
