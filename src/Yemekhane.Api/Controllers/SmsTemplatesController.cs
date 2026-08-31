using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Sms;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.SmsManage)]
[Route("api/sms-templates")]
public sealed class SmsTemplatesController(SmsTemplateService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<SmsTemplateDetails>> List(bool includeInactive, CancellationToken cancellationToken) =>
        service.ListAsync(includeInactive, cancellationToken);

    [HttpGet("{id:guid}")]
    public Task<SmsTemplateDetails> Get(Guid id, CancellationToken cancellationToken) =>
        service.GetAsync(id, cancellationToken);

    [HttpPost]
    public async Task<IActionResult> Create(SaveSmsTemplateRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public Task<SmsTemplateDetails> Update(
        Guid id, SaveSmsTemplateRequest request, CancellationToken cancellationToken) =>
        service.UpdateAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await service.DeactivateAsync(id, cancellationToken);
        return NoContent();
    }
}
