using System.Linq;
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

    /// <summary>
    /// Renk tasiyabilecek ozellik adlari: hem duz XML ozniteligi olarak
    /// (Background="#..."), hem de Setter/Style icinde (Property="Fill"
    /// Value="#...") gorulebilirler.
    /// </summary>
    private const string ColourProperties =
        "Background|Foreground|BorderBrush|Fill|Stroke|SelectionBrush|" +
        "HorizontalGridLinesBrush|VerticalGridLinesBrush|AlternatingRowBackground";

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void ViewUsesNoRawHexColour(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(ViewRoot, relativePath));

        // Duz XML ozniteligi: Background="#RGB".."#AARRGGBB" (3-8 hex hane).
        var attributeMatches = Regex.Matches(text,
            $@"({ColourProperties})=""#[0-9A-Fa-f]{{3,8}}""");

        // Setter/Style icindeki deger: <Setter Property="Fill" Value="#..."/>.
        // Property ve Value herhangi bir sirada, aralarinda baska Setter
        // ozniteligi (orn. TargetName) olabilir; bu yuzden iki yonu de dener.
        var setterMatches = Regex.Matches(text,
            $@"<Setter[^>]*Property=""({ColourProperties})""[^>]*Value=""#[0-9A-Fa-f]{{3,8}}""|" +
            $@"<Setter[^>]*Value=""#[0-9A-Fa-f]{{3,8}}""[^>]*Property=""({ColourProperties})""");

        var allMatches = attributeMatches.Cast<Match>().Concat(setterMatches.Cast<Match>()).ToList();

        Assert.True(allMatches.Count == 0,
            $"{relativePath}: ham renk kullanimi -- {string.Join(", ", allMatches.Select(m => m.Value))}");

        // Bilinen ve kasitli kapsam disi: adlandirilmis renkler (White, Black, Transparent...).
        // Bu turdeki 37 "Background=\"White\"" kullanimi ayri bir konudur;
        // bu regex'e eklemek bu turun diffini gereksiz buyutur. Sonraki
        // okuyucu icin not: bu bilinen bir bosluk, gozden kacan bir sey degil.
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
