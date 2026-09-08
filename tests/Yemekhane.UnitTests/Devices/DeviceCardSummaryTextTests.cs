using Yemekhane.Application.Devices;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kart Yukleme Durumu sayfasi SC403 icin "Tum kartlar yuklu / bekleyen yok" diyordu; okul bunu
/// "kart turnikeye gitmiyor" diye okudu. SC403 kart tutmaz: okutma sunucuya gelir, karar orada
/// verilir. Sayfa bunu acikca soylemeli.
/// </summary>
public sealed class DeviceCardSummaryTextTests
{
    [Fact]
    public void DeviceThatDoesNotStoreCardsSaysSoInsteadOfAllLoaded()
    {
        var sc403 = new DeviceCardSummaryViewModel(new DeviceCardSummary(Guid.NewGuid(), "Yemekhane Turnikesi", 0, 0, 0, StoresCards: false));

        Assert.Contains("Kart yüklenmez", sc403.StatusText, StringComparison.Ordinal);
        Assert.False(sc403.NeedsAttention);
    }

    [Fact]
    public void CardStoringDeviceKeepsTheLoadedAndPendingTexts()
    {
        Assert.Equal("Tüm kartlar yüklü",
            new DeviceCardSummaryViewModel(new DeviceCardSummary(Guid.NewGuid(), "SF300", 12, 0, 0)).StatusText);
        Assert.Equal("3 kart bekliyor",
            new DeviceCardSummaryViewModel(new DeviceCardSummary(Guid.NewGuid(), "SF300", 12, 3, 0)).StatusText);
    }
}
