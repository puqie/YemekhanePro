using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Access;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Authorize(Policy = "Device")]
[Route("api/access")]
public sealed class AccessController(AccessDecisionService service) : ControllerBase
{
    [HttpPost("check")]
    public Task<AccessDecision> Check(AccessCheckRequest request, CancellationToken cancellationToken) => service.CheckAccessAsync(request, cancellationToken);
}
