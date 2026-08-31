using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Entitlements;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/meal-entitlements")]
public sealed class MealEntitlementsController(MealEntitlementService service) : ControllerBase
{
    [HttpPost("bulk")]
    [PermissionAuthorize(Permissions.EntitlementsBulk)]
    public Task<BulkEntitlementResult> GrantBulk(BulkEntitlementRequest request, CancellationToken cancellationToken) => service.GrantBulkAsync(request, cancellationToken);
    [HttpPost("preview")]
    [PermissionAuthorize(Permissions.EntitlementsBulk)]
    public Task<EntitlementPreview> Preview(EntitlementGrantRequest request, CancellationToken cancellationToken) => service.PreviewAsync(request, cancellationToken);
    [HttpPost("apply")]
    [PermissionAuthorize(Permissions.EntitlementsBulk)]
    public Task<BulkEntitlementResult> Apply(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken) => service.ApplyAsync(request, cancellationToken);
    [HttpGet]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<MealEntitlementPage> List([FromQuery] MealEntitlementQuery query, CancellationToken cancellationToken) => service.SearchAsync(query, cancellationToken);
    [HttpGet("student/{studentId:guid}")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<IReadOnlyList<EntitlementDetails>> Student(Guid studentId, DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken) => service.ListAsync(studentId, startsOn, endsOn, cancellationToken);
    [HttpPost("{id:guid}/consume")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<bool> Consume(Guid id, CancellationToken cancellationToken) => service.TryConsumeAsync(id, cancellationToken);
    [HttpPost("{id:guid}/cancel")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<bool> Cancel(Guid id, CancellationToken cancellationToken) => service.CancelAsync(id, cancellationToken);
    [HttpPost("cancel")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<CancelEntitlementsResult> CancelBulk(CancelEntitlementsRequest request, CancellationToken cancellationToken) => service.CancelBulkAsync(request, cancellationToken);
}
