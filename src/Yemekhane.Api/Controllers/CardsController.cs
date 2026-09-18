using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.CardsManage)]
[Route("api")]
public sealed class CardsController(CardService service, ICardListQuery cards) : ControllerBase
{
    /// <summary>Kartlar ekrani: TUM kartlar (aktif + pasif), arama, durum suzgeci ve sayfalama.</summary>
    [HttpGet("cards")]
    public Task<CardListResult> List([FromQuery] string? search = null, [FromQuery] bool? isActive = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        cards.ListAsync(new CardListQuery(search, isActive, page, pageSize), cancellationToken);

    /// <summary>Kartlar ekranindan SECILEN pasif karti geri acar; ogrencinin aktif karti varsa 409.</summary>
    [HttpPost("cards/{cardId:guid}/reactivate")]
    public Task<CardDetails> ReactivateCard(Guid cardId, CancellationToken cancellationToken) => service.ReactivateCardAsync(cardId, cancellationToken);
    [HttpGet("cards/{cardNumber}")]
    public Task<CardDetails> Find(string cardNumber, CancellationToken cancellationToken) => service.FindAsync(cardNumber, cancellationToken);

    [HttpGet("students/{studentId:guid}/cards")]
    public Task<IReadOnlyList<CardDetails>> History(Guid studentId, CancellationToken cancellationToken) => service.GetHistoryAsync(studentId, cancellationToken);

    [HttpPost("students/{studentId:guid}/cards")]
    public Task<CardDetails> Assign(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken) => service.AssignAsync(studentId, request, cancellationToken);

    /// <summary>Aktif kartin ON yuzundeki baski numarasini gunceller; kart degismez.</summary>
    [HttpPut("students/{studentId:guid}/cards/printed-number")]
    public Task<CardDetails> SetPrintedNumber(Guid studentId, SetPrintedNumberRequest request, CancellationToken cancellationToken) => service.SetPrintedNumberAsync(studentId, request, cancellationToken);

    /// <summary>
    /// Kart degisimi; istenirse KART UCRETI de ayni islemde kasaya yazilir. Ucret tahsilati
    /// AYRICA cash.write ister: kart yetkisi olan herkes kasaya gelir yazamamalidir.
    /// </summary>
    [HttpPost("students/{studentId:guid}/cards/replace")]
    public async Task<ActionResult<ReplaceCardResult>> Replace(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChargeFee && !User.HasClaim(Permissions.ClaimType, Permissions.CashWrite))
            throw new RequestValidationException("Kart ücreti tahsil etmek için kasa yazma yetkisi (cash.write) gerekiyor.");
        return Ok(await service.ReplaceWithFeeAsync(studentId, request, ActorId(), cancellationToken));
    }

    private Guid ActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new RequestValidationException("Operatör kimliği bulunamadı.");
    }

    /// <summary>Ogrencinin en son pasife dusen kartini yeniden aktif eder; aktif karti varsa 409.</summary>
    [HttpPost("students/{studentId:guid}/cards/reactivate")]
    public Task<CardDetails> Reactivate(Guid studentId, CancellationToken cancellationToken) => service.ReactivateAsync(studentId, cancellationToken);

    [HttpDelete("cards/{cardId:guid}")]
    public async Task<IActionResult> Deactivate(Guid cardId, [FromQuery] string reason, CancellationToken cancellationToken)
    {
        await service.DeactivateAsync(cardId, reason, cancellationToken); return NoContent();
    }
}
