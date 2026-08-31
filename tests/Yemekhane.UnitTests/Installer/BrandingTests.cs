using System.Xml.Linq;

namespace Yemekhane.UnitTests.Installer;

/// <summary>
/// Marka varliklarinin gercekten pakete girdigini dogrular.
///
/// Simge dosyasinin depoda durmasi yetmez: exe'ye gomulmezse Windows genel bir pencere
/// simgesi gosterir ve kullanici gorev cubugunda uygulamayi ayirt edemez.
/// </summary>
public sealed class BrandingTests
{
    [Fact]
    public void IconIsEmbeddedInTheExecutable()
    {
        var project = XDocument.Load(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Yemekhane.Desktop.csproj"));
        var icon = project.Descendants().SingleOrDefault(element => element.Name.LocalName == "ApplicationIcon");

        Assert.NotNull(icon);
        Assert.False(string.IsNullOrWhiteSpace(icon!.Value));
        Assert.True(File.Exists(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", icon.Value.Replace('\\', Path.DirectorySeparatorChar))),
            $"ApplicationIcon dosyasi bulunamadi: {icon.Value}");
    }

    [Fact]
    public void IconFileIsAValidMultiSizeIcoNotARenamedPng()
    {
        // Windows kucuk boyutlari kendisi olceklemez; tek boyutlu bir .ico gorev
        // cubugunda bulanik gorunur.
        var path = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Assets", "app.ico");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadInt16());          // reserved
        Assert.Equal(1, reader.ReadInt16());          // type = icon
        Assert.True(reader.ReadInt16() >= 5, "Simge en az 5 farkli boyut icermelidir.");
    }

    [Fact]
    public void InstallerShowsTheProductIconInProgramsList()
    {
        // ARPPRODUCTICON olmadan Programlar listesinde genel bir MSI kutusu simgesi cikar.
        var document = XDocument.Load(Path.Combine(FindRoot(), "installer", "Package.wxs"));

        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "Icon");
        Assert.Contains(document.Descendants(),
            element => element.Name.LocalName == "Property"
                && (string?)element.Attribute("Id") == "ARPPRODUCTICON");
    }

    [Fact]
    public void ShortcutsUseTheProductIcon()
    {
        // Masaustu ve Baslat menusu kisayollari exe'nin simgesini almalidir.
        var document = XDocument.Load(Path.Combine(FindRoot(), "installer", "Package.wxs"));
        var shortcuts = document.Descendants()
            .Where(element => element.Name.LocalName == "Shortcut").ToArray();

        Assert.NotEmpty(shortcuts);
        Assert.All(shortcuts, shortcut =>
            Assert.False(string.IsNullOrWhiteSpace((string?)shortcut.Attribute("Icon"))));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
