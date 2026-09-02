using Yemekhane.Desktop.Converters;

namespace Yemekhane.UnitTests.DailyTracking;

/// <summary>
/// Gunluk Takip ve Dashboard'daki "Neden" sutunu cihazdan gelen ham "OK" kodunu gosteriyordu;
/// arayuz tamamen Turkce olmali. Karar servisinin urettigi nedenler zaten Turkce oldugu icin
/// yalnizca kod bicimindeki degerler cevrilir, Turkce metin AYNEN kalir.
/// </summary>
public sealed class DailyTrackingReasonTextTests
{
    [Fact]
    public void OkNedeniTurkceGosterilir() =>
        Assert.Equal("Geçiş onaylandı", EnumTextConverter.Translate("OK", "Reason"));

    [Fact]
    public void KucukHarfliOkDaCevrilir() =>
        Assert.Equal("Geçiş onaylandı", EnumTextConverter.Translate("ok", "Reason"));

    [Fact]
    public void TurkceNedenAynenKalir() =>
        Assert.Equal("Kart pasif", EnumTextConverter.Translate("Kart pasif", "Reason"));
}
