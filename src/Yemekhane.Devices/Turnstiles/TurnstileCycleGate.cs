using System.Collections.Concurrent;

namespace Yemekhane.Devices.Turnstiles;

/// <summary>
/// Turnikenin fiziksel dongusunu bekleten kapi (cihaz basina).
///
/// <para>
/// Saha: turnikenin bir gecisten sonra ~5 sn'lik mesgul suresi var; o surede gelen okutmaya
/// program "Izin verildi" deyip hakki dusuruyor, cihaz komutu "tamam" diye onayliyor ama kol
/// donmuyor -- hak yaniyor. Bu kapi, son acma komutundan sonra dongu suresi dolmadan gelen
/// okutmayi KARAR ALINMADAN bekletir: hak ancak turnike hazir olunca duser.
/// </para>
/// <para>
/// Surec icinde (bellek) tutulur; okuyucu isleyicisi okutmalari sirayla isledigi icin bekleyen
/// okutmalar kendiliginden kuyruk olusturur. Yeniden baslatmada kaybolmasi kabul edilir.
/// </para>
/// </summary>
public sealed class TurnstileCycleGate(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastCommandAt = new();

    /// <summary>Acma komutu gonderilmek uzere: dongu bu andan itibaren sayilir.</summary>
    public void MarkCommand(Guid deviceId) => _lastCommandAt[deviceId] = _timeProvider.GetUtcNow();

    /// <summary>Son komuttan bu yana dongu dolmadiysa kalan sure; dolduysa ya da hic komut yoksa sifir.</summary>
    public TimeSpan Remaining(Guid deviceId, TimeSpan minimumInterval)
    {
        if (minimumInterval <= TimeSpan.Zero || !_lastCommandAt.TryGetValue(deviceId, out var last)) return TimeSpan.Zero;
        var remaining = last + minimumInterval - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Kalan dongu suresi kadar bekler; iptal edilirse bekleme de iptal olur (karar alinmaz).</summary>
    public async Task WaitUntilReadyAsync(Guid deviceId, TimeSpan minimumInterval, CancellationToken cancellationToken)
    {
        var remaining = Remaining(deviceId, minimumInterval);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
    }
}
