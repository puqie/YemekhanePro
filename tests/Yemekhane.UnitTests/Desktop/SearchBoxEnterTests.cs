using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// SAHA: "Günlük takipte arama butonu yok, Yenile'ye basınca arıyor, Enter'la bile arayamıyorum;
/// çoğu yerde bu tarz hatalar var." Arama kutulari Enter'i IsDefault="True" dugmesine
/// birakiyordu; ayni pencerede (tum ekran hostlari MainWindow'da yuklu) birden cok varsayilan
/// dugme olunca WPF Enter'i hicbirine vermez. Kural: her arama kutusunun kendisinde ya da
/// bir ust panelinde <c>KeyBinding Key="Enter"</c> bulunur; sayfa ici suzgec dugmeleri IsDefault
/// kullanmaz.
/// </summary>
public sealed class SearchBoxEnterTests
{
    private static readonly Regex SearchProperty = new(
        @"\{Binding\s+(Search|SearchText|CardSearch|StudentSearch|StudentPickerSearch|FilterStudentNumber|HolidayStudentSearch)\b",
        RegexOptions.Compiled);

    private static string ViewsDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "src", "Yemekhane.Desktop", "Views")))
            directory = Path.GetDirectoryName(directory);
        return Path.Combine(directory ?? throw new DirectoryNotFoundException("Depo koku bulunamadi."), "src", "Yemekhane.Desktop", "Views");
    }

    public static TheoryData<string> ViewsWithSearchBoxes()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*View.xaml").OrderBy(x => x, StringComparer.Ordinal))
            if (SearchProperty.IsMatch(File.ReadAllText(file))) data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(ViewsWithSearchBoxes))]
    public void EverySearchBoxSearchesOnEnter(string view)
    {
        var document = XDocument.Load(Path.Combine(ViewsDirectory(), view));
        var boxes = document.Descendants().Where(x => x.Name.LocalName == "TextBox"
            && x.Attribute("Text") is { } text && SearchProperty.IsMatch(text.Value)).ToList();
        Assert.NotEmpty(boxes);

        var missing = boxes.Where(box => !HasEnterBinding(box)).Select(Describe).ToList();
        Assert.True(missing.Count == 0, $"{view}: Enter ile aramayan arama kutusu:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    /// <summary>
    /// Sayfa ici Filtrele/Ara/Uygula dugmeleri IsDefault tasimaz: Enter acik KeyBinding ile baglanir.
    /// (Giris/aktivasyon pencereleri ve cekmece/modal formlari kapsam disi.)
    /// </summary>
    [Theory]
    [InlineData("DailyTrackingView.xaml")]
    [InlineData("MealEntitlementsView.xaml")]
    [InlineData("CashView.xaml")]
    [InlineData("ReportsView.xaml")]
    [InlineData("CardListView.xaml")]
    [InlineData("KindergartenView.xaml")]
    public void ListFilterButtonsDoNotRelyOnIsDefault(string view)
    {
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));
        Assert.DoesNotContain("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
    }

    private static bool HasEnterBinding(XElement box)
    {
        for (XElement? element = box; element is not null; element = element.Parent)
        {
            var bindings = element.Elements().FirstOrDefault(x => x.Name.LocalName.EndsWith(".InputBindings", StringComparison.Ordinal));
            if (bindings is not null && bindings.Elements().Any(x => x.Name.LocalName == "KeyBinding"
                    && string.Equals(x.Attribute("Key")?.Value, "Enter", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string Describe(XElement box) =>
        $"<TextBox Text=\"{box.Attribute("Text")?.Value}\" AutomationProperties.Name=\"{box.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name")?.Value}\">";
}
