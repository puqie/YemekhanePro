using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/audit-logs")]
[PermissionAuthorize(Permissions.AuditRead)]
public sealed class AuditLogsController(IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditLogDetails>> List([FromQuery] AuditLogFilter filter, CancellationToken cancellationToken) =>
        auditService.ListAsync(filter, cancellationToken);
}
