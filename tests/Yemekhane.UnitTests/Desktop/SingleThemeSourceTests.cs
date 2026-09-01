using System.Text.RegularExpressions;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Temanin TEK kaynak oldugunu dogrular.
///
/// Bu test gercek bir tutarsizliktan dogdu: DesignSystem.xaml iyi yazilmisti
/// ama 13 dosya kendi renk ve stillerini yeniden tanimliyordu. StudentsView
/// temayi merge edip UZERINE yaziyordu, yani merge etkisizdi. Sonuc: tek
/// tasarim sistemi, 13 ayri gerceklik.
///
/// Kapsam notu: MainWindow.xaml kasitli olarak disaridadir. Navigasyon menusu
/// Gorev 4'te bastan yazilacak ve renk temizligi o gecişte yapilacak; bu
/// dosyayi burada temizlemek Gorev 4 ile cakisir. Bu yuzden yalnizca
/// Views/ klasoru taranir.
/// </summary>
public sealed class SingleThemeSourceTests
{
    private static readonly string ViewRoot = Path.Combine(
        RepositoryRoot(), "src", "Yemekhane.Desktop", "Views");

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(ViewRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}")) continue;
            data.Add(Path.GetRelativePath(ViewRoot, path));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void ViewDefinesNoLocalBrush(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(ViewRoot, relativePath));

        Assert.DoesNotContain("<SolidColorBrush", text);
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void ViewUsesNoRawHexColour(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(ViewRoot, relativePath));
        var matches = Regex.Matches(text, @"(Background|Foreground|BorderBrush)=""#[0-9A-Fa-f]{6,8}""");

        Assert.True(matches.Count == 0,
            $"{relativePath}: ham renk kullanimi -- {string.Join(", ", matches.Select(m => m.Value))}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
