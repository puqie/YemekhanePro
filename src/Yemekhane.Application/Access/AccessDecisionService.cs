using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Realtime;

namespace Yemekhane.Application.Access;

public sealed class AccessDecisionService(
    IAccessDecisionRepository repository,
    BusinessDayService businessDayService,
    IRealtimeEventPublisher realtimePublisher)
    : IAccessDecisionGateway
{
    private static readonly TimeZoneInfo IstanbulTimeZone = FindIstanbulTimeZone();

    public async Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request, CancellationToken cancellationToken = default)
    {
        var cardNumber = request.CardNumber?.Trim() ?? string.Empty;
        if (cardNumber.Length == 0) throw new RequestValidationException("Kart No zorunludur.");
        if (request.OperationId is { } requestedOperationId &&
            await repository.FindDecisionAsync(requestedOperationId, cancellationToken) is { } replay)
            return replay;
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.Timestamp, IstanbulTimeZone).DateTime);
        var snapshot = await repository.GetSnapshotAsync(cardNumber, request.DeviceId, request.MealTypeId, localDate, cancellationToken);
        var operationId = request.OperationId ?? Guid.NewGuid();
        AccessDecision Deny(string reason) => new("DENY", reason, snapshot.StudentId, snapshot.StudentName, request.DeviceId, request.MealTypeId, request.Timestamp, operationId);
        async Task<AccessDecision> DenyAndLog(string reason)
        {
            // Ayni OperationId ile gelen tekrar denemeler (turnike yeniden gonderimi) ayni yaniti almalidir.
            // Kazanan dal hakki tuketip ALLOW yazdiysa, kaybeden dal DENY dondurmemelidir: veri dogru olsa da
            // cagirana "yemek zaten kullanildi" denmesi turnikeyi haksiz yere kapatir.
            if (request.OperationId is { } replayedOperationId &&
                await repository.FindDecisionAsync(replayedOperationId, cancellationToken) is { } committed)
                return committed;
            var denied = Deny(reason);
            await repository.LogDeniedAsync(request, denied, cancellationToken);
            await PublishAsync(denied);
            return denied;
        }

        if (!snapshot.CardExists) return await DenyAndLog("Kart tanımsız");
        if (!snapshot.CardActive) return await DenyAndLog("Kart pasif");
        if (!snapshot.StudentActive) return await DenyAndLog("Öğrenci pasif");
        if (!snapshot.DeviceActive) return await DenyAndLog("Cihaz pasif");
        if (snapshot.GroupHoliday) return await DenyAndLog("Bugün tatil");
        if (!await businessDayService.IsBusinessDayAsync(localDate, new CalendarScope("Class", snapshot.ClassId), cancellationToken)) return await DenyAndLog("Bugün tatil");
        if (snapshot.IsOnLeave) return await DenyAndLog("Öğrenci bugün izinli");
        if (!snapshot.EntitlementId.HasValue || snapshot.EntitlementStatus != "Active") return await DenyAndLog("Bugün yemek hakkı bulunmuyor");
        if (snapshot.ConsumedQuantity >= snapshot.Quantity) return await DenyAndLog("Bu öğün daha önce kullanılmış");
        var allowed = new AccessDecision("ALLOW", "Geçiş onaylandı", snapshot.StudentId, snapshot.StudentName,
            request.DeviceId, request.MealTypeId, request.Timestamp, operationId);
        if (!await repository.TryConsumeAndLogAsync(snapshot.EntitlementId.Value, request, allowed, cancellationToken))
            return await DenyAndLog("Bu öğün daha önce kullanılmış");
        await PublishAsync(allowed);
        return allowed;
    }

    private ValueTask PublishAsync(AccessDecision decision) =>
        realtimePublisher.PublishAsync(new AccessDecisionCommittedEvent(decision.OperationId,
            decision.Decision, decision.Reason, decision.StudentId, decision.StudentName,
            decision.DeviceId, decision.MealTypeId, decision.Timestamp), CancellationToken.None);

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
