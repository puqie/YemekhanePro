using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Maintenance;

namespace Yemekhane.Api.Controllers;

/// <summary>Bakim islemleri: yil sonu sifirlamasi (onizleme + onayli calistirma).</summary>
[ApiController]
[Route("api/maintenance")]
public sealed class MaintenanceController(IYearEndResetService reset) : ControllerBase
{
    [HttpGet("year-end-reset/preview")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<YearEndResetPreview> Preview(CancellationToken cancellationToken) => reset.PreviewAsync(cancellationToken);

    [HttpPost("year-end-reset")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<YearEndResetResult> Reset(YearEndResetRequest request, CancellationToken cancellationToken) =>
        reset.ResetAsync(request.Confirmation, cancellationToken);
}
