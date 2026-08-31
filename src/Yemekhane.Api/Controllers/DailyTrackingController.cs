using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.DailyTracking;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/daily-tracking")]
[PermissionAuthorize(Permissions.AccessRead)]
public sealed class DailyTrackingController(DailyTrackingService service) : ControllerBase
{
    [HttpGet]
    public Task<DailyTrackingPage> Get([FromQuery] DailyTrackingQuery query, CancellationToken cancellationToken) =>
        service.GetAsync(query, cancellationToken);
}
