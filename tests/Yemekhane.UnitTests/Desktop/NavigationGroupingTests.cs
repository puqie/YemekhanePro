using System.Windows;
using System.Windows.Controls;
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
}
