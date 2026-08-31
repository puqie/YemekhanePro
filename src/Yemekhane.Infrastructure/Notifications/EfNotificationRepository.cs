using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Notifications;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Notifications;

public sealed class EfNotificationRepository(YemekhaneDbContext dbContext) : INotificationRepository
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan DefaultDedupeWindow = TimeSpan.FromMinutes(10);

    public async Task<NotificationItem> CreateOrCoalesceAsync(CreateNotification request, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var window = request.DeduplicationWindow ?? DefaultDedupeWindow;
        var slot = string.IsNullOrWhiteSpace(request.DeduplicationKey) ? null : CreateDedupeSlot(request, now, window);
        Notification? entity = null;
        if (slot is not null)
        {
            var updated = await SqliteBusyRetry.ExecuteAsync(() => dbContext.Notifications
                .Where(x => x.DeduplicationSlot == slot)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(y => y.Count, y => y.Count + 1)
                    .SetProperty(y => y.Severity, request.Severity)
                    .SetProperty(y => y.Title, request.Title)
                    .SetProperty(y => y.Message, request.Message)
                    .SetProperty(y => y.LatestAt, now)
                    .SetProperty(y => y.UpdatedAt, now), cancellationToken),
                dbContext.ChangeTracker.Clear, cancellationToken);
            if (updated == 1)
            {
                entity = await dbContext.Notifications.AsNoTracking()
                    .SingleAsync(x => x.DeduplicationSlot == slot, cancellationToken);
                return ToItem(entity, null);
            }
        }

        if (entity is null)
        {
            entity = new Notification
            {
                Severity = request.Severity, Type = request.Type, Title = request.Title, Message = request.Message,
                Timestamp = now, LatestAt = now, RelatedEntityType = request.RelatedEntityType,
                RelatedEntityId = request.RelatedEntityId, RelatedRoute = request.RelatedRoute,
                RouteParametersJson = request.RouteParametersJson, AudiencePermission = request.AudiencePermission,
                AudienceUserId = request.AudienceUserId, DeduplicationKey = request.DeduplicationKey,
                DeduplicationSlot = slot,
                RetainUntil = request.RetainUntil ?? now + DefaultRetention
            };
            dbContext.Notifications.Add(entity);
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToItem(entity, null);
        }
        catch (DbUpdateException) when (slot is not null)
        {
            dbContext.ChangeTracker.Clear();
            await SqliteBusyRetry.ExecuteAsync(() => dbContext.Notifications.Where(x => x.DeduplicationSlot == slot)
                    .ExecuteUpdateAsync(x => x
                        .SetProperty(y => y.Count, y => y.Count + 1)
                        .SetProperty(y => y.LatestAt, now)
                        .SetProperty(y => y.UpdatedAt, now), cancellationToken),
                dbContext.ChangeTracker.Clear, cancellationToken);
            entity = await dbContext.Notifications.AsNoTracking().SingleAsync(x => x.DeduplicationSlot == slot, cancellationToken);
            return ToItem(entity, null);
        }
    }

    public async Task<NotificationPage> ListAsync(Guid userId, IReadOnlySet<string> permissions, int pageSize,
        string? cursor, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var query = Visible(userId, permissions, now);
        if (TryDecodeCursor(cursor, out var latestAt, out var id))
            query = query.Where(x => YemekhaneDbContext.JulianDay(x.LatestAt) < YemekhaneDbContext.JulianDay(latestAt) ||
                x.LatestAt == latestAt && x.Id.CompareTo(id) < 0);

        var entities = await query.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.LatestAt)).ThenByDescending(x => x.Id)
            .Take(pageSize + 1).ToListAsync(cancellationToken);
        var page = entities.Take(pageSize).ToArray();
        var ids = page.Select(x => x.Id).ToArray();
        var receipts = await dbContext.NotificationReceipts.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.NotificationId))
            .ToDictionaryAsync(x => x.NotificationId, cancellationToken);
        var unread = await UnreadCountAsync(userId, permissions, cancellationToken);
        var next = entities.Count > pageSize && page.Length > 0 ? EncodeCursor(page[^1].LatestAt, page[^1].Id) : null;
        return new NotificationPage(page.Select(x => ToItem(x, receipts.GetValueOrDefault(x.Id))).ToArray(), next, unread);
    }

    public Task<int> UnreadCountAsync(Guid userId, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken = default) => Visible(userId, permissions, DateTimeOffset.UtcNow)
        .CountAsync(x => !dbContext.NotificationReceipts.Any(r => r.NotificationId == x.Id && r.UserId == userId && r.ReadAt != null), cancellationToken);

    public async Task<bool> MarkReadAsync(Guid notificationId, Guid userId, IReadOnlySet<string> permissions,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!await Visible(userId, permissions, now).AnyAsync(x => x.Id == notificationId, cancellationToken)) return false;
        var receipt = await dbContext.NotificationReceipts.FindAsync([notificationId, userId], cancellationToken);
        if (receipt is null)
            dbContext.NotificationReceipts.Add(new NotificationReceipt { NotificationId = notificationId, UserId = userId, ReadAt = now });
        else
            receipt.ReadAt ??= now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> MarkAllReadAsync(Guid userId, IReadOnlySet<string> permissions, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var ids = await Visible(userId, permissions, now)
            .Where(x => !dbContext.NotificationReceipts.Any(r => r.NotificationId == x.Id && r.UserId == userId && r.ReadAt != null))
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var existing = await dbContext.NotificationReceipts.Where(x => x.UserId == userId && ids.Contains(x.NotificationId))
            .ToDictionaryAsync(x => x.NotificationId, cancellationToken);
        foreach (var id in ids)
        {
            if (existing.TryGetValue(id, out var receipt)) receipt.ReadAt ??= now;
            else dbContext.NotificationReceipts.Add(new NotificationReceipt { NotificationId = id, UserId = userId, ReadAt = now });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return ids.Count;
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await dbContext.Notifications.Where(x =>
            YemekhaneDbContext.JulianDay(x.RetainUntil) <= YemekhaneDbContext.JulianDay(now))
            .ExecuteDeleteAsync(cancellationToken);

    private IQueryable<Notification> Visible(Guid userId, IReadOnlySet<string> permissions, DateTimeOffset now)
    {
        var permissionArray = permissions.ToArray();
        var query = dbContext.Notifications.AsNoTracking().Where(x =>
            YemekhaneDbContext.JulianDay(x.RetainUntil) > YemekhaneDbContext.JulianDay(now) &&
            (x.AudienceUserId == null || x.AudienceUserId == userId));
        return permissionArray.Length == 0
            ? query.Where(x => x.AudiencePermission == null)
            : query.Where(x => x.AudiencePermission == null || permissionArray.Contains(x.AudiencePermission));
    }

    private static NotificationItem ToItem(Notification value, NotificationReceipt? receipt) =>
        new(value.Id, value.Severity, value.Type, value.Title, value.Message, value.Timestamp,
            value.RelatedEntityType, value.RelatedEntityId,
            value.RelatedRoute, value.RouteParametersJson, value.Count, value.LatestAt,
            receipt?.ReadAt, receipt?.AcknowledgedAt);

    private static string CreateDedupeSlot(CreateNotification request, DateTimeOffset now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(request), "Dedupe penceresi pozitif olmalıdır.");
        var bucket = now.UtcTicks / window.Ticks;
        var value = $"{request.DeduplicationKey}|{request.AudienceUserId:D}|{request.AudiencePermission}|{bucket}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string EncodeCursor(DateTimeOffset timestamp, Guid id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:D}"));

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset timestamp, out Guid id)
    {
        timestamp = default; id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks) &&
                Guid.TryParse(parts[1], out id) && (timestamp = new DateTimeOffset(ticks, TimeSpan.Zero)) != default;
        }
        catch (FormatException) { return false; }
    }
}
