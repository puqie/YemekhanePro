using Yemekhane.Application.Balances;

namespace Yemekhane.UnitTests.Balances;

/// <summary>
/// Bakiye hesabinin saf kurallari: toplam = duz toplam; kullanilabilir = bitis tarihi gecmis
/// yuklemelerin harcanmamis kalani dusulmus; dusumler en eski gecerli yuklemeden (FIFO) duser.
/// </summary>
public sealed class BalanceLedgerTests
{
    private static readonly DateOnly Today = new(2026, 9, 2);
    private static int sequence;

    private static LedgerLine Line(long cents, string kind, DateOnly on, DateOnly? expires = null, Guid? reference = null) =>
        new(Guid.NewGuid(), cents, kind, new DateTimeOffset(on.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(3)).AddSeconds(sequence++), on, expires, reference);

    [Fact]
    public void BosDefterSifirdir()
    {
        var totals = BalanceLedger.Compute([], Today);
        Assert.Equal(new LedgerTotals(0, 0, 0), totals);
    }

    [Fact]
    public void ToplamDuzToplamdirVeSuresizYuklemeTamamenKullanilabilir()
    {
        var totals = BalanceLedger.Compute(
        [
            Line(50_000, StudentBalanceEntryKinds.TopUp, new(2026, 9, 1)),
            Line(-7_500, StudentBalanceEntryKinds.Deduction, new(2026, 9, 1)),
            Line(-7_500, StudentBalanceEntryKinds.Deduction, new(2026, 9, 2)),
        ], Today);

        Assert.Equal(35_000, totals.TotalCents);
        Assert.Equal(35_000, totals.AvailableCents);
        Assert.Equal(0, totals.ExpiredCents);
    }

    [Fact]
    public void BitisTarihiGecenYuklemeninKalaniYanarToplamDegismez()
    {
        // 300 ₺ Agustos sonuna kadar gecerli, 75 ₺'si harcandi; Eylul'de kalan 225 ₺ yanmis sayilir.
        var totals = BalanceLedger.Compute(
        [
            Line(30_000, StudentBalanceEntryKinds.TopUp, new(2026, 8, 20), expires: new(2026, 8, 31)),
            Line(-7_500, StudentBalanceEntryKinds.Deduction, new(2026, 8, 25)),
        ], Today);

        Assert.Equal(22_500, totals.TotalCents);
        Assert.Equal(0, totals.AvailableCents);
        Assert.Equal(22_500, totals.ExpiredCents);
    }

    [Fact]
    public void BitisTarihiGununSonunaKadarGecerlidir()
    {
        var line = Line(10_000, StudentBalanceEntryKinds.TopUp, new(2026, 9, 1), expires: Today);
        Assert.Equal(10_000, BalanceLedger.Compute([line], Today).AvailableCents);
        Assert.Equal(0, BalanceLedger.Compute([line], Today.AddDays(1)).AvailableCents);
    }

    [Fact]
    public void DusumOnceEnEskiGecerliYuklemedenDuser()
    {
        // Sureli 100 ₺ (Eylul sonu) + suresiz 200 ₺; 60 ₺ dusum sureli olandan gitmeli,
        // Ekim'de yalnizca sureli kalan 40 ₺ yanmali, suresiz 200 ₺ tamamen kullanilabilir kalmali.
        var lines = new[]
        {
            Line(10_000, StudentBalanceEntryKinds.TopUp, new(2026, 9, 1), expires: new(2026, 9, 30)),
            Line(20_000, StudentBalanceEntryKinds.TopUp, new(2026, 9, 2)),
            Line(-6_000, StudentBalanceEntryKinds.Deduction, new(2026, 9, 10)),
        };

        var september = BalanceLedger.Compute(lines, new DateOnly(2026, 9, 15));
        var october = BalanceLedger.Compute(lines, new DateOnly(2026, 10, 1));

        Assert.Equal(24_000, september.AvailableCents);
        Assert.Equal(20_000, october.AvailableCents);
        Assert.Equal(4_000, october.ExpiredCents);
        Assert.Equal(24_000, october.TotalCents);
    }

    [Fact]
    public void SuresiDolmusYuklemeSonrakiDusumdenMuaftir()
    {
        // Bitis tarihinden SONRA yapilan dusum o yuklemeden dusemez: suresiz yuklemeden gider.
        var lines = new[]
        {
            Line(10_000, StudentBalanceEntryKinds.TopUp, new(2026, 8, 1), expires: new(2026, 8, 31)),
            Line(20_000, StudentBalanceEntryKinds.TopUp, new(2026, 8, 2)),
            Line(-5_000, StudentBalanceEntryKinds.Deduction, new(2026, 9, 1)),
        };

        var totals = BalanceLedger.Compute(lines, Today);

        Assert.Equal(25_000, totals.TotalCents);
        Assert.Equal(15_000, totals.AvailableCents);
        Assert.Equal(10_000, totals.ExpiredCents);
    }

    [Fact]
    public void IptalIadesiKendiYuklemesiniHedeflerHarcanmissaBakiyeEksiyeDuser()
    {
        var incomeId = Guid.NewGuid();
        var lines = new[]
        {
            Line(50_000, StudentBalanceEntryKinds.TopUp, new(2026, 9, 1), reference: incomeId),
            Line(-7_500, StudentBalanceEntryKinds.Deduction, new(2026, 9, 1)),
            Line(-50_000, StudentBalanceEntryKinds.Refund, new(2026, 9, 2), reference: incomeId),
        };

        var totals = BalanceLedger.Compute(lines, Today);

        Assert.Equal(-7_500, totals.TotalCents);
        Assert.Equal(-7_500, totals.AvailableCents);
        Assert.Equal(0, totals.ExpiredCents);
    }

    [Fact]
    public void TurnikeIadesiYeniPartiOlarakKullanilabilirKalir()
    {
        var lines = new[]
        {
            Line(10_000, StudentBalanceEntryKinds.TopUp, new(2026, 9, 1)),
            Line(-7_500, StudentBalanceEntryKinds.Deduction, new(2026, 9, 2)),
            Line(7_500, StudentBalanceEntryKinds.Refund, new(2026, 9, 2)),
        };

        var totals = BalanceLedger.Compute(lines, Today);

        Assert.Equal(10_000, totals.TotalCents);
        Assert.Equal(10_000, totals.AvailableCents);
    }
}
