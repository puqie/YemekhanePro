namespace Yemekhane.Application.Realtime;

public static class RealtimeChannels
{
    public const string AccessDecisions = "access-decisions";
    public const string TurnstileResults = "turnstile-results";
    public const string DeviceStatuses = "device-statuses";
    public const string Notifications = "notifications";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AccessDecisions,
        TurnstileResults,
        DeviceStatuses,
        Notifications
    };
}

public sealed record AccessDecisionCommittedEvent(
    Guid OperationId,
    string Decision,
    string Reason,
    Guid? StudentId,
    string? StudentName,
    Guid DeviceId,
    Guid MealTypeId,
    DateTimeOffset OccurredAt);

public sealed record TurnstileResultEvent(
    Guid DeviceId,
    Guid? OperationId,
    DateTimeOffset OccurredAt,
    string Command,
    string Result,
    string? Error);

public sealed record DeviceStatusChangedEvent(
    Guid DeviceId,
    string DeviceName,
    string PreviousStatus,
    string Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset? CheckedAt = null,
    string? Message = null,
    string? ErrorCode = null,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? NextRetryAt = null);

public sealed record NotificationEvent(
    Guid NotificationId,
    string Severity,
    string Type,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    string? RelatedEntityType = null,
    string? RelatedEntityId = null,
    string? RelatedRoute = null,
    string? RouteParametersJson = null,
    int Count = 1,
    DateTimeOffset? LatestAt = null,
    string? AudiencePermission = null,
    Guid? AudienceUserId = null);

public interface IRealtimeEventPublisher
{
    ValueTask PublishAsync(AccessDecisionCommittedEvent realtimeEvent,
        CancellationToken cancellationToken = default);
    ValueTask PublishAsync(TurnstileResultEvent realtimeEvent,
        CancellationToken cancellationToken = default);
    ValueTask PublishAsync(DeviceStatusChangedEvent realtimeEvent,
        CancellationToken cancellationToken = default);
    ValueTask PublishAsync(NotificationEvent realtimeEvent,
        CancellationToken cancellationToken = default);
}
