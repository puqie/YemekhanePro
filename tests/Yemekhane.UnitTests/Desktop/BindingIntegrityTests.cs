using System.Reflection;
using System.Text.RegularExpressions;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// HER baglamanin gercek bir ozellige gittiginin dogrulanmasi.
///
/// XAML baglamalari derleme sirasinda DOGRULANMAZ. Yanlis yazilmis bir yol
/// ("FirstNmae") sessizce bos deger dondurur: kullanici alani doldurur,
/// kaydeder ve verisinin kayboldugunu gorur. Hicbir hata mesaji cikmaz.
///
/// Ekranlarda 84 TextBox, 38 ComboBox, 16 CheckBox ve 17 DatePicker var;
/// bunlari elle denemek yerine tum baglamalari statik olarak cozeriz.
/// </summary>
public sealed class BindingIntegrityTests
{
    /// <summary>Her gorunum dosyasi, App.xaml.cs'te ona atanan ViewModel ile eslesir.</summary>
    public static TheoryData<string, string> ViewModelForView() => new()
    {
        { "StudentsView.xaml", nameof(StudentsViewModel) },
        { "CashView.xaml", nameof(CashViewModel) },
        { "MealEntitlementsView.xaml", nameof(MealEntitlementsViewModel) },
        { "CalendarView.xaml", nameof(CalendarViewModel) },
        { "DevicesView.xaml", nameof(DevicesViewModel) },
        { "DeviceCardsView.xaml", nameof(DeviceCardsViewModel) },
        { "SmsView.xaml", nameof(SmsViewModel) },
        { "ReportsView.xaml", nameof(ReportsViewModel) },
        { "SettingsView.xaml", nameof(SettingsViewModel) },
        { "DailyTrackingView.xaml", nameof(DailyTrackingViewModel) },
        { "StudentImportView.xaml", nameof(StudentImportViewModel) },
        { "BulkOperationWizardView.xaml", nameof(BulkOperationWizardViewModel) },
    };

    /// <summary>
    /// Kullanicinin VERI GIRDIGI denetimler. Bunlarda yanlis bir yol, girilen
    /// verinin sessizce kaybolmasi demektir -- en sinsi hata turu.
    /// </summary>
    private static readonly string[] InputControls =
        ["TextBox", "PasswordBox", "ComboBox", "CheckBox", "DatePicker", "RadioButton", "Slider"];

    [Theory]
    [MemberData(nameof(ViewModelForView))]
    public void EveryInputControlBindsToARealProperty(string view, string viewModel)
    {
        var type = FindViewModelType(viewModel);
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));
        var broken = new List<string>();

        foreach (var (control, attribute, path) in InputBindings(xaml))
        {
            if (!Resolves(type, path))
                broken.Add($"<{control} {attribute}=\"{{Binding {path}}}\">");
        }

        Assert.True(broken.Count == 0,
            $"{view}: {viewModel} üzerinde bulunmayan {broken.Count} bağlama — " +
            $"kullanıcının girdiği veri sessizce kaybolur:{Environment.NewLine}" +
            string.Join(Environment.NewLine, broken));
    }

    [Theory]
    [MemberData(nameof(ViewModelForView))]
    public void EveryButtonCommandExists(string view, string viewModel)
    {
        // Var olmayan bir komuta bagli buton SONSUZA KADAR PASIF kalir.
        // Kullanici tiklar, hicbir sey olmaz, hata da gormez.
        var type = FindViewModelType(viewModel);
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));
        var broken = new List<string>();

        foreach (Match match in Regex.Matches(StripItemScopes(xaml), @"Command=""\{Binding\s+([^}""]+)\}"""))
        {
            var path = CleanPath(match.Groups[1].Value);
            if (path is null) continue;
            if (!Resolves(type, path)) broken.Add(path);
        }

        Assert.True(broken.Count == 0,
            $"{view}: {viewModel} üzerinde bulunmayan {broken.Count} komut — " +
            $"buton kalıcı olarak pasif kalır:{Environment.NewLine}" +
            string.Join(Environment.NewLine, broken.Distinct()));
    }

    [Theory]
    [MemberData(nameof(ViewModelForView))]
    public void EveryVisibilityBindingResolvesSoPanelsDoNotVanish(string view, string viewModel)
    {
        // Cozulemeyen bir Visibility baglamasi paneli GORUNMEZ birakabilir:
        // kullanici ekranin yarisinin neden bos oldugunu anlamaz.
        var type = FindViewModelType(viewModel);
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));
        var broken = new List<string>();

        foreach (Match match in Regex.Matches(StripItemScopes(xaml), @"Visibility=""\{Binding\s+([^}""]+)\}"""))
        {
            var path = CleanPath(match.Groups[1].Value);
            if (path is null) continue;
            if (!Resolves(type, path)) broken.Add(path);
        }

        Assert.True(broken.Count == 0,
            $"{view}: çözülemeyen {broken.Count} Visibility bağlaması — " +
            $"panel görünmez kalır:{Environment.NewLine}" +
            string.Join(Environment.NewLine, broken.Distinct()));
    }

    [Theory]
    [MemberData(nameof(ViewModelForView))]
    public void TwoWayBoundInputsHaveASetterSoTypingIsNotLost(string view, string viewModel)
    {
        // Bir TextBox salt okunur ozellige baglanirsa WPF calisma zamaninda
        // hata firlatir ve ekran acilmaz.
        var type = FindViewModelType(viewModel);
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));
        var readOnly = new List<string>();

        foreach (var (control, attribute, path) in InputBindings(xaml))
        {
            if (control is not ("TextBox" or "PasswordBox")) continue;
            if (attribute is not "Text") continue;

            var property = ResolveProperty(type, path);
            if (property is not null && !property.CanWrite)
                readOnly.Add($"{path} (salt okunur)");
        }

        Assert.True(readOnly.Count == 0,
            $"{view}: TextBox salt okunur özelliğe bağlı — ekran açılırken hata verir:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, readOnly)}");
    }

    // ------------------------------------------------- yardimcilar

    /// <summary>
    /// DataTemplate ve ItemContainerStyle iclerini cikarir.
    ///
    /// Bu bloklarin DataContext'i dis ViewModel DEGIL, koleksiyonun oge
    /// tipidir (ornegin Columns -> ReportColumnViewModel). Onlari dis
    /// ViewModel'e gore cozmeye calismak YANLIS POZITIF uretir.
    /// </summary>
    private static string StripItemScopes(string xaml)
    {
        string[] tags =
        [
            "DataTemplate", "ItemContainerStyle", "CellTemplate", "ItemTemplate",
            "CellEditingTemplate", "HeaderTemplate", "ContentTemplate",
            // Setter.Value bir sablon tasiyabilir; icerigi de oge kapsamindadir.
            "Setter.Value",
        ];
        foreach (var tag in tags)
        {
            var escaped = Regex.Escape(tag);
            xaml = Regex.Replace(xaml, "<" + escaped + @"[\s>].*?</" + escaped + ">", string.Empty,
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }
        return xaml;
    }

    /// <summary>Giris denetimlerindeki deger baglamalarini cikarir.</summary>
    private static IEnumerable<(string Control, string Attribute, string Path)> InputBindings(string xaml)
    {
        xaml = StripItemScopes(xaml);
        foreach (var control in InputControls)
        {
            var pattern = $@"<{control}\b[^>]*?>";
            foreach (Match element in Regex.Matches(xaml, pattern, RegexOptions.Singleline))
            {
                foreach (Match binding in Regex.Matches(element.Value,
                    @"(Text|SelectedItem|SelectedValue|IsChecked|SelectedDate|Value|ItemsSource|Password)=""\{Binding\s+([^}""]+)\}"""))
                {
                    var path = CleanPath(binding.Groups[2].Value);
                    if (path is not null)
                        yield return (control, binding.Groups[1].Value, path);
                }
            }
        }
    }

    /// <summary>
    /// Baglama ifadesinden cozulebilir yolu cikarir; cozulemeyecek bicimleri
    /// (RelativeSource, ElementName, dizin, bos yol) atlar.
    /// </summary>
    private static string? CleanPath(string expression)
    {
        if (expression.Contains("RelativeSource", StringComparison.Ordinal)
            || expression.Contains("ElementName", StringComparison.Ordinal)
            || expression.Contains("Source=", StringComparison.Ordinal))
            return null;

        var path = expression.Split(',')[0].Replace("Path=", string.Empty).Trim();
        if (path.Length == 0 || path is "." || path.StartsWith('[')) return null;
        return path;
    }

    /// <summary>Nokta ile ayrilmis yolu adim adim cozer (Preview.StudentCount gibi).</summary>
    private static bool Resolves(Type root, string path) => ResolveProperty(root, path) is not null;

    private static PropertyInfo? ResolveProperty(Type root, string path)
    {
        var current = root;
        PropertyInfo? property = null;

        foreach (var segment in path.Split('.'))
        {
            var name = segment.Split('[')[0].Trim();
            if (name.Length == 0) return null;

            property = current.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (property is null) return null;

            current = Unwrap(property.PropertyType);
        }
        return property;
    }

    /// <summary>Koleksiyon ve Nullable sarmalayicilarini acar.</summary>
    private static Type Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return underlying;

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition.Name.StartsWith("ObservableCollection", StringComparison.Ordinal)
                || definition.Name.StartsWith("IReadOnlyList", StringComparison.Ordinal)
                || definition.Name.StartsWith("IEnumerable", StringComparison.Ordinal)
                || definition.Name.StartsWith("List", StringComparison.Ordinal))
                return type.GetGenericArguments()[0];
        }
        return type;
    }

    private static Type FindViewModelType(string name)
    {
        var type = typeof(StudentsViewModel).Assembly.GetTypes()
            .SingleOrDefault(candidate => candidate.Name == name);
        Assert.NotNull(type);
        return type!;
    }

    private static string ViewsDirectory() =>
        Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Views");

    private static string FindRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Yemekhane.sln")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }
}
