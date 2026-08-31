using System.Reflection;
using System.Text.RegularExpressions;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Salt okunur ozelliklere iki yonlu baglama yapilmamasi.
///
/// WPF'te Run.Text baglamasi VARSAYILAN OLARAK TwoWay'dir (TextBlock.Text'ten farkli olarak).
/// Hedef ozelligin setter'i yoksa baglama calisma zamaninda InvalidOperationException firlatir:
/// "salt okunur ozelliginde TwoWay veya OneWayToSource baglama calisamaz".
///
/// Bu hata yalnizca ilgili ekran YUKLENDIGINDE ortaya cikar; derleme sirasinda gorunmez.
/// Kullanicinin gordugu: giris yapiyor ve bir hata kutusu ile karsilasiyor.
/// </summary>
public sealed class ReadOnlyBindingTests
{
    [Fact]
    public void RunTextBindingsTargetWritableOrExplicitlyOneWayProperties()
    {
        var problems = new List<string>();
        var viewsDirectory = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop");

        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var xaml = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(xaml, @"<Run\s+Text=""\{Binding\s+([^}""]*)\}"""))
            {
                var expression = match.Groups[1].Value;
                // Mode acikca verilmisse sorun yok.
                if (expression.Contains("Mode=", StringComparison.OrdinalIgnoreCase)) continue;
                var path = expression.Split(',')[0].Replace("Path=", string.Empty).Trim();
                if (path.Length == 0 || path.Contains('.')) continue;

                if (IsReadOnlyViewModelProperty(path))
                    problems.Add($"{Path.GetFileName(file)}: {path}");
            }
        }

        Assert.True(problems.Count == 0,
            "Salt okunur özelliğe varsayılan (TwoWay) Run.Text bağlaması: " + string.Join(", ", problems));
    }

    private static bool IsReadOnlyViewModelProperty(string name) =>
        typeof(Yemekhane.Desktop.ViewModels.SmsViewModel).Assembly.GetTypes()
            .Where(type => type.Namespace == "Yemekhane.Desktop.ViewModels")
            .Select(type => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .Any(property => property is not null && !property.CanWrite);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
