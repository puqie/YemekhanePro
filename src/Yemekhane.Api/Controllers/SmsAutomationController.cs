using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Sms;

namespace Yemekhane.Api.Controllers;

/// <summary>
/// Otomatik SMS kurallari (eski programdaki "Sms Sistemi Tanımları"): okuma/yazma ve
/// hak uyarisinin elle tetiklenmesi. Yetki: sistem ayarlariyla ayni.
/// </summary>
[ApiController]
[Route("api/settings/sms-automation")]
public sealed class SmsAutomationController(SmsAutomationService service) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.SettingsRead)]
    public Task<SmsAutomationStatus> Get(CancellationToken cancellationToken) => service.GetStatusAsync(cancellationToken);

    [HttpPut]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<SmsAutomationStatus> Save(SmsAutomationSettings settings, CancellationToken cancellationToken) =>
        service.SaveAsync(settings, cancellationToken);

    /// <summary>"Şimdi gönder": kayitli esik ve sablonla bugunun hak uyarilarini kuyruklar; kac SMS kuyruklandigini doner.</summary>
    [HttpPost("run-entitlement-warning")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<EntitlementWarningRunResult> RunEntitlementWarning(CancellationToken cancellationToken) =>
        service.RunEntitlementWarningAsync(cancellationToken);
}
