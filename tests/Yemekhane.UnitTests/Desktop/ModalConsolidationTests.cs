namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ortalanmis diyaloglar TEK kontrolden (controls:Modal) gelir. Once yedi ekran karartma +
/// beyaz Border + baslik + Kapat'i elle yaziyordu: yedi genislik, uc kose yaricapi, kimi
/// karartmasiz, kimi yalnizca sayfayi karartan. Bu test elle yazilmis karartmanin geri
/// donmesini engeller.
/// </summary>
public sealed class ModalConsolidationTests
{
    [Fact]
    public void ViewsContainNoHandRolledScrims()
    {
        var views = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Views");
        var offenders = Directory.GetFiles(views, "*.xaml")
            .Where(file => File.ReadAllText(file).Contains("ScrimBrush}", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Elle yazilmis karartma (ScrimBrush/ScrimStrongBrush) kaldi; controls:Modal ya da controls:Drawer kullanin: "
            + string.Join(", ", offenders));
    }

    /// <summary>Ana pencerede yalnizca komut paleti (arama) kendi karartmasini tasir; diger diyaloglar Modal'dir.</summary>
    [Fact]
    public void MainWindowKeepsOnlyTheCommandPaletteScrim()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "MainWindow.xaml"));

        Assert.Equal(1, CountOf(xaml, "ScrimStrongBrush}"));
        Assert.Equal(0, CountOf(xaml, "{StaticResource ScrimBrush}"));
        Assert.Contains("x:Name=\"ShortcutHelpHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<controls:Modal x:Name=\"SessionExpiredHost\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>Modal kullanan her gorunum Modal.xaml'i kendi kaynaklarina merge eder; aksi halde App'siz kurulumda ekran acilmaz.</summary>
    [Fact]
    public void EveryModalUserMergesTheModalTheme()
    {
        var root = FindRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", "Yemekhane.Desktop", "Views"), "*.xaml")
            .Append(Path.Combine(root, "src", "Yemekhane.Desktop", "MainWindow.xaml"));
        var missing = files
            .Where(file => File.ReadAllText(file).Contains("<controls:Modal", StringComparison.Ordinal))
            .Where(file => !File.ReadAllText(file).Contains("Themes/Modal.xaml", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(missing.Length == 0, "Modal.xaml merge edilmemis: " + string.Join(", ", missing));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !(Directory.Exists(Path.Combine(directory.FullName, "src")) && Directory.Exists(Path.Combine(directory.FullName, "tests"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
