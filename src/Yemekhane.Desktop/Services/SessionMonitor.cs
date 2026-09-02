using System.Windows.Threading;

namespace Yemekhane.Desktop.Services;

/// <summary>
/// Erisim belirtecinin suresini izler ve doldugunda <see cref="SessionExpired"/> olayini
/// BIR KEZ tetikler; oturum yenilenince (Set ile ileri bir ExpiresAt) yeniden silahlanir.
/// </summary>
/// <remarks>
/// API belirteci 15 dakikada dolar ve yenileme ucu yoktur. Onceden ekranlar tek tek
/// "oturum gerekiyor" hatasi veriyor, tek cikis yolu uygulamayi kapatip acmakti; acik
/// formdaki veri gidiyordu. Bu izleyici kabuk duzeyinde tek bir "yeniden giris" katmani
/// acilmasini saglar. Zamani belirtecin kendisinden okur; API'ye istek atmaz.
/// </remarks>
public sealed class SessionMonitor : IDisposable
{
    private readonly MutableJwtSession session;
    private readonly DispatcherTimer timer;
    private readonly TimeProvider clock;
    private bool raised;

    public event EventHandler? SessionExpired;

    public SessionMonitor(MutableJwtSession session, TimeSpan? pollInterval = null, TimeProvider? clock = null, Dispatcher? dispatcher = null)
    {
        this.session = session;
        this.clock = clock ?? TimeProvider.System;
        timer = new DispatcherTimer(pollInterval ?? TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) => Check(), dispatcher ?? Dispatcher.CurrentDispatcher);
    }

    public void Start() => timer.Start();

    /// <summary>Durumu simdi degerlendirir; suresi dolmussa olayi tetikler. Testler dogrudan cagirir.</summary>
    public void Check()
    {
        var expired = string.IsNullOrWhiteSpace(session.AccessToken) || session.ExpiresAt is null
            || session.ExpiresAt <= clock.GetUtcNow();
        if (!expired) { raised = false; return; }
        if (raised) return;
        raised = true;
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => timer.Stop();
}
