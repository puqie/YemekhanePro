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

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
