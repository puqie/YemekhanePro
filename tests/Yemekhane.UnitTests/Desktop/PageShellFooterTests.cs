using System.Windows;
using System.Windows.Controls;
using Yemekhane.Desktop.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// PageShell altbilgisinde sol (hata / durum metni) ve sag (Kaydet, sayfalama) icerik
/// UST USTE BINMEMELI.
///
/// Onceden iki ContentPresenter da ayni Grid hucresindeydi; yalnizca hizalama
/// (Left / Right) ile ayriliyorlardi. Sol icerik 900px'e kadar uzayabildigi icin
/// (SettingsView FooterLeft MaxWidth=900) uzun bir sunucu hatasi sagdaki "Kaydet"
/// dugmesinin uzerine biner ve dugme tiklanamaz hale gelir.
/// </summary>
[Collection("UI")]
public sealed class PageShellFooterTests
{
    [Fact]
    public void UzunHataMetniSagdakiDugmeyiOrtmez() =>
        UiThread.Run(() =>
        {
            // Gercek ekranlardaki ayar: sarmali ve 900px sinirli (bkz. SettingsView FooterLeft).
            var left = new TextBlock { Text = new string('A', 400), MaxWidth = 900, TextWrapping = TextWrapping.Wrap };
            var right = new Button { Content = "Kaydet" };
            var shell = new PageShell { Title = "Deneme", FooterLeft = left, FooterRight = right };

            var host = UiThread.Host(shell, 1000, 600);
            host.Measure(new Size(1000, 600));
            host.Arrange(new Rect(0, 0, 1000, 600));
            host.UpdateLayout();

            Assert.True(left.ActualWidth > 0 && right.ActualWidth > 0, "altbilgi icerigi olculemedi; test anlamsiz.");

            var leftRight = left.TranslatePoint(new Point(left.ActualWidth, 0), shell).X;
            var rightLeft = right.TranslatePoint(new Point(0, 0), shell).X;

            Assert.True(leftRight <= rightLeft + 0.5,
                $"altbilgi icerigi ust uste biniyor: sol {leftRight:F0}px'e kadar, sag {rightLeft:F0}px'te basliyor.");
        });
}
