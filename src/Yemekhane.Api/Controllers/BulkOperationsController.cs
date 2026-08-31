using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Common;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/bulk-operations")]
public sealed class BulkOperationsController(BulkOperationService service) : ControllerBase
{
    [HttpPost("preview")]
    [PermissionAuthorize(Permissions.EntitlementsBulk)]
    public Task<BulkOperationPreview> Preview(BulkCalendarOperationRequest request, CancellationToken cancellationToken) =>
        service.PreviewAsync(request, cancellationToken);

    [HttpPost("apply")]
    [PermissionAuthorize(Permissions.EntitlementsBulk)]
    [PermissionAuthorize(Permissions.CalendarManage)]
    public Task<BulkOperationResult> Apply(ApplyBulkOperationRequest request, CancellationToken cancellationToken) =>
        service.ApplyAsync(request, UserId(), cancellationToken);

    [HttpGet]
    [PermissionAuthorize(Permissions.CalendarManage)]
    public Task<BulkOperationHistoryPage> History(int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
        service.HistoryAsync(page, pageSize, cancellationToken);

    [HttpPost("{id:guid}/undo")]
    [PermissionAuthorize(Permissions.EntitlementsBulk)]
    [PermissionAuthorize(Permissions.CalendarManage)]
    public Task<UndoBulkOperationResult> Undo(Guid id, CancellationToken cancellationToken) =>
        service.UndoAsync(id, UserId(), cancellationToken);

    private Guid UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
        ? id : throw new RequestValidationException("Kimliği doğrulanmış kullanıcı bilgisi geçersiz.");
}
