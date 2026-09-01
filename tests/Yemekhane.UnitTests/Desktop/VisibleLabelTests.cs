using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Kullanicinin GOZLE gordugu etiketler ve sutun basliklari.
///
/// Bu testler elle denerken bulunan iki hatadan dogdu:
///   1) Gunluk Takip ve Kasa ekranlarinda filtre kutularinin gorsel etiketi
///      yoktu; yan yana bes bos kutu duruyor, hangisinin ne oldugu
///      anlasilmiyordu. AutomationProperties.Name vardi -- yani ekran
///      okuyucu kullananlar icin sorun yoktu, GOREN kullanici icin vardi.
///   2) Ogrenciler tablosunda "BUGÜNKÜ HAK" ve "BUGÜN GİRİŞ" basliklari
///      sutun genisligine sigmiyor, "BUGÜNKÜ HA..." diye kirpiliyordu.
///
/// Ogrenciler ekranindaki filtreler zaten dogru kalibi kullaniyor
/// (her kutunun ustunde Label stilinde bir TextBlock); bu testler o kalibin
/// tum ekranlarda uygulandigini garanti eder.
/// </summary>
[Collection("UI")]
public sealed class VisibleLabelTests
{
    private static string ViewsDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Yemekhane.sln")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return Path.Combine(directory!, "src", "Yemekhane.Desktop", "Views");
    }

    /// <summary>Metnin belirtilen yazi tipiyle kaplayacagi genisligi olcer.</summary>
    private static double MeasureWidth(string text, double fontSize, FontWeight weight)
    {
        double width = 0;
        UiThread.Run(() =>
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = weight,
                FontFamily = new FontFamily("Segoe UI"),
            };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = block.DesiredSize.Width;
        });
        return width;
    }

    // ------------------------------------------------- sutun basliklari

    /// <summary>
    /// Her DataGrid sutun basligi, sutun genisligine SIGMALIDIR.
    ///
    /// Kirpilan bir baslik ("BUGÜNKÜ HA...") kullaniciyi sutunun ne oldugu
    /// konusunda tahmine zorlar. DataGrid basliklarinda kalin yazi ve her iki
    /// yanda dolgu vardir; olcum bunu hesaba katar.
    /// </summary>
    [Theory]
    [InlineData("StudentsView.xaml")]
    [InlineData("CashView.xaml")]
    [InlineData("DailyTrackingView.xaml")]
    [InlineData("MealEntitlementsView.xaml")]
    public void EveryFixedWidthColumnHeaderFitsItsColumn(string view)
    {
        // DataGridColumnHeader varsayilan dolgusu ve siralama oku icin pay.
        const double headerPadding = 16;

        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));
        var tooNarrow = new List<string>();

        foreach (Match column in Regex.Matches(xaml, @"<DataGrid\w*Column\b[^>]*?/?>", RegexOptions.Singleline))
        {
            var header = Regex.Match(column.Value, @"Header=""([^""]*)""").Groups[1].Value;
            var widthText = Regex.Match(column.Value, @"Width=""(\d+)""").Groups[1].Value;

            // Yildiz veya Auto genislikli sutunlar kendilerini ayarlar.
            if (header.Length == 0 || widthText.Length == 0) continue;

            var available = double.Parse(widthText, System.Globalization.CultureInfo.InvariantCulture);
            var needed = MeasureWidth(header, fontSize: 11, FontWeights.SemiBold) + headerPadding;

            if (needed > available)
                tooNarrow.Add($"  '{header}': {needed:F0}px gerekiyor, sütun {available:F0}px");
        }

        Assert.True(tooNarrow.Count == 0,
            $"{view}: {tooNarrow.Count} sütun başlığı kırpılıyor — kullanıcı sütunun " +
            $"ne olduğunu tahmin etmek zorunda kalır:{Environment.NewLine}" +
            string.Join(Environment.NewLine, tooNarrow));
    }

    // ------------------------------------------------- gorsel etiketler

    /// <summary>
    /// Filtre satirindaki her giris denetiminin GORSEL bir etiketi olmalidir.
    ///
    /// AutomationProperties.Name yeterli degildir: o yalnizca ekran okuyucuya
    /// konusur. Goren kullanici yan yana bes bos kutu gorur ve hangisine ne
    /// yazacagini bilemez.
    ///
    /// Sayim yaklasimi: filtre panelindeki giris denetimi sayisi, o paneldeki
    /// Label stilli TextBlock sayisindan fazla olmamalidir.
    /// </summary>
    [Theory]
    [InlineData("DailyTrackingView.xaml")]
    [InlineData("CashView.xaml")]
    public void FilterInputsHaveVisibleLabelsNotOnlyScreenReaderNames(string view)
    {
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));

        foreach (Match panel in Regex.Matches(xaml, @"<WrapPanel\b.*?</WrapPanel>", RegexOptions.Singleline))
        {
            var body = panel.Value;

            // Yalnizca FILTRE panelleri denetlenir: icinde bir "Filtrele" ya da
            // benzeri uygulama butonu bulunanlar.
            if (!body.Contains("Filtrele", StringComparison.Ordinal)
                && !body.Contains("ApplyFiltersCommand", StringComparison.Ordinal)) continue;

            var inputs = Regex.Matches(body, @"<(TextBox|ComboBox|DatePicker)\b").Count;
            var labels = Regex.Matches(body, @"Style=""\{StaticResource Label\}""").Count;

            Assert.True(labels >= inputs,
                $"{view}: filtre satırında {inputs} giriş kutusu var ama {labels} görsel etiket. " +
                "Kullanıcı hangi kutuya ne yazacağını göremiyor " +
                "(AutomationProperties.Name yalnızca ekran okuyucuya konuşur).");
        }
    }
}
