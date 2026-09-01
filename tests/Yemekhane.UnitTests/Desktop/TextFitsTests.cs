using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Metinlerin kabina SIGDIGININ dogrulanmasi.
///
/// Bu test elle denerken bulunan bir hatadan dogdu: kenar cubugundaki marka
/// yazisi "YEMEKHANEPRC" gorunuyordu -- son harf kirpilmisti.
///
/// Mevcut duzen testleri bunu yakalayamaz: onlar panelin PENCEREYE tasip
/// tasmadigini olcer. Bir StackPanel icindeki metin ise sessizce kirpilir,
/// tasma olarak raporlanmaz. Kullanicinin gordugu tek sey yarim bir kelimedir.
/// </summary>
[Collection("UI")]
public sealed class TextFitsTests
{
    /// <summary>
    /// Metnin belirtilen yazi tipiyle kaplayacagi genisligi olcer.
    /// WPF'in kendi metin bicimlendiricisi kullanilir; tahmin degil olcumdur.
    /// </summary>
    private static double MeasureWidth(string text, double fontSize, FontWeight weight, string family)
    {
        double width = 0;
        UiThread.Run(() =>
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = weight,
                FontFamily = new FontFamily(family),
            };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = block.DesiredSize.Width;
        });
        return width;
    }

    /// <summary>
    /// Kenar cubugundaki marka yazisi TAM gorunmelidir.
    ///
    /// Kalan genislik hesabi (MainWindow.xaml):
    ///   kenar cubugu           214
    ///   Grid Margin sol/sag  -  18 - 14
    ///   StackPanel Margin    -   4
    ///   logo                 -  34
    ///   logo sag bosluk      -  10
    ///   ------------------------------
    ///   metne kalan             134 px
    /// </summary>
    [Fact]
    public void TheBrandNameFitsInTheSidebar()
    {
        const double available = 214 - 18 - 14 - 4 - 34 - 10;

        var width = MeasureWidth("YEMEKHANEPRO", fontSize: 16, FontWeights.Bold, "Segoe UI");

        Assert.True(width <= available,
            $"Marka yazısı {width:F0}px yer kaplıyor ama {available:F0}px alan var; " +
            "son harf kırpılır ve kullanıcı 'YEMEKHANEPRC' görür.");
    }

    /// <summary>
    /// Kenar cubugundaki alt baslik da sigmalidir.
    /// </summary>
    [Fact]
    public void TheSidebarSubtitleFits()
    {
        const double available = 214 - 18 - 14 - 4 - 34 - 10;

        var width = MeasureWidth("Yemekhane yönetimi", fontSize: 12, FontWeights.Normal, "Segoe UI");

        Assert.True(width <= available,
            $"Alt başlık {width:F0}px, alan {available:F0}px; metin kırpılır.");
    }

    /// <summary>
    /// Menu ogeleri de sigmalidir: kirpilmis bir menu adi kullaniciyi
    /// hangi ekrana gittigi konusunda tereddutte birakir.
    /// </summary>
    [Theory]
    [InlineData("▦   Dashboard")]
    [InlineData("Günlük Takip")]
    [InlineData("Öğrenciler")]
    [InlineData("Yemek Hakedişleri")]
    [InlineData("Takvim / Tatil")]
    [InlineData("Cihazlar / Turnikeler")]
    [InlineData("Kart Yükleme Durumu")]
    [InlineData("SMS Merkezi")]
    [InlineData("Kasa")]
    [InlineData("Raporlar")]
    [InlineData("Ayarlar")]
    [InlineData("Sicil Aktar")]
    public void EveryMenuLabelFitsInTheSidebar(string label)
    {
        // Menu butonu: kenar cubugu - Grid Margin - buton Padding (11 x 2)
        const double available = 214 - 18 - 14 - 22;

        var width = MeasureWidth(label, fontSize: 12, FontWeights.SemiBold, "Segoe UI");

        Assert.True(width <= available,
            $"'{label}' {width:F0}px yer kaplıyor, alan {available:F0}px; menü adı kırpılır.");
    }
}
