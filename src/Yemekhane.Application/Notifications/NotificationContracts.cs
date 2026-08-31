using Yemekhane.Application.Realtime;

namespace Yemekhane.Application.Notifications;

public static class NotificationSeverities
{
    public const string Success = "Success";
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
        { Success, Info, Warning, Error };
}

public sealed record CreateNotification(
    string Severity,
    string Type,
    string Title,
    string Message,
    string? RelatedEntityType = null,
    string? RelatedEntityId = null,
    string? RelatedRoute = null,
    string? RouteParametersJson = null,
    string? AudiencePermission = null,
    Guid? AudienceUserId = null,
    string? DeduplicationKey = null,
    TimeSpan? DeduplicationWindow = null,
    DateTimeOffset? RetainUntil = null);

public sealed record NotificationItem(
    Guid Id,
    string Severity,
    string Type,
    string Title,
    string Message,
    DateTimeOffset Timestamp,
    string? RelatedEntityType,
    string? RelatedEntityId,
    string? RelatedRoute,
    string? RouteParametersJson,
    int Count,
    DateTimeOffset LatestAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? AcknowledgedAt);

public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor, int UnreadCount);

public interface INotificationRepository
{
    Task<NotificationItem> CreateOrCoalesceAsync(CreateNotification request, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<NotificationPage> ListAsync(Guid userId, IReadOnlySet<string> permissions, int pageSize,
        string? cursor, CancellationToken cancellationToken = default);
    Task<int> UnreadCountAsync(Guid userId, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken = default);
    Task<bool> MarkReadAsync(Guid notificationId, Guid userId, IReadOnlySet<string> permissions,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<int> MarkAllReadAsync(Guid userId, IReadOnlySet<string> permissions, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class NotificationService(
    INotificationRepository repository,
    IRealtimeEventPublisher realtimePublisher,
    TimeProvider timeProvider)
{
    public async Task<NotificationItem> CreateAsync(CreateNotification request,
        CancellationToken cancellationToken = default)
    {
        if (!NotificationSeverities.All.Contains(request.Severity))
            throw new ArgumentException("Geçersiz bildirim seviyesi.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Bildirim türü, başlığı ve mesajı zorunludur.", nameof(request));

        var item = await repository.CreateOrCoalesceAsync(request, timeProvider.GetUtcNow(), cancellationToken);
        await realtimePublisher.PublishAsync(new NotificationEvent(item.Id, item.Severity, item.Type,
            item.Title, item.Message, item.Timestamp, item.RelatedEntityType, item.RelatedEntityId,
            item.RelatedRoute, item.RouteParametersJson,
            item.Count, item.LatestAt, request.AudiencePermission, request.AudienceUserId), cancellationToken);
        return item;
    }

    public Task<NotificationPage> ListAsync(Guid userId, IReadOnlySet<string> permissions, int pageSize,
        string? cursor, CancellationToken cancellationToken = default) =>
        repository.ListAsync(userId, permissions, Math.Clamp(pageSize, 1, 100), cursor, cancellationToken);

    public Task<int> UnreadCountAsync(Guid userId, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken = default) => repository.UnreadCountAsync(userId, permissions, cancellationToken);

    public Task<bool> MarkReadAsync(Guid id, Guid userId, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken = default) =>
        repository.MarkReadAsync(id, userId, permissions, timeProvider.GetUtcNow(), cancellationToken);

    public Task<int> MarkAllReadAsync(Guid userId, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken = default) =>
        repository.MarkAllReadAsync(userId, permissions, timeProvider.GetUtcNow(), cancellationToken);
}
