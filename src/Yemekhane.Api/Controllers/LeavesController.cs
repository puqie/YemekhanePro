using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Leaves;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/leaves")]
public sealed class LeavesController(LeaveService service) : ControllerBase
{
    [HttpPost]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<LeaveDetails> Create(CreateLeaveRequest request, CancellationToken cancellationToken) => service.CreateAsync(request, cancellationToken);
    [HttpGet("student/{studentId:guid}")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<IReadOnlyList<LeaveDetails>> List(Guid studentId, CancellationToken cancellationToken) => service.ListAsync(studentId, cancellationToken);

    /// <summary>Takvimden secilen ogrencilere ayni tarih araliginda izin ("ogrenciye ozel tatil").</summary>
    [HttpPost("bulk")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<BulkLeaveResult> CreateMany(CreateBulkLeaveRequest request, CancellationToken cancellationToken) =>
        service.CreateManyAsync(request, ActorId(), cancellationToken);

    /// <summary>Tarih araligina degen izinler, ogrenci adiyla (takvim cekmecesi).</summary>
    [HttpGet]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<IReadOnlyList<LeaveListRow>> ListInRange([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken cancellationToken) =>
        service.ListInRangeAsync(from, to, cancellationToken);

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, ActorId(), cancellationToken);
        return NoContent();
    }

    private Guid ActorId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
