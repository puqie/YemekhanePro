using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using Yemekhane.Desktop;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Donusturuculerin dogru hedef turle kullanildigini dogrular.
///
/// InverseBooleanConverter bool dondurur; Visibility bekleyen bir ozelliye baglandiginda
/// WPF donusumu sessizce basarisiz sayar ve eleman GORUNUR kalir. Boylece gizlenmesi
/// gereken bir katman ekranda durur -- ust uste binmis butonlar bu yuzden olusur.
/// </summary>
public sealed class ConverterUsageTests
{
    [Fact]
    public void InverseBooleanConverterReturnsBooleanNotVisibility()
    {
        var converter = new InverseBooleanConverter();

        Assert.IsType<bool>(converter.Convert(true, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InverseVisibilityConverterCollapsesWhenTrue()
    {
        // Visibility bekleyen yerlerde kullanilacak ayri donusturucu.
        var converter = InverseBooleanToVisibilityConverter.Instance;

        Assert.Equal(Visibility.Collapsed,
            converter.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible,
            converter.Convert(false, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NoViewBindsInverseBoolToAVisibilityProperty()
    {
        var offenders = new List<string>();
        var root = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop");

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var xaml = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(xaml, @"Visibility=""\{Binding[^}]*Converter=\{StaticResource\s+([^}\s]+)\}"))
            {
                var converter = match.Groups[1].Value;
                if (converter is "InverseBool")
                    offenders.Add($"{Path.GetFileName(file)}: {converter}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Visibility'ye bool dönen converter bağlanmış: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// SAAT gosteren her baglama LocalTime donusturucusunden gecmelidir.
    ///
    /// Kayitlar UTC saklanir. WPF'in StringFormat'i degerin KENDI ofsetini oldugu gibi
    /// bicimler -- yerel saate CEVIRMEZ. Donusturucu unutulunca ekran Turkiye'de
    /// 3 SAAT GERI gosterir (UTC+3). Sahada "saat yanlis" sikayeti tam olarak buydu.
    ///
    /// Yalnizca saat iceren bicimler denetlenir: DateOnly alanlari (Tarih, TargetDate)
    /// saat tasimaz ve donusturucu ISTEMEZ. C# biçiminde "mm" DAKIKA, "MM" AY oldugu
    /// icin karsilastirma buyuk/kucuk harfe DUYARLIDIR.
    /// </summary>
    [Fact]
    public void EveryClockBindingGoesThroughTheLocalTimeConverter()
    {
        var offenders = new List<string>();
        var root = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop");

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var xaml = File.ReadAllText(file);
            // Baglamayi TIRNAKTAN TIRNAGA al. "[^{}]*" ile yazilirsa StringFormat={}{0:...}
            // icindeki susler kalibi kirar ve hicbir sey eslesmez -- test sessizce hep
            // gecer (mutasyonla dogrulandi).
            foreach (Match match in Regex.Matches(xaml, @"""\{Binding[^""]*"""))
            {
                var binding = match.Value;
                var format = Regex.Match(binding, @"StringFormat=\{\}\{0:([^}]*)\}");
                if (!format.Success) continue;

                // Saat/dakika/saniye iceriyor mu? ("MM" ay oldugu icin Ordinal karsilastirma)
                var pattern = format.Groups[1].Value;
                var showsClock = pattern.Contains("HH", StringComparison.Ordinal)
                    || pattern.Contains("mm", StringComparison.Ordinal)
                    || pattern.Contains("ss", StringComparison.Ordinal);
                if (!showsClock) continue;

                if (!binding.Contains("LocalTime", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {binding}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Saat gösteren bağlama LocalTime converter'ından geçmiyor (3 saat geri gösterir): "
            + string.Join(" | ", offenders));
    }

    /// <summary>
    /// LocalTime kullanan her View onu KENDI kaynaklarinda tanimlamalidir.
    ///
    /// Bu proje donusturuculeri merkezi bir sozlukte tutmuyor: her View kendi
    /// ornegini yaratiyor. Tanimi unutulan bir StaticResource XAML'i YUKLENEMEZ
    /// hale getirir -- ekran tamamen acilmaz. Bir kez yasandi: BulkOperationWizardView'a
    /// LocalTime baglanip tanim eklenmeyince 46 test birden dustu.
    /// </summary>
    [Fact]
    public void EveryViewUsingLocalTimeAlsoDefinesIt()
    {
        var offenders = new List<string>();
        var root = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop");

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var xaml = File.ReadAllText(file);
            if (!xaml.Contains("StaticResource LocalTime", StringComparison.Ordinal)) continue;
            if (!xaml.Contains("LocalTimeConverter", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "LocalTime kullanılıyor ama tanımlanmamış (XAML hiç yüklenmez): " + string.Join(", ", offenders));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
