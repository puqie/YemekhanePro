using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yemekhane.Desktop.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ortak sayfa iskeletinin bolgelerini dogrular.
///
/// Once her view baslik, alt baslik, arac cubugu, yukleniyor gostergesi,
/// bos liste yazisi, hata satiri ve sayfalamayi ELLE kuruyordu. Sekiz parca,
/// on iki ekran, hepsi biraz farkli.
///
/// Testler XAML metnine degil, GERCEKTEN OLUSTURULMUS gorsel agaca bakar:
/// Drawer'da gorulen bir tuzak burada da gecerli olabilir -- templated
/// icerik ApplyTemplate cagrilana kadar gorsel agaca yerlesmez. Bu yuzden
/// her test ApplyTemplate + Measure/Arrange/UpdateLayout cagirir ve
/// GetTemplateChild yerine gercek VisualTreeHelper gecisiyle dogrular.
/// </summary>
[Collection("UI")]
public sealed class PageShellTests
{
    [Fact]
    public void BaslikVeAltBaslikTasir() =>
        UiThread.Run(() =>
        {
            var shell = new PageShell { Title = "Raporlar", Subtitle = "Aylik ozet" };

            Assert.Equal("Raporlar", shell.Title);
            Assert.Equal("Aylik ozet", shell.Subtitle);
        });

    [Fact]
    public void BaslikMetniGorselAgactaGerceklesir() =>
        UiThread.Run(() =>
        {
            var shell = Build(title: "Raporlar", subtitle: "Aylik ozet");

            var titleBlock = FindText(shell, "Raporlar");
            var subtitleBlock = FindText(shell, "Aylik ozet");

            Assert.NotNull(titleBlock);
            Assert.NotNull(subtitleBlock);
            Assert.True(titleBlock!.ActualWidth > 0);
            Assert.True(subtitleBlock!.ActualWidth > 0);
        });

    [Fact]
    public void EylemlerVerildigindeGoruntulenir() =>
        UiThread.Run(() =>
        {
            var action = new Button { Content = "Yenile" };
            var shell = Build(title: "Baslik", actions: action);

            Assert.True(IsRealized(action));
            Assert.True(action.ActualWidth > 0);
        });

    [Fact]
    public void FiltrelerNullIkenBolgeCokerVeYerKaplamaz() =>
        UiThread.Run(() =>
        {
            var withoutFilters = BuildAutoSized(title: "Baslik", filters: null);
            var withFilters = BuildAutoSized(title: "Baslik", filters: new Border { Width = 40, Height = 40 });

            Assert.True(withoutFilters.DesiredSize.Height < withFilters.DesiredSize.Height,
                "Filters null oldugunda sayfa filtre bolgesi kadar kisa olmali.");
        });

    [Fact]
    public void FiltrelerVerildigindeGorselAgactaGerceklesir() =>
        UiThread.Run(() =>
        {
            var marker = new Border { Width = 40, Height = 40, Tag = "filtre-isareti" };
            var shell = Build(title: "Baslik", filters: marker);

            Assert.True(IsRealized(marker));
            Assert.True(marker.ActualWidth > 0);
        });

    [Fact]
    public void IcerikContentPresenterUzerindenGoruntulenir() =>
        UiThread.Run(() =>
        {
            var content = new TextBlock { Text = "Govde icerigi" };
            var shell = new PageShell { Title = "Baslik", Content = content };
            Apply(shell);

            Assert.True(IsRealized(content));
        });

    [Fact]
    public void AltBantSolHataSagSayfalamaTasir() =>
        UiThread.Run(() =>
        {
            var error = new TextBlock { Text = "Hata olustu" };
            var pager = new Button { Content = "Daha eski kayıtları yükle" };
            var shell = Build(title: "Baslik", footerLeft: error, footerRight: pager);

            Assert.True(IsRealized(error));
            Assert.True(IsRealized(pager));

            var errorPoint = error.TranslatePoint(new Point(0, 0), shell);
            var pagerPoint = pager.TranslatePoint(new Point(0, 0), shell);

            Assert.True(errorPoint.X < pagerPoint.X,
                "FooterLeft, FooterRight'in solunda olmali.");
        });

    [Fact]
    public void DailyTrackingViewDonusumu1440x900deTasmadanOlculur() =>
        UiThread.Run(() =>
        {
            var view = new Yemekhane.Desktop.Views.DailyTrackingView();
            UiThread.ApplyResources(view);
            const double width = 1440;
            const double height = 900;
            var host = new Border { Width = width, Height = height, Child = view };
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            Assert.True(view.ActualWidth <= width + 0.5,
                $"DailyTrackingView: {view.ActualWidth:F0}px genişlik, pencere {width}px.");
            Assert.True(view.ActualHeight <= height + 0.5,
                $"DailyTrackingView: {view.ActualHeight:F0}px yükseklik, pencere {height}px.");
        });

    private static PageShell Build(string? title = null, string? subtitle = null,
        object? actions = null, object? filters = null, object? footerLeft = null,
        object? footerRight = null, object? content = null)
    {
        var shell = new PageShell
        {
            Title = title ?? string.Empty,
            Subtitle = subtitle ?? string.Empty,
            Actions = actions,
            Filters = filters,
            FooterLeft = footerLeft,
            FooterRight = footerRight,
            Content = content ?? new Border(),
        };
        Apply(shell);
        return shell;
    }

    /// <summary>
    /// Icerige gore olculur (sonsuz alan): boylece "*" satiri kalan alani
    /// doldurmaz, Filters cokerse gercekten daha az yer kaplar. Sabit bir
    /// pencere boyutunda (orn. 1000x700) her iki durumda da "*" satiri
    /// kalan tum yuksekligi doldurur ve fark olculemez.
    /// </summary>
    private static PageShell BuildAutoSized(string? title = null, object? filters = null)
    {
        var shell = new PageShell
        {
            Title = title ?? string.Empty,
            Filters = filters,
            Content = new Border { Height = 10 },
        };
        UiThread.ApplyResources(shell);
        shell.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Yemekhane.Desktop;component/Themes/PageShell.xaml")
        });
        shell.ApplyTemplate();
        shell.Measure(new Size(1000, double.PositiveInfinity));
        shell.Arrange(new Rect(0, 0, 1000, shell.DesiredSize.Height));
        shell.UpdateLayout();
        return shell;
    }

    private static void Apply(PageShell shell)
    {
        UiThread.ApplyResources(shell);
        shell.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Yemekhane.Desktop;component/Themes/PageShell.xaml")
        });

        const double width = 1000;
        const double height = 700;
        shell.Measure(new Size(width, height));
        shell.Arrange(new Rect(0, 0, width, height));
        shell.ApplyTemplate();
        shell.UpdateLayout();
    }

    private static bool IsRealized(FrameworkElement element) =>
        PresentationSource.FromVisual(element) is not null || HasVisualParent(element);

    private static bool HasVisualParent(DependencyObject element) =>
        VisualTreeHelper.GetParent(element) is not null;

    private static TextBlock? FindText(DependencyObject root, string text)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && block.Text == text) return block;
            var nested = FindText(child, text);
            if (nested is not null) return nested;
        }
        return null;
    }
}
