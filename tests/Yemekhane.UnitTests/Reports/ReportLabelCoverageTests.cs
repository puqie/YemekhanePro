using Yemekhane.Application.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// Rapor DOSYALARINDA (PDF/Excel/CSV) ham Ingilizce kod basilmamalidir.
///
/// <para>
/// Ekran sozlugu ile rapor sozlugu AYRI dosyalardir ve ayrisabilirler. Olculdu:
/// 12 kod yalnizca ekranda cevriliydi, rapor dosyasinda ham geciyordu. Okul memurunun
/// eline gecen belge yari Ingilizce oluyordu.
/// </para>
/// <para>
/// Tatil/Aktarim raporu iki ayri yerden etkileniyordu: DURUM sutunu tatil TURUNU
/// tasir (Official/Administrative/...) ve ACIKLAMA sutunu "tatil adi / davranis kodu"
/// bicimindedir -- ikinci parca da hamdi ("Yılbaşı / NextBusinessDay").
/// </para>
/// </summary>
public sealed class ReportLabelCoverageTests
{
    private static ReportRow Row(ReportType type, string? status = null, string? description = null) =>
        new() { Id = Guid.NewGuid(), Type = type, Status = status, Description = description };

    /// <summary>Hakedis raporunda "Yakıldı" cevrilir; once ham "Forfeited" basiliyordu.</summary>
    [Fact]
    public void TheForfeitedStatusIsTranslated() =>
        Assert.Equal("Yakıldı", ReportText.Status(Row(ReportType.MealEntitlement, "Forfeited")));

    /// <summary>SMS raporundaki ara durumlar da cevrilir.</summary>
    [Theory]
    [InlineData("Sending", "Gönderiliyor")]
    [InlineData("RetryScheduled", "Yeniden denenecek")]
    [InlineData("Sent", "Gönderildi")]
    public void TheSmsStatusesAreTranslated(string code, string expected) =>
        Assert.Equal(expected, ReportText.Status(Row(ReportType.Sms, code)));

    /// <summary>Tatil raporunda DURUM sutunu tatil TURUDUR ve cevrilir.</summary>
    [Theory]
    [InlineData("Official", "Resmî tatil")]
    [InlineData("Administrative", "İdari izin")]
    [InlineData("Trip", "Gezi")]
    [InlineData("Bulk", "Toplu işlem")]
    public void TheHolidayTypesAreTranslated(string code, string expected) =>
        Assert.Equal(expected, ReportText.Status(Row(ReportType.HolidayTransfer, code)));

    /// <summary>Tatil aciklamasindaki davranis kodu cevrilir; tatilin ADI oldugu gibi kalir.</summary>
    [Fact]
    public void TheHolidayBehaviourInTheDescriptionIsTranslated() =>
        Assert.Equal("Yılbaşı / Sonraki iş gününe aktar",
            ReportText.Description(Row(ReportType.HolidayTransfer, "Official", "Yılbaşı / NextBusinessDay")));

    /// <summary>Davranis parcasi yoksa aciklama oldugu gibi kalir.</summary>
    [Fact]
    public void AHolidayDescriptionWithoutABehaviourIsUnchanged() =>
        Assert.Equal("Yılbaşı", ReportText.Description(Row(ReportType.HolidayTransfer, "Official", "Yılbaşı")));

    /// <summary>
    /// HICBIR rapor turunde ham Ingilizce kod kalmamali: bilinen tum durum kodlari
    /// icin ceviri, girdiden FARKLI olmalidir.
    /// </summary>
    [Theory]
    [InlineData(ReportType.MealEntitlement, "Active")]
    [InlineData(ReportType.MealEntitlement, "Cancelled")]
    [InlineData(ReportType.MealEntitlement, "Transferred")]
    [InlineData(ReportType.MealEntitlement, "Forfeited")]
    [InlineData(ReportType.Sms, "Pending")]
    [InlineData(ReportType.Sms, "Failed")]
    [InlineData(ReportType.HolidayTransfer, "Other")]
    public void NoKnownCodeIsLeftUntranslated(ReportType type, string code)
    {
        var label = ReportText.Status(Row(type, code));

        Assert.NotEqual(code, label);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }
}
