using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Devices;

namespace Yemekhane.Api.Controllers;

/// <summary>
/// Kartlarin cihazlara yuklenme durumu. Her kart-cihaz cifti ayri izlenir; boylece
/// bir turnikede eksik kalan kart gorulebilir.
/// </summary>
[ApiController]
[Route("api/device-cards")]
public sealed class DeviceCardsController(IDeviceCardSyncService sync, IDeviceCardListQuery cards) : ControllerBase
{
    /// <summary>Cihaz bazinda yuklenen / bekleyen / hatali kart sayilari.</summary>
    [HttpGet("summary")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<IReadOnlyList<DeviceCardSummary>> Summary(CancellationToken cancellationToken) =>
        sync.GetDeviceSummariesAsync(cancellationToken);

    /// <summary>Tek bir kartin her cihazdaki durumu.</summary>
    [HttpGet("cards/{cardId:guid}")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<IReadOnlyList<DeviceCardStatusRow>> CardStatus(Guid cardId, CancellationToken cancellationToken) =>
        sync.GetCardStatusAsync(cardId, cancellationToken);

    /// <summary>Bir cihazin yuklenmeyi bekleyen kart kuyrugu.</summary>
    [HttpGet("{deviceId:guid}/pending")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<IReadOnlyList<PendingDeviceCard>> Pending(Guid deviceId, [FromQuery] int limit,
        CancellationToken cancellationToken) =>
        sync.GetPendingAsync(deviceId, limit is > 0 and <= 500 ? limit : 100, cancellationToken);

    /// <summary>
    /// Bir cihazdaki kartlarin tamami (yuklu / bekleyen / hatali), arama ve sayfalamayla.
    /// Kaynak sunucunun kart-cihaz durum tablosudur; cihaz bellegi dogrudan okunmaz.
    /// </summary>
    [HttpGet("{deviceId:guid}/cards")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<DeviceCardListResult> Cards(Guid deviceId, [FromQuery] string? search = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        cards.ListAsync(new DeviceCardListQuery(deviceId, search, page, pageSize), cancellationToken);

    /// <summary>Karti tum aktif cihazlara yeniden yuklenmek uzere kuyruga alir.</summary>
    [HttpPost("cards/{cardId:guid}/resync")]
    [PermissionAuthorize(Permissions.DevicesManage)]
    public async Task<IActionResult> Resync(Guid cardId, CancellationToken cancellationToken)
    {
        await sync.QueueCardAsync(cardId, cancellationToken);
        return Accepted();
    }

    /// <summary>Bekleyen kart kuyrugunu zamanlayiciyi beklemeden hemen isler.</summary>
    [HttpPost("push")]
    [PermissionAuthorize(Permissions.DevicesManage)]
    public async Task<IActionResult> PushNow([FromServices] IEnumerable<IHostedService> hostedServices,
        CancellationToken cancellationToken)
    {
        var worker = hostedServices.OfType<DeviceCardPushWorker>().FirstOrDefault();
        if (worker is null) return Problem("Kart yükleme servisi çalışmıyor.", statusCode: StatusCodes.Status503ServiceUnavailable);
        await worker.PushPendingCardsAsync(cancellationToken);
        return Accepted();
    }
}
