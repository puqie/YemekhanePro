using Yemekhane.Application.Access;
using Yemekhane.Application.Realtime;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Devices.Turnstiles;

public sealed class TurnstileService(
    IAccessDecisionGateway accessDecisionGateway,
    ITurnstileResolver turnstileResolver,
    ITurnstileEventStore eventStore,
    TimeProvider timeProvider,
    IRealtimeEventPublisher realtimePublisher,
    NotificationService? notifications = null)
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    public async Task<TurnstileResult> ProcessCardReadAsync(AccessCheckRequest request,
        TimeSpan? commandTimeout = null, CancellationToken cancellationToken = default)
    {
        if (!turnstileResolver.TryResolve(request.DeviceId, out var turnstile) || turnstile is null)
        {
            return new(null, HardwareCommandOutcome.DeviceNotFound,
                "Turnike bulunamadı; geçiş işlemi yapılmadı.");
        }

        if (turnstile.ConnectionState != DeviceConnectionState.Connected)
        {
            await RecordAsync(request.DeviceId, null, "NONE", "DISCONNECTED",
                "Turnike bağlı değil.").ConfigureAwait(false);
            return new(null, HardwareCommandOutcome.Disconnected,
                "Turnike bağlı değil; geçiş işlemi yapılmadı.");
        }

        if (!turnstileResolver.Supports(request.DeviceId, DeviceCapability.GrantAccess))
        {
            await RecordAsync(request.DeviceId, null, "GRANT", "CAPABILITY_NOT_SUPPORTED",
                "Turnike GrantAccess komutunu desteklemiyor.").ConfigureAwait(false);
            return new(null, HardwareCommandOutcome.CapabilityNotSupported,
                "Turnike açma komutunu desteklemiyor; erişim kararı alınmadı ve geçiş verilmedi.");
        }

        var direction = string.Equals(request.Direction, "Exit", StringComparison.OrdinalIgnoreCase)
            ? TurnstileDirection.Exit
            : TurnstileDirection.Entry;

        AccessDecision decision;
        try
        {
            decision = await accessDecisionGateway.CheckAccessAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(null, HardwareCommandOutcome.Cancelled, "Kart işlemi iptal edildi.");
        }

        var isAllowed = string.Equals(decision.Decision, "ALLOW", StringComparison.Ordinal);
        var capability = isAllowed ? DeviceCapability.GrantAccess : DeviceCapability.DenyAccess;
        var command = isAllowed ? "GRANT" : "DENY";
        if (!turnstileResolver.Supports(request.DeviceId, capability))
        {
            await RecordAsync(request.DeviceId, decision.OperationId, command, "CAPABILITY_NOT_SUPPORTED",
                $"Turnike {capability} komutunu desteklemiyor.").ConfigureAwait(false);
            return new(decision, HardwareCommandOutcome.CapabilityNotSupported,
                isAllowed
                    ? "Geçiş kararı olumlu ancak turnike açma komutunu desteklemiyor; geçiş verilmedi ve inceleme gerekiyor."
                    : $"Erişim reddedildi: {decision.Reason}. Turnike red komutunu desteklemiyor.");
        }

        var timeoutValue = commandTimeout ?? DefaultCommandTimeout;
        if (timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), "Zaman aşımı sıfırdan büyük olmalıdır.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutValue);
        try
        {
            var commandTask = isAllowed
                ? turnstile.GrantAccessAsync(direction, timeout.Token)
                : turnstile.DenyAccessAsync(direction, timeout.Token);
            var commandResult = await commandTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (commandResult.Succeeded)
            {
                await RecordAsync(request.DeviceId, decision.OperationId, command, "SUCCEEDED", null)
                    .ConfigureAwait(false);
                return new(decision, HardwareCommandOutcome.Succeeded,
                    isAllowed ? "Geçiş onaylandı ve turnike açıldı." : $"Erişim reddedildi: {decision.Reason}",
                    commandResult);
            }

            var turnstileEvent = new TurnstileEventData(request.DeviceId,
                decision.OperationId, timeProvider.GetUtcNow(), command, "REVIEW_REQUIRED",
                commandResult.ErrorCode is null ? commandResult.Message : $"{commandResult.ErrorCode}: {commandResult.Message}");
            var write = await eventStore.RecordAsync(turnstileEvent, compensateConsumption: isAllowed,
                CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(turnstileEvent).ConfigureAwait(false);
            await NotifyReviewAsync(turnstileEvent).ConfigureAwait(false);
            return new(decision,
                write.ConsumptionCompensated
                    ? HardwareCommandOutcome.CompensatedRetryRequired
                    : HardwareCommandOutcome.ReviewRequired,
                write.ConsumptionCompensated
                    ? "Turnike açılamadı; tüketilen yemek hakkı güvenle iade edildi. İşlem yeniden denenmelidir."
                    : "Turnike komutu başarısız; geçiş verilmedi ve kayıt inceleme gerektiriyor.",
                commandResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordAsync(request.DeviceId, decision.OperationId, command, "REVIEW_REQUIRED",
                "Komut kullanıcı tarafından iptal edildi; fiziksel sonuç belirsiz.").ConfigureAwait(false);
            return new(decision, HardwareCommandOutcome.Cancelled,
                "Turnike komutu iptal edildi; geçiş doğrulanamadı ve kayıt inceleme gerektiriyor.");
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(request.DeviceId, decision.OperationId, command, "REVIEW_REQUIRED",
                "Komut zaman aşımına uğradı; fiziksel sonuç belirsiz.").ConfigureAwait(false);
            return new(decision, HardwareCommandOutcome.TimedOut,
                "Turnike komutu zaman aşımına uğradı; geçiş doğrulanamadı ve kayıt inceleme gerektiriyor.");
        }
        catch (Exception exception)
        {
            await RecordAsync(request.DeviceId, decision.OperationId, command, "REVIEW_REQUIRED",
                exception.Message).ConfigureAwait(false);
            return new(decision, HardwareCommandOutcome.ReviewRequired,
                "Turnike komutu tamamlanamadı; geçiş doğrulanamadı ve kayıt inceleme gerektiriyor.");
        }
    }

    private async Task RecordAsync(Guid deviceId, Guid? operationId, string command, string result, string? error)
    {
        var turnstileEvent = new TurnstileEventData(deviceId, operationId,
            timeProvider.GetUtcNow(), command, result, error);
        await eventStore.RecordAsync(turnstileEvent, compensateConsumption: false,
            CancellationToken.None).ConfigureAwait(false);
        await PublishAsync(turnstileEvent).ConfigureAwait(false);
        if (result == "REVIEW_REQUIRED") await NotifyReviewAsync(turnstileEvent).ConfigureAwait(false);
    }

    private ValueTask PublishAsync(TurnstileEventData turnstileEvent) =>
        realtimePublisher.PublishAsync(new TurnstileResultEvent(turnstileEvent.DeviceId,
            turnstileEvent.OperationId, turnstileEvent.Timestamp, turnstileEvent.Command,
            turnstileEvent.Result, turnstileEvent.Error), CancellationToken.None);

    private Task NotifyReviewAsync(TurnstileEventData value) => notifications?.CreateAsync(new CreateNotification(
        NotificationSeverities.Warning, "TurnstileReviewRequired", "Turnike işlemi inceleme bekliyor",
        value.Error ?? "Fiziksel geçiş sonucu doğrulanamadı.", "TurnstileEvent", value.OperationId?.ToString("D"), "daily-tracking",
        AudiencePermission: "access.read", DeduplicationKey: $"turnstile-review:{value.DeviceId:D}:{value.OperationId}"), CancellationToken.None)
        ?? Task.CompletedTask;
}
