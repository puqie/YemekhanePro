using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yemekhane.Desktop;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Menunun gruplanmis oldugunu dogrular.
///
/// Once 12 oge ayni punto, ayni renk ve ayni dolguyla alt alta diziliydi;
/// sirasi da mantiksizdi (Kart Yukleme Durumu ile SMS Merkezi yan yana).
/// Kullanici her seferinde 12 satiri bastan tariyordu.
/// </summary>
[Collection("UI")]
public sealed class NavigationGroupingTests
{
    [Fact]
    public void MenuUcGrupBasligiTasir() =>
        UiThread.Run(() =>
        {
            var window = new MainWindow();
            UiThread.ApplyResources(window);

            var panel = (Panel)window.FindName("NavigationButtons")!;
            var titles = panel.Children.OfType<TextBlock>()
                .Select(block => block.Text).ToList();

            Assert.Equal(new[] { "GÜNLÜK İŞ", "TANIMLAR", "SİSTEM" }, titles);
        });

    [Fact]
    public void TumMenuOgeleriOrtakStiliKullanir() =>
        UiThread.Run(() =>
        {
            var window = new MainWindow();
            UiThread.ApplyResources(window);

            var panel = (Panel)window.FindName("NavigationButtons")!;
            var expected = (Style)window.TryFindResource("NavItem")!;

            foreach (var button in panel.Children.OfType<Button>())
                Assert.Same(expected, button.Style);
        });

    /// <summary>
    /// Secim GERCEKTEN gorsel bir fark yaratmali; sadece stilin bagli
    /// olmasi yetmez.
    ///
    /// Once secim, Tag'e "secili" yazan bir gezinme kodu ve dogrudan
    /// Background/FontWeight atamasiyla yapiliyordu -- Tag rota kimligini
    /// tasidigindan bu hicbir yerde tetiklenmiyordu (DesignSystem.xaml'deki
    /// Tag=="secili" tetikleyicisi olu kod idi) ve elle atanan koyu yesil
    /// (#25433F) lacivert kenar cubuguyla uyusmuyordu; ustelik o local
    /// deger NavItem'in IsMouseOver tetikleyicisini de o dugme icin kalici
    /// olarak devre disi birakiyordu. Bu test, secili dugmenin GERCEKTEN
    /// diger dugmelerden farkli goruntulendigini -- efektif Background
    /// uzerinden -- dogrular.
    /// </summary>
    [Fact]
    public void SeciliMenuOgesiGorselOlarakFarklidir() =>
        UiThread.Run(() =>
        {
            var window = new MainWindow();
            UiThread.ApplyResources(window);
            window.ApplyTemplate();

            window.Navigate(Yemekhane.Desktop.Services.ShellRoutes.Students);

            var panel = (Panel)window.FindName("NavigationButtons")!;
            var buttons = panel.Children.OfType<Button>().ToList();
            var selected = buttons.Single(b => Equals(b.Tag, "students"));
            var unselected = buttons.Single(b => Equals(b.Tag, "dashboard"));

            // Sablonlar olusmadan Background'a bakmak tetikleyiciyi henuz
            // uygulanmamis bulabilir; ContentPresenter/Border once olussun.
            selected.ApplyTemplate();
            unselected.ApplyTemplate();

            Assert.True(NavigationSelection.GetIsSelected(selected),
                "Secili dugmenin NavigationSelection.IsSelected degeri True olmali.");
            Assert.False(NavigationSelection.GetIsSelected(unselected),
                "Secili olmayan dugmenin NavigationSelection.IsSelected degeri False olmali.");

            // Tag rota kimligini tasimaya devam etmeli -- secim onu ezmemeli.
            Assert.Equal("students", selected.Tag);
            Assert.Equal("dashboard", unselected.Tag);

            // Local deger birakilmadigini dogrula: stil/tetikleyici gecerliligini
            // korumali (BackgroundProperty icin local deger yoksa
            // ReadLocalValue UnsetValue doner).
            Assert.Equal(DependencyProperty.UnsetValue, selected.ReadLocalValue(Button.BackgroundProperty));
            Assert.Equal(DependencyProperty.UnsetValue, selected.ReadLocalValue(Button.FontWeightProperty));

            // Tetikleyici, Button.Background'i degil, sablon icindeki "bd"
            // adli Border'in Background'ini degistirir (NavItem'in
            // ControlTemplate'i, DesignSystem.xaml). Gercek gorsel farki
            // orada aramak gerekir.
            var selectedFill = ((SolidColorBrush)((Border)selected.Template.FindName("bd", selected)!).Background).Color;
            var unselectedFill = ((SolidColorBrush)((Border)unselected.Template.FindName("bd", unselected)!).Background).Color;
            Assert.NotEqual(selectedFill, unselectedFill);

            var selectedStripe = ((SolidColorBrush)((Border)selected.Template.FindName("stripe", selected)!).Background).Color;
            var unselectedStripe = ((SolidColorBrush)((Border)unselected.Template.FindName("stripe", unselected)!).Background).Color;
            Assert.NotEqual(selectedStripe, unselectedStripe);
        });
}
