using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Turnstiles;

/// <summary>Dogrulanamayan bir acma komutunun kaydi: hangi islem, hangi yon, ne zaman.</summary>
public sealed record UnconfirmedGrant(Guid OperationId, TurnstileDirection Direction, DateTimeOffset RecordedAt);

/// <summary>
/// DOGRULANAMAYAN turnike acma komutlarinin kisa sureli kaydi (cihaz + kart basina tek giris).
///
/// <para>
/// Saha: hak dusuruldu, "Izin verildi" yazildi ama kol donmedi; cocuk 3 saniye sonra yeniden
/// okutunca "Bu ogun daha once kullanilmis" diye reddedildi ve kapida kaldi. Acma komutu zaman
/// asimi ya da belirsiz hatayla bittiginde (REVIEW_REQUIRED, iade YOK) bu kayit tutulur; pencere
/// icindeki yeniden okutma hak dusurmeden AYNI islem adina kapiyi yeniden dener.
/// </para>
/// <para>
/// Yalnizca DOGRULANAMAYAN komutlar icin: cihaz "acildi" dediyse (SUCCEEDED) kayit tutulmaz,
/// yoksa kart turnike ustunden geri verilip ikinci kisi gecirilebilirdi. Surec ici (bellek)
/// tutulur: turnike tek bilgisayardan surulur; yeniden baslatmada kaybolmasi kabul edilir.
/// </para>
/// </summary>
public sealed class UnconfirmedGrantRegistry(TimeProvider? timeProvider = null)
{
    /// <summary>Yeniden okutma penceresi: cocugun kapida bekleyip tekrar denemesine yetecek sure.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid DeviceId, string CardNumber), UnconfirmedGrant> _entries = new();

    /// <summary>Kac kart yeniden deneme bekliyor (tanilama ve testler icin).</summary>
    public int Count => _entries.Count;

    public void Record(Guid deviceId, string cardNumber, Guid operationId, TurnstileDirection direction)
    {
        Prune();
        _entries[(deviceId, Key(cardNumber))] = new UnconfirmedGrant(operationId, direction, _timeProvider.GetUtcNow());
    }

    /// <summary>Komut dogrulaninca kayit silinir: ayni kartin sonraki okutmasi normal karar yolundan gecer.</summary>
    public void Clear(Guid deviceId, string cardNumber) => _entries.TryRemove((deviceId, Key(cardNumber)), out _);

    /// <summary>
    /// Pencere icindeki kaydi alir ve SILER: her yeniden okutma en fazla bir deneme yapar; deneme
    /// yine dogrulanamazsa cagiran kaydi yeniden tutar.
    /// </summary>
    public bool TryTake(Guid deviceId, string cardNumber, [NotNullWhen(true)] out UnconfirmedGrant? grant)
    {
        Prune();
        if (_entries.TryRemove((deviceId, Key(cardNumber)), out var value))
        {
            grant = value;
            return true;
        }

        grant = null;
        return false;
    }

    private void Prune()
    {
        var threshold = _timeProvider.GetUtcNow() - Window;
        foreach (var entry in _entries)
        {
            if (entry.Value.RecordedAt < threshold) _entries.TryRemove(entry.Key, out _);
        }
    }

    private static string Key(string cardNumber) => cardNumber?.Trim() ?? string.Empty;
}
