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

    /// <summary>Tek gun ya da aralik (EndDate dahil); aralikta her gun ayri satir, ortak GroupId. Yanit ilk gun + DayCount.</summary>
    [HttpPost]
    public Task<HolidayDetails> Create(CreateHolidayRequest request, CancellationToken cancellationToken) => service.CreateAsync(request, cancellationToken);

    /// <summary>Yanlis eklenen tatil silinir; <c>wholeRange=true</c> ayni aralıktaki tum gunleri kaldirir. Haklar degismez.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool wholeRange, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, wholeRange, cancellationToken);
        return NoContent();
    }
}
