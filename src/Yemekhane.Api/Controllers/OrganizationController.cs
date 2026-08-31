using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Organization;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/organization")]
public sealed class OrganizationController(OrganizationService service) : ControllerBase
{
    [HttpGet("classes")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<IReadOnlyList<ClassRecord>> Classes(CancellationToken cancellationToken) => service.ListClassesAsync(cancellationToken);
    [HttpPost("classes")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<ClassRecord> CreateClass([FromBody] string name, CancellationToken cancellationToken) => service.CreateClassAsync(name, cancellationToken);
    [HttpGet("groups")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<IReadOnlyList<GroupRecord>> Groups(CancellationToken cancellationToken) => service.ListGroupsAsync(cancellationToken);
    [HttpPost("groups")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<GroupRecord> CreateGroup(SaveGroupRequest request, CancellationToken cancellationToken) => service.CreateGroupAsync(request, cancellationToken);
    [HttpPut("groups/{groupId:guid}/members")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<IActionResult> ReplaceMembers(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken)
    {
        await service.ReplaceMembersAsync(groupId, studentIds, cancellationToken); return NoContent();
    }
}
