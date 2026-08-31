using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Entitlements;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.EntitlementsBulk)]
[Route("api/meal-transfers")]
public sealed class MealTransfersController(MealTransferService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<MealTransferDetails>> List(Guid studentId, CancellationToken cancellationToken) =>
        service.ListAsync(studentId, cancellationToken);

    [HttpPost]
    public Task<MealTransferResult> Transfer(TransferMealEntitlementsRequest request, CancellationToken cancellationToken) =>
        service.TransferAsync(request, cancellationToken);
}
