using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Yemekhane.UnitTests.Desktop;

[Collection("UI")]
public sealed class ControlSizingTests
{
    [Fact]
    public void TekSatirliButonVeGirislerAyniYuksekliktedir() => UiThread.Run(() =>
    {
        var textBox = new TextBox { Text = "Tutar" };
        var comboBox = new ComboBox { ItemsSource = new[] { "Öğle" }, SelectedIndex = 0 };
        var button = new Button { Content = "Kasaya işle" };
        var primary = new Button { Content = "Uygula" };
        var root = new StackPanel
        {
            Width = 320,
            UseLayoutRounding = true
        };
        root.SetValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
        root.SetValue(TextElement.FontSizeProperty, 12d);
        root.Children.Add(textBox);
        root.Children.Add(comboBox);
        root.Children.Add(button);
        root.Children.Add(primary);
        UiThread.ApplyResources(root);
        primary.Style = (Style)root.FindResource("Primary");

        root.Measure(new Size(320, 300));
        root.Arrange(new Rect(0, 0, 320, root.DesiredSize.Height));
        root.UpdateLayout();

        Assert.True(
            Math.Abs(textBox.ActualHeight - comboBox.ActualHeight) < 0.1
            && Math.Abs(textBox.ActualHeight - button.ActualHeight) < 0.1
            && Math.Abs(textBox.ActualHeight - primary.ActualHeight) < 0.1,
            $"TextBox={textBox.ActualHeight:F1}, ComboBox={comboBox.ActualHeight:F1}, "
            + $"Button={button.ActualHeight:F1}, Primary={primary.ActualHeight:F1}");
    });

    [Fact]
    public void ButonMetniDikeydeKirpilmaz() => UiThread.Run(() =>
    {
        var button = new Button { Content = "İptali Onayla" };
        var host = UiThread.Host(button, 240, 80);
        host.Measure(new Size(240, 80));
        host.Arrange(new Rect(0, 0, 240, 80));
        host.UpdateLayout();

        var content = Descendants(button).OfType<ContentPresenter>().Single();
        var usableHeight = button.ActualHeight - button.Padding.Top - button.Padding.Bottom
            - button.BorderThickness.Top - button.BorderThickness.Bottom;

        Assert.True(usableHeight >= content.DesiredSize.Height + 2,
            $"Buton metni için {usableHeight:F1}px alan var; metin {content.DesiredSize.Height:F1}px istiyor.");
    });

    [Fact]
    public void KompaktFiltreAlanlariVeButonAyniYuksekliktedir() => UiThread.Run(() =>
    {
        var textBox = new TextBox { Text = "Ara" };
        var comboBox = new ComboBox { ItemsSource = new[] { "Aktif" }, SelectedIndex = 0 };
        var datePicker = new DatePicker { SelectedDate = DateTime.Today };
        var button = new Button { Content = "Ara" };
        var root = new StackPanel { Width = 320 };
        UiThread.ApplyResources(root);
        textBox.Style = (Style)root.FindResource("FieldCompact");
        comboBox.Style = (Style)root.FindResource("FieldCompactCombo");
        datePicker.Style = (Style)root.FindResource("FieldCompactDate");
        button.Style = (Style)root.FindResource("Action");
        root.Children.Add(textBox); root.Children.Add(comboBox); root.Children.Add(datePicker); root.Children.Add(button);

        root.Measure(new Size(320, 300));
        root.Arrange(new Rect(0, 0, 320, root.DesiredSize.Height));
        root.UpdateLayout();

        Assert.Equal(34, textBox.ActualHeight, 1);
        Assert.Equal(textBox.ActualHeight, comboBox.ActualHeight, 1);
        Assert.Equal(textBox.ActualHeight, datePicker.ActualHeight, 1);
        Assert.Equal(textBox.ActualHeight, button.ActualHeight, 1);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
