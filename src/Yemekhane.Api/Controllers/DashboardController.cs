using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Dashboard;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[PermissionAuthorize(Permissions.DashboardRead)]
public sealed class DashboardController(DashboardService service) : ControllerBase
{
    /// <param name="classKind">
    /// Sinif turu suzgeci (bos ise tumu). Panel mutfaga verilecek sayiyi gosterir ve
    /// Takvim ekraniyla AYNI suzgeci kullanmalidir; aksi halde iki ekran ayni gun icin
    /// farkli sayi gosterir.
    /// </param>
    [HttpGet]
    public Task<DashboardSnapshot> Get(CancellationToken cancellationToken, string? classKind = null) =>
        service.GetAsync(classKind, cancellationToken);
}
