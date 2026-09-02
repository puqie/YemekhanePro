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
    /// <summary>
    /// Tanimlar: /api/organization/{classes|sections|departments|jobs}/lookups liste,
    /// POST {kind}, PUT/DELETE {kind}/{id}. Eski programdaki dort "Tanim" ekraninin
    /// karsiligi; masaustu Tanimlar ekrani ve ogrenci formundaki "+" hizli ekleme kullanir.
    /// </summary>
    [HttpGet("{kind:regex(^(classes|sections|departments|jobs)$)}/lookups")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<IReadOnlyList<LookupRecord>> Lookups(string kind, CancellationToken cancellationToken) =>
        service.ListLookupsAsync(OrganizationService.ParseKind(kind), cancellationToken);
    [HttpPost("{kind:regex(^(sections|departments|jobs)$)}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<LookupRecord> CreateLookup(string kind, SaveLookupRequest request, CancellationToken cancellationToken) =>
        service.CreateLookupAsync(OrganizationService.ParseKind(kind), request.Name, cancellationToken);
    [HttpPut("{kind:regex(^(classes|sections|departments|jobs)$)}/{id:guid}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<LookupRecord> RenameLookup(string kind, Guid id, SaveLookupRequest request, CancellationToken cancellationToken) =>
        service.RenameLookupAsync(OrganizationService.ParseKind(kind), id, request.Name, cancellationToken);
    [HttpDelete("{kind:regex(^(classes|sections|departments|jobs)$)}/{id:guid}")]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public async Task<IActionResult> DeleteLookup(string kind, Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteLookupAsync(OrganizationService.ParseKind(kind), id, cancellationToken); return NoContent();
    }
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
