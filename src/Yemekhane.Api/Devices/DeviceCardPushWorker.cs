using Yemekhane.Application.Devices;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Sf300;

namespace Yemekhane.Api.Devices;

public sealed class DeviceCardPushOptions
{
    public int IntervalSeconds { get; init; } = 30;
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Gecici hatada bir kart bu kadar denendikten sonra kalici sayilir. Sinirsiz deneme,
    /// arizali tek bir kartin kuyrugu surekli mesgul etmesine yol acardi.
    /// </summary>
    public int MaxAttempts { get; init; } = 10;
}

/// <summary>
/// Bekleyen kartlari SF300 cihazlarina yukler ve sonucu kart-cihaz durumuna yazar.
///
/// Her cihaz kendi kuyrugunu isler: bir cihazin cevrimdisi olmasi digerlerine kart
/// yuklenmesini engellemez. Bir cihazdaki hata o cihazin dongusunu bitirir, digerleri surer.
/// </summary>
public sealed class DeviceCardPushWorker(
    IServiceScopeFactory scopes,
    DeviceRegistry registry,
    TimeProvider timeProvider,
    DeviceCardPushOptions options,
    ILogger<DeviceCardPushWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogDeviceFailure = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6001, nameof(DeviceCardPushWorker)),
        "{Device} cihazina kart yuklenirken hata olustu.");

    private static readonly Action<ILogger, int, string, Exception?> LogPushed = LoggerMessage.Define<int, string>(
        LogLevel.Information, new EventId(6002, nameof(DeviceCardPushWorker)),
        "{Count} kart {Device} cihazina yuklendi.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds), timeProvider);
        do
        {
            try
            {
                await PushPendingCardsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                LogDeviceFailure(logger, "tum cihazlar", exception);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Tum bagli cihazlarin bekleyen kart kuyrugunu bir kez isler.
    /// Zamanlayiciyi beklemeden calistirilabilmesi icin ayri tutulmustur (test ve elle tetikleme).
    /// </summary>
    public async Task PushPendingCardsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<IDeviceCardSyncService>();

        foreach (var device in registry.Devices)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (device is not IAccessController controller ||
                device.ConnectionState != DeviceConnectionState.Connected) continue;

            try
            {
                await PushDeviceAsync(sync, controller, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // Tek bir cihazin hatasi digerlerinin kuyrugunu durdurmamalidir.
                LogDeviceFailure(logger, device.Name, exception);
            }
        }
    }

    private async Task PushDeviceAsync(IDeviceCardSyncService sync, IAccessController controller,
        CancellationToken cancellationToken)
    {
        var pending = await sync.GetPendingAsync(controller.Id, options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0) return;

        var pushed = 0;
        foreach (var card in pending)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                if (card.IsRemoval)
                {
                    // Iptal edilen kart cihazdan GERCEKTEN silinmelidir; yalnizca veritabaninda
                    // isaretlemek, kartin turnikeden gecmeye devam etmesi anlamina gelir.
                    await controller.DeleteCardAsync(card.CardNumber, cancellationToken).ConfigureAwait(false);
                    await sync.MarkRemovedAsync(controller.Id, card.CardId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await controller.SendCardAsync(card.CardNumber, card.StudentId.ToString("D"), cancellationToken)
                        .ConfigureAwait(false);
                    await sync.MarkLoadedAsync(controller.Id, card.CardId, cancellationToken).ConfigureAwait(false);
                }

                pushed++;
            }
            catch (DeviceConnectionException exception)
            {
                var permanent = IsPermanent(exception.ErrorCode) || card.AttemptCount + 1 >= options.MaxAttempts;
                await sync.MarkFailedAsync(controller.Id, card.CardId, exception.ErrorCode ?? exception.Message,
                    permanent, cancellationToken).ConfigureAwait(false);

                // Baglanti koptuysa bu turda kalan kartlari denemek anlamsizdir.
                if (IsDisconnected(exception.ErrorCode)) break;
            }
            catch (DeviceCapabilityException exception)
            {
                // Cihaz bu komutu desteklemiyorsa tekrar denemek durumu degistirmez.
                await sync.MarkFailedAsync(controller.Id, card.CardId, exception.Message, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                // Bozuk kart verisi yalnizca o karti dusurmelidir; cihazin tum kuyrugunu degil.
                await sync.MarkFailedAsync(controller.Id, card.CardId, exception.Message, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (pushed > 0) LogPushed(logger, pushed, controller.Name, null);
    }

    /// <summary>
    /// Kalici hatalar yeniden denenmez: gecersiz kart veya dolu cihaz hafizasi tekrar denemekle duzelmez,
    /// yalnizca kuyrugu mesgul eder ve gercek sorunlari gizler.
    ///
    /// Siniflandirma <see cref="DeviceErrorCodes"/> uzerinden yapilir; kodlar saticiya gore
    /// karsilastirilirsa her yeni cihaz ailesi (SC403 gibi) sessizce siniflandirma disinda kalir
    /// ve olu bir cihaz kart kart denenmeye devam eder.
    /// </summary>
    private static bool IsPermanent(string? errorCode) => DeviceErrorCodes.IsPermanent(errorCode);

    private static bool IsDisconnected(string? errorCode) => DeviceErrorCodes.IsDisconnected(errorCode);
}
