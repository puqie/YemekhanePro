using System.Globalization;
using System.Windows;
using Yemekhane.Desktop.Converters;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogrenci kimliginin ayirt edici oldugunu dogrular.
///
/// Bu test gercek bir veri sorunundan dogdu: ayni ad soyada sahip birden
/// fazla ogrenci var (ADA KATIRCI / ADA HASLAMACI / ADA SOYLEMEZ).
/// CashViewModel.VoidConfirmationText bir islemi iptal ederken yalnizca
/// tutar ve ad soyad gosteriyordu; kullanici hangi kisinin islemini iptal
/// ettigini bilemezdi.
/// </summary>
public sealed class StudentIdentityConverterTests
{
    private static string Convert(params object?[] values) =>
        (string)new StudentIdentityConverter()
            .Convert(values!, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void TumAlanlarVarsaHepsiniGosterir() =>
        Assert.Equal("FATİH SİDAL · No 5371 · 6E · Kart 8352094",
            Convert("FATİH", "SİDAL", "5371", "6E", "8352094"));

    [Fact]
    public void SinifYoksaOAlaniAtlar() =>
        Assert.Equal("FATİH SİDAL · No 5371 · Kart 8352094",
            Convert("FATİH", "SİDAL", "5371", null, "8352094"));

    [Fact]
    public void KartYoksaOAlaniAtlar() =>
        Assert.Equal("FATİH SİDAL · No 5371 · 6E",
            Convert("FATİH", "SİDAL", "5371", "6E", null));

    [Fact]
    public void BosMetinYokSayilir() =>
        Assert.Equal("FATİH SİDAL · No 5371",
            Convert("FATİH", "SİDAL", "5371", "  ", ""));

    /// <summary>Kimlik hicbir zaman yalnizca ad soyad olmamali.</summary>
    [Fact]
    public void NumarasizOgrenciDeAyirtEdiciBilgiTasir() =>
        Assert.Equal("FATİH SİDAL · Kart 8352094",
            Convert("FATİH", "SİDAL", null, null, "8352094"));

    /// <summary>
    /// WPF, bir baglama kaynagi cozulemediginde UnsetValue gecirir.
    /// Bu deger metne sizarsa kasiyer "No {DependencyProperty.UnsetValue}"
    /// gibi bozuk bir kimlik gorur -- hicbir sey gostermemekten daha kotudur.
    /// </summary>
    [Fact]
    public void CozulemeyenBaglamaMetneSizmaz() =>
        Assert.Equal("FATİH SİDAL · Kart 8352094",
            Convert("FATİH", "SİDAL", DependencyProperty.UnsetValue, null, "8352094"));
}
