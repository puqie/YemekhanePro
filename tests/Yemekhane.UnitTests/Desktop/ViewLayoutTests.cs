using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Sayfa duzeninin gercekten olcumlenebildigini dogrular.
///
/// XAML derlenmesi bir sayfanin DOGRU gorundugunu kanitlamaz: eksik bir kaynak anahtari,
/// tasan bir panel ya da sifir genislikte bir alan yalnizca calisma zamaninda ortaya cikar.
/// Bu testler her sayfayi gercek bir pencere boyutunda olcumleyip yerlestirir.
/// </summary>
[Collection("UI")]
public sealed class ViewLayoutTests
{
    public static TheoryData<string> Views() =>
    [
        "students", "daily", "entitlements", "calendar", "devices",
        "devicecards", "sms", "cash", "reports", "settings", "bulk"
    ];

    /// <summary>
    /// Views() eksi "devicecards": o ekranda hic TextBox/ComboBox/DatePicker/
    /// PasswordBox YOK (yalnizca DataGrid ve Button var). IsVisible filtresi
    /// kaldirildiktan sonra (bkz. inceleme geri bildirimi) bu ekran "hiçbir
    /// giriş alanı ölçülemedi" ile KIRMIZI cikiyordu -- bu bir GENISLIK
    /// regresyonu degil, teorinin bu ekran icin zaten anlamsiz olmasi.
    /// DeviceCardsView.xaml'e DOKUNULMAZ (gorev kapsami disi); bunun yerine
    /// yalnizca bu teori o ekrani atlar.
    /// </summary>
    public static TheoryData<string> ViewsWithInputFields() =>
    [
        "students", "daily", "entitlements", "calendar", "devices",
        "sms", "cash", "reports", "settings", "bulk"
    ];

    private static FrameworkElement Create(string name) => name switch
    {
        "students" => new StudentsView(),
        "daily" => new DailyTrackingView(),
        "entitlements" => new MealEntitlementsView(),
        "calendar" => new CalendarView(),
        "devices" => new DevicesView(),
        "devicecards" => new DeviceCardsView(),
        "sms" => new SmsView(),
        "cash" => new CashView(),
        "reports" => new ReportsView(),
        "settings" => new SettingsView(),
        "bulk" => new BulkOperationWizardView(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Bilinmeyen görünüm.")
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void ViewMeasuresAtFullHdWithoutOverflow(string name) =>
        OnUiThread(() =>
        {
            var (element, width, height) = Arrange(name, 1600, 900);

            // Icerik pencereye sigmali; tasan bir panel butonlari ekran disinda birakir.
            Assert.True(element.ActualWidth <= width + 0.5,
                $"{name}: {element.ActualWidth:F0}px genişlik, pencere {width}px.");
            Assert.True(element.ActualHeight <= height + 0.5,
                $"{name}: {element.ActualHeight:F0}px yükseklik, pencere {height}px.");
        });

    [Theory]
    [MemberData(nameof(Views))]
    public void ViewStillMeasuresOnASmallLaptopScreen(string name) =>
        OnUiThread(() =>
        {
            // Okul idaresinde 1366x768 dizustu yaygindir; bu boyutta da yerlesmelidir.
            var (element, width, height) = Arrange(name, 1366, 720);

            Assert.True(element.ActualWidth <= width + 0.5, $"{name}: küçük ekranda taşıyor.");
            Assert.True(element.ActualHeight <= height + 0.5, $"{name}: küçük ekranda taşıyor.");
        });

    [Theory]
    [MemberData(nameof(ViewsWithInputFields))]
    public void NoInputFieldCollapsesToAnUnusableWidth(string name) =>
        OnUiThread(() =>
        {
            var (element, _, _) = Arrange(name, 1600, 900);
            // IsVisible KONTROL EDILMEZ: bu barindirma (UiThread.Host -> Border,
            // PresentationSource'suz) altinda IsVisible HER ZAMAN false donuyor --
            // ekrandaki HICBIR kontrol icin true olmuyor (bkz. inceleme geri
            // bildirimi: StudentsView'da 19 girdiden 15'i ActualWidth > 0 ama
            // 0'i IsVisible). Bu filtre varken test 11 ekranin TAMAMINDA bos
            // kume uzerinde calisip sessizce yesil donuyordu. ActualWidth > 0,
            // FieldWidthTests.cs'in de kullandigi dogru vekildir: yalnizca
            // GERCEKTEN yerlestirilmis (gorunmeyen sekmede degil) kontroller
            // sifirdan buyuk genislik alir.
            var measured = Descendants(element)
                .OfType<Control>()
                .Where(control => control is TextBox or ComboBox or DatePicker or PasswordBox)
                .Where(control => control.ActualWidth > 0)
                .ToArray();

            // Hicbir kontrol olculmediyse test KANITLAMAZ; FieldWidthTests'teki
            // ayni kural burada da gecerli.
            Assert.True(measured.Length > 0, $"{name}: hiçbir giriş alanı ölçülemedi; test anlamsız.");

            var narrow = measured
                .Where(control => control.ActualWidth < 80)
                .Select(control => $"{control.GetType().Name}({control.ActualWidth:F0}px)")
                .ToArray();

            Assert.True(narrow.Length == 0,
                $"{name}: kullanılamayacak kadar dar alan(lar): {string.Join(", ", narrow)}");
        });

    [Theory]
    [MemberData(nameof(Views))]
    public void FilterControlsStayInsideTheirPanel(string name) =>
        OnUiThread(() =>
        {
            // Sarmalayan panelden tasan bir filtre alani ekranda GORUNMEZ:
            // kullanici "Filtrele" dugmesini bulamaz.
            // IsVisible KONTROL EDILMEZ: bkz. NoInputFieldCollapsesToAnUnusableWidth
            // ustundeki not -- bu barindirma altinda her zaman false donuyor.
            var (element, width, _) = Arrange(name, 1600, 900);
            var measured = Descendants(element).OfType<FrameworkElement>()
                .Where(child => child is TextBox or ComboBox or Button)
                .Where(child => child.ActualWidth > 0)
                .ToArray();

            Assert.True(measured.Length > 0, $"{name}: hiçbir kontrol ölçülemedi; test anlamsız.");

            var escaped = measured
                .Select(child => new
                {
                    child,
                    Right = child.TranslatePoint(new Point(child.ActualWidth, 0), element).X
                })
                .Where(item => item.Right > width + 1)
                .Select(item => $"{item.child.GetType().Name}(sağ kenar {item.Right:F0}px)")
                .ToArray();

            Assert.True(escaped.Length == 0,
                $"{name}: panel dışına taşan kontrol(ler): {string.Join(", ", escaped.Take(5))}");
        });

    [Theory]
    [MemberData(nameof(Views))]
    public void EveryButtonIsBigEnoughToClick(string name) =>
        OnUiThread(() =>
        {
            // IsVisible KONTROL EDILMEZ: bkz. NoInputFieldCollapsesToAnUnusableWidth
            // ustundeki not.
            var (element, _, _) = Arrange(name, 1600, 900);
            var measured = Descendants(element).OfType<Button>()
                .Where(button => button.ActualHeight > 0)
                .ToArray();

            Assert.True(measured.Length > 0, $"{name}: hiçbir düğme ölçülemedi; test anlamsız.");

            var tiny = measured
                .Where(button => button.ActualHeight < 26)
                .Select(button => $"{button.Content}({button.ActualHeight:F0}px)")
                .ToArray();

            Assert.True(tiny.Length == 0, $"{name}: çok küçük düğme(ler): {string.Join(", ", tiny)}");
        });

    private static (FrameworkElement Element, double Width, double Height) Arrange(
        string name, double width, double height)
    {
        var host = UiThread.Host(Create(name), width, height);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        return ((FrameworkElement)host.Child, width, height);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void OnUiThread(Action action) => UiThread.Run(action);
}
