using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Access;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Realtime;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Api.Devices;

/// <summary>
/// Turnikeye bagli bir okuyucudan gelen tek okutmayi gecis kararina ve turnike komutuna cevirir.
/// </summary>
public interface ITurnstileCardHandler
{
    /// <summary>Null: karar alinamadi (ornegin su saatte tanimli ogun yok); turnike acilmadi.</summary>
    Task<TurnstileResult?> HandleAsync(Guid deviceId, CardReadEvent card, CancellationToken cancellationToken);
}

/// <summary>Cihazin yapilandirilmis yonu (Entry/Exit). Turnike nesnesi yonu bilmez; veritabani bilir.</summary>
public interface ITurnstileDeviceDirectory
{
    Task<string?> GetDirectionAsync(Guid deviceId, CancellationToken cancellationToken);
}

public sealed class EfTurnstileDeviceDirectory(YemekhaneDbContext db) : ITurnstileDeviceDirectory
{
    public Task<string?> GetDirectionAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.Devices.AsNoTracking()
            .Where(device => device.Id == deviceId)
            .Select(device => (string?)device.Direction)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// Okutma → ogun secimi → <see cref="TurnstileService"/> (karar, hak dusumu, role, iade).
///
/// Ogun, sunucu saatine gore ogun tanimlarindaki saat penceresinden secilir. Damga da sunucu
/// saatidir: sahada cihaz saati 83 dakika geri cikmisti; cihaz saatine guvenmek ogunu, gece
/// yarisina yakin gunu bile kaydirirdi.
/// </summary>
public sealed class TurnstileCardHandler(
    TurnstileService turnstiles,
    IMealTypeRepository mealTypes,
    ITurnstileDeviceDirectory devices,
    IAccessDecisionRepository decisions,
    IRealtimeEventPublisher realtime,
    TimeProvider timeProvider,
    ILogger<TurnstileCardHandler> logger) : ITurnstileCardHandler
{
    /// <summary>Ogun secilemeyen okutmanin red gerekcesi; Gunluk Takip'te bu metinle gorunur.</summary>
    public const string NoMealWindowReason = "Şu saatte tanımlı öğün yok";

    private static readonly TimeZoneInfo IstanbulTimeZone = FindIstanbulTimeZone();

    private static readonly Action<ILogger, string, Guid, Exception?> LogNoMealWindow = LoggerMessage.Define<string, Guid>(
        LogLevel.Warning, new EventId(6101, nameof(TurnstileCardHandler)),
        "{Card} karti okutuldu ama su saatte tanimli bir ogun yok; {Device} turnikesi acilmadi. Ogun tanimlarina saat araligi girin.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogResult =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Information, new EventId(6102, nameof(TurnstileCardHandler)),
            "{Card} karti: karar {Decision}, turnike {Outcome}. {Message}");

    public async Task<TurnstileResult?> HandleAsync(Guid deviceId, CardReadEvent card, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);

        var now = timeProvider.GetUtcNow();
        var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IstanbulTimeZone).DateTime);
        var meals = await mealTypes.ListAsync(includeInactive: false, cancellationToken).ConfigureAwait(false);
        var meal = MealWindowResolver.Resolve(meals, localTime);
        var direction = await devices.GetDirectionAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (meal is null)
        {
            LogNoMealWindow(logger, card.CardNumber, deviceId, null);
            await RecordUnscheduledReadAsync(deviceId, card, now, direction, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var request = new AccessCheckRequest(card.CardNumber, deviceId, meal.Id, now,
            string.IsNullOrWhiteSpace(direction) ? "Entry" : direction, card.ReaderSource,
            OperationId: Guid.NewGuid());

        var result = await turnstiles.ProcessCardReadAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        LogResult(logger, card.CardNumber, result.AccessDecision?.Decision ?? "-",
            result.HardwareOutcome.ToString(), result.Message, null);
        return result;
    }

    /// <summary>
    /// Ogun secilemeyen okutma GORUNUR bir red olarak kaydedilir ve canli yayinlanir (Gunluk Takip).
    /// Once yalnizca gunluge dusuyordu; okul 11:10'da kart okutup "sistemde dusmedi" dedi.
    /// Ogun yoktur: MealTypeId Guid.Empty gider, depo bunu bos sutun olarak yazar.
    /// </summary>
    private async Task RecordUnscheduledReadAsync(Guid deviceId, CardReadEvent card, DateTimeOffset now,
        string? direction, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var request = new AccessCheckRequest(card.CardNumber, deviceId, Guid.Empty, now,
            string.IsNullOrWhiteSpace(direction) ? "Entry" : direction, card.ReaderSource, OperationId: operationId);
        var denied = new AccessDecision("DENY", NoMealWindowReason, null, null, deviceId, Guid.Empty, now, operationId);
        await decisions.LogDeniedAsync(request, denied, cancellationToken).ConfigureAwait(false);
        await realtime.PublishAsync(new AccessDecisionCommittedEvent(operationId, denied.Decision, denied.Reason,
            null, null, deviceId, Guid.Empty, now), cancellationToken).ConfigureAwait(false);
    }

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
