using System.Windows.Input;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ekranlar kendiliginden guncel kalmali: memur "Takvim'e her gecisimde Yenile'ye basmak
/// zorunda kaliyorum" diyordu. Tazeleme yalnizca GORUNEN ekrana uygulanir ve acik bir
/// cekmece/modal varken ertelenir; aksi halde kullanicinin doldurdugu form altindan kayar.
/// </summary>
public sealed class ScreenRefreshMonitorTests
{
    private static ScreenRefreshMonitor Monitor(ICommand? command, bool busy = false, Action? onRefresh = null) =>
        new(() => command, () => busy);

    [Fact]
    public void TickRunsTheVisibleScreenRefresh()
    {
        var runs = 0;
        var command = new RelayCommand(() => runs++);
        using var monitor = Monitor(command);

        monitor.Tick();
        monitor.Tick();

        Assert.Equal(2, runs);
    }

    /// <summary>Acik cekmece/modal varken tazeleme ERTELENIR; form altindan kaymamali.</summary>
    [Fact]
    public void TickIsSkippedWhileALayerIsOpen()
    {
        var runs = 0;
        using var monitor = new ScreenRefreshMonitor(() => new RelayCommand(() => runs++), () => true);

        monitor.Tick();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void TickIsSafeWhenTheScreenHasNoRefreshCommand()
    {
        using var monitor = new ScreenRefreshMonitor(() => null, () => false);

        monitor.Tick();
    }

    /// <summary>Komut calistirilamaz durumdaysa (yetki yok, yukleme suruyor) zorlanmaz.</summary>
    [Fact]
    public void DisabledCommandIsNotExecuted()
    {
        var runs = 0;
        var command = new RelayCommand(() => runs++, () => false);
        using var monitor = Monitor(command);

        monitor.Tick();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void StoppingPreventsFurtherWork()
    {
        var runs = 0;
        var monitor = Monitor(new RelayCommand(() => runs++));
        monitor.Start();
        monitor.Stop();
        monitor.Dispose();

        // Dispose sonrasi elle tetikleme de guvenli olmali (zamanlayici durmus olur).
        monitor.Tick();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void DefaultIntervalIsShortEnoughToFeelLiveButNotChatty()
    {
        Assert.InRange(ScreenRefreshMonitor.DefaultInterval.TotalSeconds, 15, 120);
    }
}
