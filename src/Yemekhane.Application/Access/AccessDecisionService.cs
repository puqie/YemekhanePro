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
        // BUGUNE AKTIF HAK varsa tatil / hafta sonu kontrolu ATLANIR: o gunu acan operatorun
        // kendisidir ("tatil olmasina ragmen gun ekledim, o gun hakkim var!"). Hakedis verme
        // akisi hafta sonunu ancak acikca istenince, takvim tatilini ise hic vermez; kapali gune
        // dusen aktif hak bilincli bir karardir. Tatil aktarimi calistiysa hak "Transferred"
        // olur ve bu dala girilmez. Hak yoksa kapali gun eskisi gibi "Bugün tatil" ile
        // reddedilir: bakiye yolu tatilde acilmaz.
        // Guid? olarak tutulur: derleyici null akisini bool uzerinden izleyemez ve .Value
        // CS8629 verir (Release -warnaserror ile derleme durur); "is { } id" deseni bunu cozer.
        var activeRightId = snapshot.EntitlementStatus == "Active" ? snapshot.EntitlementId : null;
        if (activeRightId is null)
        {
            if (snapshot.GroupHoliday) return await DenyAndLog("Bugün tatil");
            if (!await businessDayService.IsBusinessDayAsync(localDate, new CalendarScope("Class", snapshot.ClassId), cancellationToken)) return await DenyAndLog("Bugün tatil");
        }
        if (snapshot.IsOnLeave) return await DenyAndLog("Öğrenci bugün izinli");
        if (activeRightId is not { } entitlementId)
        {
            // Hakedis yoksa on odemeli bakiye devreye girer (eski programdaki "TL Bakiye Yukleme").
            // Ucreti 0 olan ogunde bakiye kurali yoktur: bedelsiz ogun icin para dusulmez, hak aranir.
            // Ucreti TANIMSIZ ogun: ret dogru (para dusulemez) ama sebep dogru soylenmeli.
            // Once "Bugün yemek hakkı bulunmuyor" deniyordu ve operator sorunu ogrencide
            // ariyordu; eksik olan OGUN TANIMIYDI.
            if (!snapshot.MealPriceDefined) return await DenyAndLog("Öğün ücreti tanımlı değil");
            if (snapshot.MealPriceCents <= 0) return await DenyAndLog("Bugün yemek hakkı bulunmuyor");
            if (snapshot.AvailableBalanceCents < snapshot.MealPriceCents) return await DenyAndLog(Balances.BalanceAccessReasons.InsufficientBalance);
            var paid = new AccessDecision("ALLOW", Balances.BalanceAccessReasons.BalanceUsed, snapshot.StudentId, snapshot.StudentName,
                request.DeviceId, request.MealTypeId, request.Timestamp, operationId);
            // Anlik goruntu onbellekten gelmis olabilir; depo bakiyeyi kilit altinda yeniden sayar.
            if (!await repository.TryDeductBalanceAndLogAsync(snapshot.MealPriceCents, request, paid, cancellationToken))
                return await DenyAndLog(Balances.BalanceAccessReasons.InsufficientBalance);
            await PublishAsync(paid);
            return paid;
        }
        if (snapshot.ConsumedQuantity >= snapshot.Quantity) return await DenyAndLog("Bu öğün daha önce kullanılmış");
        var allowed = new AccessDecision("ALLOW", "Geçiş onaylandı", snapshot.StudentId, snapshot.StudentName,
            request.DeviceId, request.MealTypeId, request.Timestamp, operationId);
        if (!await repository.TryConsumeAndLogAsync(entitlementId, request, allowed, cancellationToken))
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
