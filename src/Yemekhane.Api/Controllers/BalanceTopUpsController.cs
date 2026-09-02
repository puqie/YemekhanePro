using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;

namespace Yemekhane.Api.Controllers;

/// <summary>Kasa > Bakiye Yukle: tek transaction'da "Bakiye Yükleme" gelir islemi + defter satiri.</summary>
[ApiController]
[Route("api/cash/balance-top-ups")]
public sealed class BalanceTopUpsController(StudentBalanceService service) : ControllerBase
{
    [HttpPost]
    [PermissionAuthorize(Permissions.CashWrite)]
    public async Task<IActionResult> TopUp(BalanceTopUpRequest request, CancellationToken cancellationToken)
    {
        var result = await service.TopUpAsync(request, ActorId(), cancellationToken);
        return Created($"/api/students/{result.Entry.Id:D}/balance", result);
    }

    private Guid ActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new RequestValidationException("Operatör kimliği bulunamadı.");
    }
}
