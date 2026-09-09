using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Common;
using Yemekhane.Application.Tuition;

namespace Yemekhane.Api.Controllers;

/// <summary>
/// Anasinifi ucret planlari: sinif ya da ogrenci bazli toplam/taksit, aylik sabit ve gunluk
/// ucret. Okuma kasa okuma yetkisiyle (veli borcunu kasiyer de gorur), degistirme kasa
/// yonetimi yetkisiyle yapilir.
/// </summary>
[ApiController]
[Route("api/tuition")]
public sealed class TuitionController(TuitionService service) : ControllerBase
{
    [HttpGet("plans")]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<PagedResult<TuitionPlanDetails>> List([FromQuery] string? period, [FromQuery] Guid? classId,
        [FromQuery] Guid? studentId, [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        service.ListAsync(new TuitionPlanFilter(period, classId, studentId, includeInactive, page, pageSize), cancellationToken);

    [HttpGet("plans/{id:guid}")]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<TuitionPlanDetails> Get(Guid id, CancellationToken cancellationToken) =>
        service.GetAsync(id, cancellationToken);

    /// <summary>Ogrencinin gecerli plani: kendi plani yoksa sinifindan devralinir.</summary>
    [HttpGet("students/{studentId:guid}")]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<StudentTuitionSummary> ForStudent(Guid studentId, CancellationToken cancellationToken) =>
        service.ForStudentAsync(studentId, cancellationToken);

    /// <summary>Ayni sinif/ogrenci ve donem icin plan varsa GUNCELLENIR; taksitler yeniden uretilir.</summary>
    [HttpPost("plans")]
    [PermissionAuthorize(Permissions.CashManage)]
    public async Task<ActionResult<TuitionPlanDetails>> Save(SaveTuitionPlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await service.SaveAsync(request, ActorId(), cancellationToken);
        return Created($"/api/tuition/plans/{plan.Id:D}", plan);
    }

    [HttpDelete("plans/{id:guid}")]
    [PermissionAuthorize(Permissions.CashManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, ActorId(), cancellationToken);
        return NoContent();
    }

    /// <summary>Kasadan girilmis bir tahsilati taksite sayar.</summary>
    [HttpPost("payments")]
    [PermissionAuthorize(Permissions.CashWrite)]
    public Task<TuitionInstallmentDetails> ApplyPayment(ApplyTuitionPaymentRequest request, CancellationToken cancellationToken) =>
        service.ApplyPaymentAsync(request, ActorId(), cancellationToken);

    private Guid ActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new RequestValidationException("Operatör kimliği bulunamadı.");
    }
}
