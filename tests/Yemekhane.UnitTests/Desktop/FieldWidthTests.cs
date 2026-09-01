using System.Windows;
using System.Windows.Controls;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Giris kutularinin KULLANILABILIR genislikte olmasi.
///
/// Bu test elle denerken bulunan bir hatadan dogdu: Ayarlar ekraninda okul
/// adi, adres ve iletisim kutulari 1440px'lik pencerede yaklasik 95px
/// genisligindeydi; saginda 1000px'den fazla bos alan duruyordu. Adres gibi
/// uzun bir metni bu kutuya girmek pratikte imkansizdir.
///
/// Sebep: kutulari saran StackPanel'in HorizontalAlignment="Left" olmasi.
/// StackPanel yatayda icerigine gore daralir, bu yuzden cocuklardaki
/// Stretch ve MinWidth beklendigi gibi calismaz.
///
/// Mevcut duzen testleri bunu yakalayamaz: onlar panelin PENCEREYE sigip
/// sigmadigini olcer, kutunun KULLANILABILIR olup olmadigini degil.
/// </summary>
[Collection("UI")]
public sealed class FieldWidthTests
{
    /// <summary>Bir metin kutusunun rahat kullanilabilmesi icin gereken en az genislik.</summary>
    private const double UsableWidth = 220;

    /// <summary>Gorunumu gercek bir pencere boyutunda olcup yerlestirir.</summary>
    private static List<(string Name, double Width)> MeasureTextBoxes(
        Func<FrameworkElement> create, double windowWidth = 1440, double windowHeight = 900)
    {
        var results = new List<(string, double)>();
        UiThread.Run(() =>
        {
            var view = create();
            UiThread.ApplyResources(view);
            var host = new Border { Width = windowWidth, Height = windowHeight, Child = view };
            host.Measure(new Size(windowWidth, windowHeight));
            host.Arrange(new Rect(0, 0, windowWidth, windowHeight));
            host.UpdateLayout();

            foreach (var box in Descendants(view).OfType<TextBox>())
            {
                // Gorunmeyen sekmelerdeki kutular olculmez: onlar zaten
                // yerlestirilmemistir ve genislikleri sifirdir.
                // IsVisible KONTROL EDILMEZ: TabControl yalnizca secili sekmeyi
                // olusturur ve olcum aninda gorunurluk henuz yerlesmemis olabilir.
                // ActualWidth > 0 sarti, gercekten yerlestirilmis kutulari secer.
                if (box.ActualWidth <= 0) continue;
                results.Add((Describe(box), box.ActualWidth));
            }
        });
        return results;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>Kutuyu hata mesajinda taninabilir kilar.</summary>
    private static string Describe(TextBox box)
    {
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        return binding?.ParentBinding.Path.Path ?? box.Name ?? "(adsız)";
    }

    [Fact]
    public void SettingsFieldsAreWideEnoughToTypeIntoOnAFullHdScreen()
    {
        var measured = MeasureTextBoxes(() => new SettingsView());

        // Hicbir kutu olculmediyse test bir sey KANITLAMAZ; sessizce yesil
        // donmesi, gercek bir hatayi gormezden gelmek olurdu.
        Assert.True(measured.Count > 0, "Hiçbir metin kutusu ölçülemedi; test anlamsız.");

        var narrow = measured
            .Where(x => x.Width < UsableWidth)
            .ToList();

        Assert.True(narrow.Count == 0,
            $"Ayarlar ekranında {narrow.Count} kutu {UsableWidth:F0}px'den dar — " +
            $"kullanıcı adres gibi uzun metinleri giremez:{Environment.NewLine}" +
            string.Join(Environment.NewLine, narrow.Select(x => $"  {x.Name}: {x.Width:F0}px")));
    }

    [Fact]
    public void SettingsFieldsStayUsableOnASmallLaptopScreen()
    {
        // Okul idaresinde 1366x768 dizustu yaygindir.
        var narrow = MeasureTextBoxes(() => new SettingsView(), 1366, 768)
            .Where(x => x.Width < UsableWidth)
            .ToList();

        Assert.True(narrow.Count == 0,
            $"1366x768 ekranda {narrow.Count} kutu çok dar:{Environment.NewLine}" +
            string.Join(Environment.NewLine, narrow.Select(x => $"  {x.Name}: {x.Width:F0}px")));
    }
}
