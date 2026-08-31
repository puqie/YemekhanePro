using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// Rapor CSV'si operatör tarafından Excel'de açılıyor. Öğrenci adı gibi güvenilmeyen alanlar
/// = + - @ ile başlıyorsa Excel bunları formül olarak çalıştırır, bu yüzden etkisizleştirilmeli.
/// </summary>
public sealed class ReportCsvServiceTests
{
    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    public void FormulaPrefixedValuesAreNeutralized(string value)
    {
        var escaped = ReportCsvService.Escape(value);

        Assert.StartsWith("\"'", escaped, StringComparison.Ordinal);
        Assert.EndsWith("\"", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryValueIsQuotedWithoutPrefix()
    {
        var escaped = ReportCsvService.Escape("Ayşe Yılmaz");

        Assert.Equal("\"Ayşe Yılmaz\"", escaped);
    }

    [Fact]
    public void EmbeddedQuoteIsDoubled()
    {
        var escaped = ReportCsvService.Escape("5\"A");

        Assert.Equal("\"5\"\"A\"", escaped);
    }

    [Fact]
    public void NullBecomesEmptyQuotedField()
    {
        Assert.Equal("\"\"", ReportCsvService.Escape(null));
    }
}
