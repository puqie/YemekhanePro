using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Sms;

namespace Yemekhane.Api.Controllers;

/// <summary>
/// SMS saglayicisini sinama: test SMS (kuyruga girmeden, kayitli ayarlarla, ham yanit dahil)
/// ve Mutlucell kontor sorgusu. Yetki: sistem ayarlarini yonetme.
/// </summary>
[ApiController]
[Route("api/settings/sms")]
public sealed class SmsProviderController(ISmsProviderProbe probe) : ControllerBase
{
    [HttpPost("test")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<SmsTestResult> SendTest(SmsTestRequest request, CancellationToken cancellationToken) =>
        probe.SendTestAsync(request, cancellationToken);

    [HttpGet("credit")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<SmsCreditResult> Credit(CancellationToken cancellationToken) => probe.QueryCreditAsync(cancellationToken);
}
