using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/sms")]
public sealed class SmsController(SmsService service, BulkSmsService bulkService, SmsTemplateService templateService) : ControllerBase
{
    [HttpPost]
    [PermissionAuthorize(Permissions.SmsSend)]
    public async Task<ActionResult<SmsLogDetails>> Enqueue(
        EnqueueSmsRequest request, CancellationToken cancellationToken)
    {
        var result = await service.EnqueueAsync(request, cancellationToken);
        return Accepted(result);
    }

    [HttpGet]
    [PermissionAuthorize(Permissions.SmsRead)]
    public Task<PagedResult<SmsLogDetails>> List(
        [FromQuery] string? status,
        [FromQuery] string? phone,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? studentId,
        [FromQuery] string? provider,
        [FromQuery] string? student,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        service.ListAsync(new SmsHistoryFilter(status, phone, from, to, page, pageSize, studentId, provider, student), cancellationToken);

    [HttpGet("targets")]
    [PermissionAuthorize(Permissions.SmsSend)]
    public Task<SmsTargetOptions> Targets([FromQuery] string? search, CancellationToken cancellationToken) =>
        bulkService.TargetsAsync(search, cancellationToken);

    [HttpGet("templates")]
    [PermissionAuthorize(Permissions.SmsSend)]
    public Task<IReadOnlyList<SmsTemplateDetails>> SendTemplates(CancellationToken cancellationToken) =>
        templateService.ListAsync(false, cancellationToken);

    [HttpPost("bulk/preview")]
    [PermissionAuthorize(Permissions.SmsSend)]
    public Task<BulkSmsPreview> Preview(BulkSmsRequest request, CancellationToken cancellationToken) =>
        bulkService.PreviewAsync(request, cancellationToken);

    [HttpPost("bulk/apply")]
    [PermissionAuthorize(Permissions.SmsSend)]
    public async Task<ActionResult<BulkSmsEnqueueResult>> Apply(ApplyBulkSmsRequest request, CancellationToken cancellationToken) =>
        Accepted(await bulkService.ApplyAsync(request, cancellationToken));

    [HttpPost("{id:guid}/retry")]
    [PermissionAuthorize(Permissions.SmsSend)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        await service.RetryAsync(id, cancellationToken);
        return Accepted();
    }
}
