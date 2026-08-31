using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/income")]
public sealed class IncomeController(IncomeService service) : ControllerBase
{
    [HttpGet("types")]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<IReadOnlyList<IncomeTypeDetails>> ListTypes(bool includeInactive, CancellationToken cancellationToken) =>
        service.ListTypesAsync(includeInactive, cancellationToken);

    [HttpGet("types/{id:guid}")]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<IncomeTypeDetails> GetType(Guid id, CancellationToken cancellationToken) =>
        service.GetTypeAsync(id, cancellationToken);

    [HttpPost("types")]
    [PermissionAuthorize(Permissions.CashManage)]
    public async Task<IActionResult> CreateType(SaveIncomeTypeRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateTypeAsync(request, ActorId(), cancellationToken);
        return CreatedAtAction(nameof(GetType), new { id = created.Id }, created);
    }

    [HttpPut("types/{id:guid}")]
    [PermissionAuthorize(Permissions.CashManage)]
    public Task<IncomeTypeDetails> UpdateType(Guid id, SaveIncomeTypeRequest request, CancellationToken cancellationToken) =>
        service.UpdateTypeAsync(id, request, ActorId(), cancellationToken);

    [HttpDelete("types/{id:guid}")]
    [PermissionAuthorize(Permissions.CashManage)]
    public async Task<IActionResult> DeactivateType(Guid id, CancellationToken cancellationToken)
    {
        await service.DeactivateTypeAsync(id, ActorId(), cancellationToken);
        return NoContent();
    }

    [HttpPost("transactions")]
    [PermissionAuthorize(Permissions.CashWrite)]
    public async Task<IActionResult> Record(CreateIncomeTransactionRequest request, CancellationToken cancellationToken)
    {
        var created = await service.RecordAsync(request, ActorId(), cancellationToken);
        return Created("/api/income/transactions", created);
    }

    [HttpPost("transactions/{id:guid}/void")]
    [PermissionAuthorize(Permissions.CashWrite)]
    public Task<IncomeTransactionDetails> Void(Guid id, VoidIncomeTransactionRequest request, CancellationToken cancellationToken) =>
        service.VoidAsync(id, request.Reason, ActorId(), cancellationToken);

    [HttpGet("transactions")]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<PagedResult<IncomeTransactionDetails>> ListTransactions(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? incomeTypeId, [FromQuery] Guid? studentId,
        [FromQuery] string? cardNumber, [FromQuery] bool? isVoided,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        service.ListAsync(new IncomeTransactionFilter(from, to, incomeTypeId, studentId, cardNumber, isVoided, page, pageSize), cancellationToken);

    private Guid ActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new RequestValidationException("Operatör kimliği bulunamadı.");
    }
}
