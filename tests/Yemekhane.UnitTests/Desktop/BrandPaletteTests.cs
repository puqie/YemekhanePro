using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Marka paletinin okunabilir kaldigini dogrular.
///
/// Logonun parlak turuncusu (#FA5103) beyaz zeminde yalnizca 3.36:1 kontrast verir; WCAG AA
/// metin esigi 4.5:1'dir. Marka rengini oldugu gibi metne uygulamak ekrani "markali" ama
/// okunamaz hale getirir -- gun isigi vuran bir yemekhane ofisinde bu gercek bir sorundur.
/// </summary>
public sealed class BrandPaletteTests
{
    private const string White = "#FFFFFF";

    [Theory]
    [InlineData("AccentBrush")]
    [InlineData("DangerBrush")]
    [InlineData("SuccessBrush")]
    [InlineData("WarningBrush")]
    [InlineData("InfoBrush")]
    [InlineData("InkBrush")]
    [InlineData("MutedBrush")]
    public void ForegroundBrushesMeetWcagAaOnWhite(string key)
    {
        var color = ReadBrush(key);

        Assert.True(Contrast(color, White) >= 4.5,
            $"{key} ({color}) beyaz zeminde {Contrast(color, White):F2}:1 -- AA esigi 4.5:1.");
    }

    [Fact]
    public void AccentIsDerivedFromTheLogoOrangeNotTheOldTeal()
    {
        // Palet logoyla uyumlu olmali; eski teal marka rengi geride kalmamali.
        var accent = ReadBrush("AccentBrush");
        var (red, green, blue) = Parse(accent);

        Assert.True(red > green && green >= blue, $"Vurgu rengi sicak olmali, bulunan: {accent}");
    }

    [Fact]
    public void NoViewStillUsesTheRetiredTealBrandColor()
    {
        var stale = new[] { "#156B63", "#12675F", "#49A99C", "#25433F" };
        var offenders = Directory
            .EnumerateFiles(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop"), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => stale.Any(color =>
                File.ReadAllText(file).Contains(color, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Eski marka rengi kalmis: " + string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    private static string ReadBrush(string key)
    {
        var path = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Themes", "DesignSystem.xaml");
        var document = XDocument.Load(path);
        var brush = document.Descendants()
            .Single(element => element.Name.LocalName == "SolidColorBrush"
                && element.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == key));
        return brush.Attribute("Color")!.Value;
    }

    private static (int R, int G, int B) Parse(string hex)
    {
        var value = hex.TrimStart('#');
        return (Convert.ToInt32(value[..2], 16),
                Convert.ToInt32(value.Substring(2, 2), 16),
                Convert.ToInt32(value.Substring(4, 2), 16));
    }

    private static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
