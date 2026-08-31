using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Parents;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class ParentsController(ParentService service) : ControllerBase
{
    [HttpGet("students/{studentId:guid}/parents")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public async Task<IReadOnlyList<ParentDetails>> List(Guid studentId, CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(studentId, cancellationToken);
        return User.HasClaim(Permissions.ClaimType, Permissions.StudentsSensitiveRead) ? result
            : result.Select(x => x with { Phone = StudentSensitiveMasker.MaskPhone(x.Phone)! }).ToArray();
    }

    [HttpPost("students/{studentId:guid}/parents")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<ParentDetails> Create(Guid studentId, SaveParentRequest request, CancellationToken cancellationToken) =>
        Mask(await service.CreateAsync(studentId, request, cancellationToken));

    [HttpPut("parents/{parentId:guid}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<ParentDetails> Update(Guid parentId, SaveParentRequest request, CancellationToken cancellationToken) =>
        Mask(await service.UpdateAsync(parentId, request, cancellationToken));

    [HttpDelete("parents/{parentId:guid}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<IActionResult> Deactivate(Guid parentId, CancellationToken cancellationToken)
    {
        await service.DeactivateAsync(parentId, cancellationToken); return NoContent();
    }

    private ParentDetails Mask(ParentDetails value) =>
        User.HasClaim(Permissions.ClaimType, Permissions.StudentsSensitiveRead)
            ? value
            : value with { Phone = StudentSensitiveMasker.MaskPhone(value.Phone)! };
}
