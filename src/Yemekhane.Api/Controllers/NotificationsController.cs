using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[PermissionAuthorize(Permissions.NotificationsRead)]
public sealed class NotificationsController(NotificationService service) : ControllerBase
{
    [HttpGet]
    public Task<NotificationPage> List([FromQuery] int pageSize = 30, [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        service.ListAsync(UserId(), PermissionSet(), pageSize, cursor, cancellationToken);

    [HttpGet("unread-count")]
    public async Task<object> UnreadCount(CancellationToken cancellationToken) =>
        new { Count = await service.UnreadCountAsync(UserId(), PermissionSet(), cancellationToken) };

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        await service.MarkReadAsync(id, UserId(), PermissionSet(), cancellationToken) ? NoContent() : NotFound();

    [HttpPost("read-all")]
    public async Task<object> MarkAllRead(CancellationToken cancellationToken) =>
        new { Count = await service.MarkAllReadAsync(UserId(), PermissionSet(), cancellationToken) };

    private Guid UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new UnauthorizedAccessException();

    private HashSet<string> PermissionSet() => User.FindAll(Permissions.ClaimType)
        .Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
}
