using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Calendar;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.CalendarManage)]
[Route("api/holidays")]
public sealed class HolidaysController(HolidayService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<HolidayDetails>> List(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken) => service.ListAsync(startsOn, endsOn, cancellationToken);
    [HttpPost]
    public Task<HolidayDetails> Create(CreateHolidayRequest request, CancellationToken cancellationToken) => service.CreateAsync(request, cancellationToken);
}
