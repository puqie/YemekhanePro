using System.Security.Claims;
using Yemekhane.Application.Audit;

namespace Yemekhane.Api.Infrastructure;

public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    public Guid? UserId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
