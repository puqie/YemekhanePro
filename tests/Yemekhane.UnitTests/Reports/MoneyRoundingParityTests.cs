using Yemekhane.Application.Balances;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// Para, iki farkli yoldan ayni kurusa donusmelidir.
///
/// <para>
/// Uygulama katmani <c>StudentBalanceService.ToCents</c> kullanir: decimal aritmetigi
/// ve AwayFromZero. Rapor ve Kasa sorgulari ise SQLite tarafinda double uzerinden
/// <c>ROUND(Amount * 100)</c> yapar (IEEE754, banker's yuvarlamaya yakin).
/// </para>
/// <para>
/// Bugun ikisi AYNI sonucu verir cunku tutar en fazla IKI ONDALIK tasir: sutun
/// <c>HasPrecision(18, 2)</c> ile tanimli ve <c>IncomeService</c> girisi dogrular.
/// Iki basamakta bu iki yuvarlama yontemi ayrisamaz.
/// </para>
/// <para>
/// Bu testler o dayanagi KILITLER. Hassasiyet artirilirsa (ornegin 18,4) ya da giris
/// dogrulamasi gevsetilirse yarim kurus sinirinda Kasa Ozeti ile ogrenci ekstresi
/// SESSIZCE ayrisirdi -- muhasebenin uzlastiramadigi birikimli fark olusurdu.
/// </para>
/// </summary>
public sealed class MoneyRoundingParityTests
{
    /// <summary>SQLite'in double uzerinden yaptigi donusumun bire bir taklidi.</summary>
    private static long ReportPathCents(decimal lira) => (long)Math.Round((double)lira * 100d);

    /// <summary>Iki ondalikli her tutarda iki yol AYNI kurusu verir.</summary>
    [Theory]
    [InlineData("0.01")]
    [InlineData("0.05")]
    [InlineData("12.34")]
    [InlineData("16.67")]
    [InlineData("33.33")]
    [InlineData("40.57")]
    [InlineData("99.99")]
    [InlineData("100.00")]
    [InlineData("1234.56")]
    [InlineData("48000.00")]
    public void BothPathsAgreeOnTwoDecimalAmounts(string text)
    {
        var lira = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(StudentBalanceService.ToCents(lira), ReportPathCents(lira));
    }

    /// <summary>Genis bir aralikta taranarak da ayrisma olmadigi dogrulanir.</summary>
    [Fact]
    public void BothPathsAgreeAcrossTheWholeTwoDecimalRange()
    {
        for (var cents = 1; cents <= 200_000; cents++)
        {
            var lira = cents / 100m;
            Assert.Equal(StudentBalanceService.ToCents(lira), ReportPathCents(lira));
        }
    }

    /// <summary>Kurusa cevir-geri al: iki ondalikta kayipsizdir.</summary>
    [Theory]
    [InlineData("0.01")]
    [InlineData("16.67")]
    [InlineData("1234.56")]
    public void ConvertingToCentsAndBackIsLossless(string text)
    {
        var lira = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(lira, StudentBalanceService.ToLira(StudentBalanceService.ToCents(lira)));
    }
}
