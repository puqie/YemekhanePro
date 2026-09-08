using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Cards;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.CardsManage)]
[Route("api")]
public sealed class CardsController(CardService service) : ControllerBase
{
    [HttpGet("cards/{cardNumber}")]
    public Task<CardDetails> Find(string cardNumber, CancellationToken cancellationToken) => service.FindAsync(cardNumber, cancellationToken);

    [HttpGet("students/{studentId:guid}/cards")]
    public Task<IReadOnlyList<CardDetails>> History(Guid studentId, CancellationToken cancellationToken) => service.GetHistoryAsync(studentId, cancellationToken);

    [HttpPost("students/{studentId:guid}/cards")]
    public Task<CardDetails> Assign(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken) => service.AssignAsync(studentId, request, cancellationToken);

    /// <summary>Aktif kartin ON yuzundeki baski numarasini gunceller; kart degismez.</summary>
    [HttpPut("students/{studentId:guid}/cards/printed-number")]
    public Task<CardDetails> SetPrintedNumber(Guid studentId, SetPrintedNumberRequest request, CancellationToken cancellationToken) => service.SetPrintedNumberAsync(studentId, request, cancellationToken);

    [HttpPost("students/{studentId:guid}/cards/replace")]
    public Task<CardDetails> Replace(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken) => service.ReplaceAsync(studentId, request, cancellationToken);

    [HttpDelete("cards/{cardId:guid}")]
    public async Task<IActionResult> Deactivate(Guid cardId, [FromQuery] string reason, CancellationToken cancellationToken)
    {
        await service.DeactivateAsync(cardId, reason, cancellationToken); return NoContent();
    }
}
