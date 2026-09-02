using System.Globalization;
using System.Windows;
using Yemekhane.Desktop.Converters;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Arayuzun tamamen Turkce olmasi gerekirken kullaniciya ham API kodlari
/// gorunuyordu: Gunluk Takip'te "ALLOW"/"DENY", hakedislerde "Active"/"Manual",
/// raporlarda "VOIDED", Ayarlar'da "Information" ve "Disabled".
///
/// En sinsi iki hata bu testin asil konusu:
/// 1. Taninmayan bir kod bos string'e cevrilirse ekrandan KAYBOLUR. Sunucu yeni
///    bir durum kodu eklediginde hucre bosalir ve kimse fark etmez.
/// 2. API ayni kavrami iki bicimde yolluyor: hakedis kaydinda "Active",
///    rapor projeksiyonunda "ACTIVE". Duyarli karsilastirma birini cevirmez.
/// </summary>
public sealed class EnumTextConverterTests
{
    private static string Convert(object? value, string? parameter) =>
        (string)new EnumTextConverter()
            .Convert(value!, typeof(string), parameter!, CultureInfo.InvariantCulture);

    [Fact]
    public void AllowKararıTurkceGosterilir() =>
        Assert.Equal("İzin Verildi", Convert("ALLOW", "Decision"));

    [Fact]
    public void DenyKararıTurkceGosterilir() =>
        Assert.Equal("Reddedildi", Convert("DENY", "Decision"));

    [Fact]
    public void KucukHarfliDurumCevrilir() =>
        Assert.Equal("Aktif", Convert("active", "Status"));

    [Fact]
    public void BuyukHarfliDurumCevrilir() =>
        Assert.Equal("Aktif", Convert("ACTIVE", "Status"));

    [Fact]
    public void RaporunVoidedDurumuCevrilir() =>
        Assert.Equal("İptal", Convert("VOIDED", "Status"));

    [Fact]
    public void ManuelKaynakCevrilir() =>
        Assert.Equal("Elle", Convert("Manual", "Source"));

    [Theory]
    [InlineData("Information", "Bilgi")]
    [InlineData("Error", "Hata")]
    [InlineData("Warning", "Uyarı")]
    public void LogSeviyeleriCevrilir(string code, string expected) =>
        Assert.Equal(expected, Convert(code, "LogLevel"));

    [Theory]
    [InlineData("Error", "Hata")]
    [InlineData("Reconnecting", "Yeniden bağlanıyor")]
    [InlineData("Offline", "Çevrimdışı")]
    [InlineData("Online", "Çevrimiçi")]
    public void CihazDurumlariCevrilir(string code, string expected) =>
        Assert.Equal(expected, Convert(code, "DeviceStatus"));

    [Fact]
    public void KapaliSyncDurumuCevrilir() =>
        Assert.Equal("Kapalı", Convert("Disabled", "SyncState"));

    /// <summary>
    /// EN ONEMLI TEST: taninmayan kod ekrandan kaybolmamali, ham haliyle gorunmeli.
    /// Bos string dondurursek yeni bir sunucu durumu sessizce yok olur.
    /// </summary>
    [Fact]
    public void BilinmeyenDegerAynenDoner() =>
        Assert.Equal("WEIRD_CODE", Convert("WEIRD_CODE", "Status"));

    [Fact]
    public void BilinmeyenSozlukAdiDegeriAynenDondurur() =>
        Assert.Equal("ALLOW", Convert("ALLOW", "OlmayanSozluk"));

    [Fact]
    public void ParametresizCagriDegeriAynenDondurur() =>
        Assert.Equal("ALLOW", Convert("ALLOW", null));

    [Fact]
    public void NullDegerBosMetinDondurur() =>
        Assert.Equal("", Convert(null, "Status"));

    [Fact]
    public void BoslukDegerBosMetinDondurur() =>
        Assert.Equal("", Convert("   ", "Status"));

    /// <summary>Baglama cozulemedigi zaman WPF UnsetValue yollar; "UnsetValue" yazmamali.</summary>
    [Fact]
    public void CozulmemisBaglamaBosMetinDondurur() =>
        Assert.Equal("", Convert(DependencyProperty.UnsetValue, "Status"));

    [Fact]
    public void GeriDonusumDesteklenmez() =>
        Assert.Throws<NotSupportedException>(() => new EnumTextConverter()
            .ConvertBack("Aktif", typeof(string), "Status", CultureInfo.InvariantCulture));

    /// <summary>
    /// Turkce kultur "I" harfini "ı"ya cevirir. Sozluk anahtarlari İngilizce kod
    /// oldugundan karsilastirma ordinal olmali, aksi halde "Information" eslesmez.
    /// </summary>
    [Fact]
    public void TurkceKulturAltindaBileInformationEslesir()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("Bilgi", Convert("INFORMATION", "LogLevel"));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
