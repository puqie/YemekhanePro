using System.Windows.Input;
using System.Windows.Threading;

namespace Yemekhane.Desktop.Services;

/// <summary>
/// Ekranlari kendiliginden guncel tutar. Memur "Takvim'e her gecisimde Yenile'ye basmak
/// zorunda kaliyorum" diyordu: ekranlar yalnizca acilista bir kez yukleniyor, sekme
/// degistirmek veri cekmiyordu ve sunucu is verisi degisimlerini yayinlamiyordu.
///
/// Yalnizca GORUNEN ekran tazelenir: gizli ekranlari da yenilemek her turda on kusur
/// istek demektir. Zamanlayici yalnizca kullanici bir sey yazmiyorken ve onceki tazeleme
/// bittiyse calisir; bir cekmece/pencere acikken beklenir, aksi halde kullanicinin
/// doldurdugu form altindan kayar.
/// </summary>
public sealed class ScreenRefreshMonitor : IDisposable
{
    /// <summary>Varsayilan tazeleme araligi; ekran verisi bu kadar eskiyebilir.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(45);

    private readonly DispatcherTimer timer;
    private readonly Func<ICommand?> currentRefresh;
    private readonly Func<bool> isBusy;
    private bool disposed;

    /// <param name="currentRefresh">Gorunen ekranin tazeleme komutu; yoksa null.</param>
    /// <param name="isBusy">Acik katman/duzenleme var mi; varsa tazeleme ertelenir.</param>
    public ScreenRefreshMonitor(Func<ICommand?> currentRefresh, Func<bool> isBusy,
        TimeSpan? interval = null, Dispatcher? dispatcher = null)
    {
        this.currentRefresh = currentRefresh;
        this.isBusy = isBusy;
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? Dispatcher.CurrentDispatcher)
        {
            Interval = interval ?? DefaultInterval
        };
        timer.Tick += (_, _) => Tick();
    }

    public void Start() => timer.Start();
    public void Stop() => timer.Stop();

    /// <summary>Testler zamanlayiciyi beklemeden tetikleyebilsin diye ayri metot.</summary>
    public void Tick()
    {
        if (isBusy()) return;
        var command = currentRefresh();
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
    }
}
