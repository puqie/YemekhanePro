using System.Collections.Concurrent;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;

namespace Yemekhane.Api.Devices;

public sealed class TurnstileReaderOptions
{
    /// <summary>Bagli okuyucularin dongusu var mi diye bakma araligi.</summary>
    public int SupervisionIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Akisi hatayla biten okuyucu bu kadar sure sonra yeniden dinlenir. Aninda yeniden baslatmak,
    /// "bagli" gorunen ama okumayan bir cihazda her tikte hata gunlugu uretirdi.
    /// </summary>
    public int RestartDelaySeconds { get; init; } = 10;
}

/// <summary>
/// Turnikeye bagli her kart okuyucuyu (SC403 "Turnike bagli") dinler ve her okutmayi
/// <see cref="ITurnstileCardHandler"/> uzerinden gecis boru hattina (karar → hak dusumu → role) verir.
///
/// <para>
/// Bu isci olmadan boru hattinin HICBIR uretim cagirani yoktu: cihaz baglansa, kart okunsa bile
/// olay kimseye ulasmiyor ve turnike acilmiyordu (2026-09-08'de sahada bulundu). API ucu
/// (<c>POST api/access/check</c>) yalnizca karar verir, turnikeyi surmez.
/// </para>
/// <para>
/// Cihaz basina bir okuma dongusu vardir. Dongu, akis kesilince (cihaz koptu) biter; DeviceManager
/// cihazi yeniden baglayinca gozetim tiki donguyu yeniden baslatir. Tek bir kartin islenmesindeki
/// hata donguyu bitirmez: sonraki okutmalar islenmeye devam eder.
/// </para>
/// </summary>
public sealed class TurnstileCardReadWorker(
    IServiceScopeFactory scopes,
    DeviceRegistry registry,
    TimeProvider timeProvider,
    TurnstileReaderOptions options,
    ILogger<TurnstileCardReadWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogLoopStarted = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(6201, nameof(TurnstileCardReadWorker)),
        "{Device} okuyucusu dinleniyor; okutmalar turnike boru hattina verilecek.");

    private static readonly Action<ILogger, string, Exception?> LogLoopFailed = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6202, nameof(TurnstileCardReadWorker)),
        "{Device} okuyucusunun kart akisi kesildi; cihaz yeniden baglaninca dinleme surer.");

    private static readonly Action<ILogger, string, string, Exception?> LogCardFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(6203, nameof(TurnstileCardReadWorker)),
        "{Card} karti ({Device}) islenemedi; turnike acilmadi. Sonraki okutmalar islenmeye devam eder.");

    private readonly ConcurrentDictionary<Guid, Task> _loops = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _failedAt = new();

    /// <summary>Okuma dongusu suren cihazlar (tanilama ve test).</summary>
    public IReadOnlyCollection<Guid> ActiveReaders =>
        _loops.Where(pair => !pair.Value.IsCompleted).Select(pair => pair.Key).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SupervisionIntervalSeconds), timeProvider);
        do
        {
            EnsureReaders(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Bagli ve turnikeli her okuyucu icin dongu baslatir, bitmis donguleri temizler.
    /// Zamanlayiciyi beklemeden calistirilabilmesi icin ayri tutulmustur (test ve elle tetikleme).
    /// </summary>
    public void EnsureReaders(CancellationToken cancellationToken)
    {
        foreach (var (deviceId, loop) in _loops)
        {
            if (loop.IsCompleted) _loops.TryRemove(deviceId, out _);
        }

        var now = timeProvider.GetUtcNow();
        foreach (var device in registry.Devices)
        {
            if (device is not ICardReader reader || device is not ITurnstile) continue;
            if (device.ConnectionState != DeviceConnectionState.Connected) continue;
            if (_loops.ContainsKey(device.Id)) continue;
            if (_failedAt.TryGetValue(device.Id, out var failedAt) &&
                now - failedAt < TimeSpan.FromSeconds(options.RestartDelaySeconds)) continue;

            _loops[device.Id] = ReadLoopAsync(reader, cancellationToken);
        }
    }

    private async Task ReadLoopAsync(ICardReader reader, CancellationToken cancellationToken)
    {
        await Task.Yield();
        LogLoopStarted(logger, reader.Name, null);
        try
        {
            await foreach (var card in reader.ReadCardsAsync(cancellationToken).ConfigureAwait(false))
            {
                await HandleAsync(reader, card, cancellationToken).ConfigureAwait(false);
            }

            _failedAt[reader.Id] = timeProvider.GetUtcNow();
            LogLoopFailed(logger, reader.Name, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _failedAt[reader.Id] = timeProvider.GetUtcNow();
            LogLoopFailed(logger, reader.Name, exception);
        }
    }

    private async Task HandleAsync(ICardReader reader, CardReadEvent card, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ITurnstileCardHandler>();
            await handler.HandleAsync(reader.Id, card, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCardFailed(logger, card.CardNumber, reader.Name, exception);
        }
    }
}
