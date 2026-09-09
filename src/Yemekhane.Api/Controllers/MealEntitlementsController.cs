using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
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
    public Task<BulkEntitlementResult> Apply(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken) => service.ApplyAsync(request, ActorId(), cancellationToken);
    [HttpGet]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<MealEntitlementPage> List([FromQuery] MealEntitlementQuery query, CancellationToken cancellationToken) => service.SearchAsync(query, cancellationToken);
    [HttpGet("student/{studentId:guid}")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<IReadOnlyList<EntitlementDetails>> Student(Guid studentId, DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken) => service.ListAsync(studentId, startsOn, endsOn, cancellationToken);
    /// <summary>Ogrencinin ogun bazinda kalan hakki, bitis gunu ve yenileme gunu.</summary>
    [HttpGet("student/{studentId:guid}/periods")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<IReadOnlyList<EntitlementPeriodSummary>> Periods(Guid studentId, CancellationToken cancellationToken) => service.PeriodsAsync(studentId, cancellationToken);
    /// <summary>Hakedisi bitmek uzere olan (ve bitmis) ogrenciler; yenileme takibi.</summary>
    [HttpGet("expiring")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<IReadOnlyList<EntitlementPeriodSummary>> Expiring([FromQuery] ExpiringEntitlementQuery query, CancellationToken cancellationToken) => service.ExpiringAsync(query, cancellationToken);
    [HttpPost("{id:guid}/consume")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<bool> Consume(Guid id, CancellationToken cancellationToken) => service.TryConsumeAsync(id, cancellationToken);
    [HttpPost("{id:guid}/cancel")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    public Task<bool> Cancel(Guid id, CancellationToken cancellationToken) => service.CancelAsync(id, cancellationToken);
    [HttpPost("cancel")]
    [PermissionAuthorize(Permissions.EntitlementsManage)]
    // Iptal, varsa hakedis tahsilatini da geri alir: hak iptal edilip para kasada kalirsa
    // kasa ile hak birbirini tutmaz.
    public Task<CancelEntitlementsResult> CancelBulk(CancelEntitlementsRequest request, CancellationToken cancellationToken) => service.CancelBulkWithRefundAsync(request, ActorId(), cancellationToken);

    private Guid ActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new RequestValidationException("Operatör kimliği bulunamadı.");
    }
}
