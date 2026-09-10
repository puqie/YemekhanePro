using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// "Gecmis yuklemeler ve gecmis girisler AYRI sayfalar olsun, GECEN YILLARA da bakilsin."
///
/// Iki gecmis sekmesi (Hakedisler, Gecis Gecmisi) TEK bir tarih araligini paylasir:
/// kullanici "Ceylin'in gecen yiline bakayim" derken ikisine de ayni donem icin bakar.
/// Iki ayri kutu olsaydi biri 2025'te obur 2026'da kalip sessizce yanlis karsilastirma
/// yapilabilirdi.
///
/// Aralik YALNIZCA gecmis sekmelerine gecer: kartlar, veliler ve denetim kaydi tarih
/// suzgeciyle daralmamalidir.
/// </summary>
public sealed class StudentHistoryRangeTests
{
    /// <summary>Iki gecmis sekmesi tanimli olmali; ekran tarih kutularini bunlara gore gosterir.</summary>
    [Fact]
    public void BothHistoryTabsAreRegistered()
    {
        Assert.Equal(["Entitlements", "Access History"], StudentsViewModel.HistoryTabKeys);
    }

    /// <summary>Hakedis sekmesi gecmis sekmesidir: gecmis yuklemeler oradan gorulur.</summary>
    [Fact]
    public void TheEntitlementTabIsAHistoryTab() =>
        Assert.Contains("Entitlements", StudentsViewModel.HistoryTabKeys);

    /// <summary>Gecis gecmisi de gecmis sekmesidir: hangi gun girdigi oradan gorulur.</summary>
    [Fact]
    public void TheAccessHistoryTabIsAHistoryTab() =>
        Assert.Contains("Access History", StudentsViewModel.HistoryTabKeys);

    /// <summary>Kartlar/veliler gibi sekmeler aralikla daralmamali.</summary>
    [Theory]
    [InlineData("Cards")]
    [InlineData("Parents")]
    [InlineData("Payments")]
    [InlineData("Balance")]
    [InlineData("Audit")]
    public void NonHistoryTabsAreNotDateFiltered(string key) =>
        Assert.DoesNotContain(key, StudentsViewModel.HistoryTabKeys);
}
