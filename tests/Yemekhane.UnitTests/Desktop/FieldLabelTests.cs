using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Her giris alaninin ne oldugunun anlasilabilir olmasini dogrular.
///
/// Etiketsiz bir kutu dizisi kullaniciya hicbir sey soylemez: "yedi bos kutu" gorur ve
/// hangisine ne yazacagini bilemez. ToolTip yetmez -- kullanici uzerine gelmeden goremez
/// ve dokunmatik/klavye kullanicisi hic goremez.
///
/// Alanin ya gorunur bir etiketi ya da en azindan bir erisilebilirlik adi olmalidir.
/// </summary>
[Collection("UI")]
public sealed class FieldLabelTests
{
    public static TheoryData<string> Views() =>
    [
        "students", "daily", "entitlements", "devices", "sms", "cash", "reports", "settings", "definitions"
    ];

    [Theory]
    [MemberData(nameof(Views))]
    public void EveryInputIsIdentifiableByLabelOrAccessibleName(string name) =>
        UiThread.Run(() =>
        {
            var root = Build(name, 1600, 900);
            var unlabeled = Descendants(root)
                .OfType<Control>()
                .Where(control => control is TextBox or ComboBox or PasswordBox)
                .Where(control => control.IsVisible && control.ActualWidth > 0)
                .Where(control => !HasAccessibleName(control) && !HasVisibleLabel(control))
                .Select(Describe)
                .ToArray();

            Assert.True(unlabeled.Length == 0,
                $"{name}: etiketsiz {unlabeled.Length} alan -> {string.Join(", ", unlabeled.Take(6))}");
        });

    // ToolTip KABUL EDILMEZ: kullanici uzerine gelmeden goremez, dokunmatik ve klavye
    // kullanicisi hic goremez. Ekrana bakan biri alanin ne oldugunu ANINDA anlamali.
    private static bool HasAccessibleName(Control control) =>
        !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control));

    /// <summary>Alanin hemen ustunde ya da solunda duran bir etiket var mi?</summary>
    private static bool HasVisibleLabel(Control control)
    {
        if (VisualTreeHelper.GetParent(control) is not DependencyObject parent) return false;
        var box = Bounds(control);
        return Descendants(parent).OfType<TextBlock>()
            .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) && text.Text.Length <= 40)
            .Any(text =>
            {
                var label = Bounds(text);
                var above = label.Bottom <= box.Top + 2 && box.Top - label.Bottom < 26
                            && Math.Abs(label.Left - box.Left) < 40;
                var beside = label.Right <= box.Left + 2 && box.Left - label.Right < 20
                             && Math.Abs(label.Top - box.Top) < 24;
                return above || beside;
            });
    }

    private static Rect Bounds(FrameworkElement element)
    {
        var origin = element.TranslatePoint(new Point(0, 0), null);
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static string Describe(Control control) =>
        $"{control.GetType().Name}@{control.ActualWidth:F0}px";

    private static FrameworkElement Build(string name, double width, double height)
    {
        FrameworkElement view = name switch
        {
            "students" => new StudentsView(),
            "daily" => new DailyTrackingView(),
            "entitlements" => new MealEntitlementsView(),
            "devices" => new DevicesView(),
            "sms" => new SmsView(),
            "cash" => new CashView(),
            "reports" => new ReportsView(),
            "settings" => new SettingsView(),
            "definitions" => new DefinitionsView(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Bilinmeyen görünüm.")
        };
        var host = UiThread.Host(view, width, height);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        return view;
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
}
