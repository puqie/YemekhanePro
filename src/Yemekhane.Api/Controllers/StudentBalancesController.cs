using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Balances;

namespace Yemekhane.Api.Controllers;

/// <summary>Ogrencinin on odemeli bakiyesi ve hareket defteri (Ogrenciler > Bakiye sekmesi).</summary>
[ApiController]
[Route("api/students/{id:guid}/balance")]
public sealed class StudentBalancesController(StudentBalanceService service) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<StudentBalanceSummary> Get(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        service.GetAsync(id, page, pageSize, cancellationToken);
}
