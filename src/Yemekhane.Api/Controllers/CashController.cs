using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Cash;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.CashRead)]
[Route("api/cash")]
public sealed class CashController(CashService service) : ControllerBase
{
    [HttpGet("summary")]
    public Task<CashSummary> Summary(
        [FromQuery] CashSummaryPeriod period = CashSummaryPeriod.Daily,
        [FromQuery] DateOnly? date = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken cancellationToken = default) =>
        service.GetSummaryAsync(period, date, from, to, cancellationToken);

    [HttpGet("daily")]
    public Task<CashSummary> Daily([FromQuery] DateOnly? date = null,
        CancellationToken cancellationToken = default) =>
        service.GetDailyAsync(date, cancellationToken);
}
