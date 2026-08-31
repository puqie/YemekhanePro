using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.CalendarManage)]
[Route("api/calendar")]
public sealed class CalendarController(CalendarService service) : ControllerBase
{
    [HttpGet("scopes")]
    public Task<IReadOnlyCollection<CalendarScopeOption>> Scopes(CancellationToken cancellationToken) =>
        service.ListScopesAsync(cancellationToken);

    [HttpGet("month")]
    public Task<MonthlyCalendar> Month(string month, string? scopeType, Guid? scopeId, CancellationToken cancellationToken) =>
        service.GetMonthAsync(month, scopeType, scopeId, cancellationToken);

    [HttpGet("day/{date}")]
    public Task<CalendarDayDetails> Day(DateOnly date, string? scopeType, Guid? scopeId, CancellationToken cancellationToken) =>
        service.GetDayAsync(date, scopeType, scopeId, cancellationToken);

    [HttpPost("exceptions")]
    public Task<CalendarExceptionItem> CreateException(CreateScheduleExceptionRequest request, CancellationToken cancellationToken) =>
        service.CreateExceptionAsync(request with { CreatedBy = UserId() }, cancellationToken);

    private Guid UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
        ? id : throw new RequestValidationException("Kimliği doğrulanmış kullanıcı bilgisi geçersiz.");
}
