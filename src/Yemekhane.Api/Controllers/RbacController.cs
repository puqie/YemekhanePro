using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Authorization.Permissions.UsersManage)]
[Route("api/admin")]
public sealed class RbacController(RbacService service) : ControllerBase
{
    [HttpGet("permissions")]
    public Task<List<PermissionDefinition>> ListPermissions(CancellationToken cancellationToken) => service.ListPermissionsAsync(cancellationToken);

    [HttpGet("roles")]
    public Task<IReadOnlyList<RoleDetails>> Roles(CancellationToken cancellationToken) => service.ListRolesAsync(cancellationToken);

    [HttpPost("roles")]
    public async Task<ActionResult<RoleDetails>> CreateRole(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await service.CreateRoleAsync(request, ActorId(), cancellationToken);
        return Created($"/api/admin/roles/{role.Id}", role);
    }

    [HttpPut("roles/{roleId:guid}/permissions")]
    public async Task<IActionResult> ReplacePermissions(Guid roleId, ReplaceRolePermissionsRequest request, CancellationToken cancellationToken)
    {
        await service.ReplaceRolePermissionsAsync(roleId, request, ActorId(), cancellationToken);
        return NoContent();
    }

    [HttpPut("roles/{roleId:guid}")]
    public Task<RoleDetails> UpdateRole(Guid roleId, UpdateRoleRequest request, CancellationToken cancellationToken) =>
        service.UpdateRoleAsync(roleId, request, ActorId(), cancellationToken);

    [HttpDelete("roles/{roleId:guid}")]
    public async Task<IActionResult> DeleteRole(Guid roleId, CancellationToken cancellationToken)
    {
        await service.DeleteRoleAsync(roleId, ActorId(), cancellationToken);
        return NoContent();
    }

    [HttpGet("users")]
    public Task<IReadOnlyList<UserAccessDetails>> Users(CancellationToken cancellationToken) => service.ListUsersAsync(cancellationToken);

    [HttpPost("users")]
    public async Task<ActionResult<UserAccessDetails>> CreateUser(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await service.CreateUserAsync(request, ActorId(), cancellationToken);
        return Created($"/api/admin/users/{user.Id}", user);
    }

    [HttpPut("users/{userId:guid}/roles")]
    public async Task<IActionResult> ReplaceRoles(Guid userId, ReplaceUserRolesRequest request, CancellationToken cancellationToken)
    {
        await service.ReplaceUserRolesAsync(userId, request, ActorId(), cancellationToken);
        return NoContent();
    }

    [HttpPut("users/{userId:guid}/active")]
    public async Task<IActionResult> SetActive(Guid userId, SetUserActiveRequest request, CancellationToken cancellationToken)
    {
        await service.SetUserActiveAsync(userId, request.IsActive, ActorId(), cancellationToken);
        return NoContent();
    }

    private Guid ActorId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new RequestValidationException("Kimliği doğrulanmış kullanıcı bilgisi geçersiz.");
}
