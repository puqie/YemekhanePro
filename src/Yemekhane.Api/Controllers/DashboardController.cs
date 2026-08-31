using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Dashboard;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[PermissionAuthorize(Permissions.DashboardRead)]
public sealed class DashboardController(DashboardService service) : ControllerBase
{
    [HttpGet]
    public Task<DashboardSnapshot> Get(CancellationToken cancellationToken) =>
        service.GetAsync(cancellationToken);
}
