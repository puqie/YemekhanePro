using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Api.Authentication;
using Yemekhane.Application.Audit;

namespace Yemekhane.Api.Authorization;

public sealed record RoleDetails(Guid Id, string Name, bool IsBuiltIn, IReadOnlyList<string> Permissions);
public sealed record UserAccessDetails(Guid Id, string Username, bool IsActive, IReadOnlyList<RoleSummary> Roles);
public sealed record RoleSummary(Guid Id, string Name);
public sealed record CreateRoleRequest(string Name, IReadOnlyCollection<string>? Permissions);
public sealed record UpdateRoleRequest(string Name);
public sealed record ReplaceRolePermissionsRequest(IReadOnlyCollection<string>? Permissions);
public sealed record ReplaceUserRolesRequest(IReadOnlyCollection<Guid>? RoleIds);
public sealed record CreateUserRequest(string Username, string Password, IReadOnlyCollection<Guid>? RoleIds);
public sealed record SetUserActiveRequest(bool IsActive);

public sealed class RbacService(
    YemekhaneDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider,
    IAuditService auditService)
{
    public async Task<IReadOnlyList<RoleDetails>> ListRolesAsync(CancellationToken cancellationToken) =>
        await dbContext.Roles.AsNoTracking().OrderBy(x => x.Name)
            .Select(role => new RoleDetails(role.Id, role.Name, role.IsBuiltIn,
                (from assignment in dbContext.RolePermissions
                 join permission in dbContext.Permissions on assignment.PermissionId equals permission.Id
                 where assignment.RoleId == role.Id orderby permission.Code select permission.Code).ToList()))
            .ToListAsync(cancellationToken);

    public Task<List<PermissionDefinition>> ListPermissionsAsync(CancellationToken cancellationToken) =>
        dbContext.Permissions.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<UserAccessDetails>> ListUsersAsync(CancellationToken cancellationToken) =>
        await dbContext.Users.AsNoTracking().OrderBy(x => x.Username)
            .Select(user => new UserAccessDetails(user.Id, user.Username, user.IsActive,
                (from userRole in dbContext.UserRoles join role in dbContext.Roles on userRole.RoleId equals role.Id
                 where userRole.UserId == user.Id orderby role.Name select new RoleSummary(role.Id, role.Name)).ToList()))
            .ToListAsync(cancellationToken);

    public async Task<RoleDetails> CreateRoleAsync(CreateRoleRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var name = ValidateRoleName(request.Name);
        var normalized = NormalizeRoleName(name);
        if (await dbContext.Roles.AnyAsync(x => x.NormalizedName == normalized, cancellationToken))
            throw new EntityConflictException("Bu rol adı zaten kullanılıyor.");
        var permissionIds = await ResolvePermissionIdsAsync(request.Permissions, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var role = new Role { Id = Guid.NewGuid(), Name = name, NormalizedName = normalized, IsBuiltIn = false, CreatedAt = now };
        dbContext.Roles.Add(role);
        dbContext.RolePermissions.AddRange(permissionIds.Select(x => new RolePermissionAssignment { RoleId = role.Id, PermissionId = x.Value }));
        AddAudit(actorId, "RoleCreated", "Role", role.Id, null, new { role.Name, Permissions = permissionIds.Keys });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new RoleDetails(role.Id, role.Name, false, permissionIds.Keys.Order().ToArray());
    }

    public async Task ReplaceRolePermissionsAsync(Guid roleId, ReplaceRolePermissionsRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.SingleOrDefaultAsync(x => x.Id == roleId, cancellationToken)
            ?? throw new EntityNotFoundException("Rol bulunamadı.");
        if (role.IsBuiltIn)
            throw new EntityConflictException("Yerleşik rol izinleri değiştirilemez.");
        var permissionIds = await ResolvePermissionIdsAsync(request.Permissions, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var current = await dbContext.RolePermissions.Where(x => x.RoleId == roleId).ToListAsync(cancellationToken);
        var before = await PermissionCodesAsync(current.Select(x => x.PermissionId), cancellationToken);
        dbContext.RolePermissions.RemoveRange(current);
        dbContext.RolePermissions.AddRange(permissionIds.Values.Select(id => new RolePermissionAssignment { RoleId = roleId, PermissionId = id }));
        await TouchUsersInRoleAsync(roleId, cancellationToken);
        AddAudit(actorId, "RolePermissionsReplaced", "Role", roleId, before, permissionIds.Keys);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RoleDetails> UpdateRoleAsync(Guid roleId, UpdateRoleRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.SingleOrDefaultAsync(x => x.Id == roleId, cancellationToken)
            ?? throw new EntityNotFoundException("Rol bulunamadı.");
        if (role.IsBuiltIn) throw new EntityConflictException("Yerleşik roller yeniden adlandırılamaz.");
        var name = ValidateRoleName(request.Name);
        var normalized = NormalizeRoleName(name);
        if (await dbContext.Roles.AnyAsync(x => x.Id != roleId && x.NormalizedName == normalized, cancellationToken))
            throw new EntityConflictException("Bu rol adı zaten kullanılıyor.");
        var before = role.Name;
        role.Name = name;
        role.NormalizedName = normalized;
        role.UpdatedAt = timeProvider.GetUtcNow();
        await TouchUsersInRoleAsync(roleId, cancellationToken);
        AddAudit(actorId, "RoleRenamed", "Role", roleId, before, name);
        await dbContext.SaveChangesAsync(cancellationToken);
        var permissions = await (from assignment in dbContext.RolePermissions join permission in dbContext.Permissions
                                 on assignment.PermissionId equals permission.Id where assignment.RoleId == roleId
                                 orderby permission.Code select permission.Code).ToListAsync(cancellationToken);
        return new RoleDetails(role.Id, role.Name, false, permissions);
    }

    public async Task DeleteRoleAsync(Guid roleId, Guid actorId, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.SingleOrDefaultAsync(x => x.Id == roleId, cancellationToken)
            ?? throw new EntityNotFoundException("Rol bulunamadı.");
        if (role.IsBuiltIn) throw new EntityConflictException("Yerleşik roller silinemez.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await TouchUsersInRoleAsync(roleId, cancellationToken);
        dbContext.Roles.Remove(role);
        AddAudit(actorId, "RoleDeleted", "Role", roleId, new { role.Name }, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceUserRolesAsync(Guid userId, ReplaceUserRolesRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new EntityNotFoundException("Kullanıcı bulunamadı.");
        var roleIds = (request.RoleIds ?? []).Distinct().ToArray();
        if (await dbContext.Roles.CountAsync(x => roleIds.Contains(x.Id), cancellationToken) != roleIds.Length)
            throw new RequestValidationException("Bir veya daha fazla rol geçersiz.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var current = await dbContext.UserRoles.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var adminId = await AdminRoleIdAsync(cancellationToken);
        if (user.IsActive && current.Any(x => x.RoleId == adminId) && !roleIds.Contains(adminId))
            await EnsureAnotherActiveAdminAsync(userId, adminId, cancellationToken);
        dbContext.UserRoles.RemoveRange(current);
        dbContext.UserRoles.AddRange(roleIds.Select(roleId => new UserRole { UserId = userId, RoleId = roleId }));
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit(actorId, "UserRolesReplaced", "User", userId, current.Select(x => x.RoleId), roleIds);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<UserAccessDetails> CreateUserAsync(CreateUserRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        if (username.Length is < 1 or > 128 || request.Password is null || request.Password.Length < 12)
            throw new RequestValidationException("Kullanıcı adı ve en az 12 karakterli parola gereklidir.");
        var normalized = LoginService.NormalizeUsername(username);
        if (await dbContext.Users.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
            throw new EntityConflictException("Bu kullanıcı adı zaten kullanılıyor.");
        var roleIds = (request.RoleIds ?? []).Distinct().ToArray();
        var roles = await dbContext.Roles.Where(x => roleIds.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        if (roles.Count != roleIds.Length)
            throw new RequestValidationException("Bir veya daha fazla rol geçersiz.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var user = new User { Id = Guid.NewGuid(), Username = username, NormalizedUsername = normalized, PasswordHash = string.Empty, SecurityStamp = Guid.NewGuid().ToString("N"), CreatedAt = timeProvider.GetUtcNow() };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        dbContext.Users.Add(user);
        dbContext.UserRoles.AddRange(roleIds.Select(roleId => new UserRole { UserId = user.Id, RoleId = roleId }));
        AddAudit(actorId, "UserCreated", "User", user.Id, null, new { user.Username, RoleIds = roleIds });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserAccessDetails(user.Id, user.Username, true, roles.Select(x => new RoleSummary(x.Id, x.Name)).ToArray());
    }

    public async Task SetUserActiveAsync(Guid userId, bool isActive, Guid actorId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new EntityNotFoundException("Kullanıcı bulunamadı.");
        if (user.IsActive && !isActive)
        {
            var adminId = await AdminRoleIdAsync(cancellationToken);
            if (await dbContext.UserRoles.AnyAsync(x => x.UserId == userId && x.RoleId == adminId, cancellationToken))
                await EnsureAnotherActiveAdminAsync(userId, adminId, cancellationToken);
        }
        var before = user.IsActive;
        user.IsActive = isActive;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit(actorId, "UserActiveChanged", "User", userId, before, isActive);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, Guid>> ResolvePermissionIdsAsync(IEnumerable<string>? requested, CancellationToken cancellationToken)
    {
        var codes = (requested ?? []).Select(x => x?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (codes.Any(x => !Permissions.All.Contains(x, StringComparer.Ordinal)))
            throw new RequestValidationException("Bir veya daha fazla izin kodu geçersiz.");
        var values = await dbContext.Permissions.Where(x => codes.Contains(x.Code)).ToDictionaryAsync(x => x.Code, x => x.Id, cancellationToken);
        if (values.Count != codes.Length)
            throw new RequestValidationException("Bir veya daha fazla izin tanımlı değil.");
        return values;
    }

    private async Task EnsureAnotherActiveAdminAsync(Guid excludedUserId, Guid adminRoleId, CancellationToken cancellationToken)
    {
        var exists = await (from user in dbContext.Users join userRole in dbContext.UserRoles on user.Id equals userRole.UserId
                            where user.Id != excludedUserId && user.IsActive && userRole.RoleId == adminRoleId select user.Id)
            .AnyAsync(cancellationToken);
        if (!exists)
            throw new EntityConflictException("Son aktif Admin kaldırılamaz veya devre dışı bırakılamaz.");
    }

    private Task<Guid> AdminRoleIdAsync(CancellationToken cancellationToken) => dbContext.Roles
        .Where(x => x.NormalizedName == "ADMIN").Select(x => x.Id).SingleAsync(cancellationToken);

    private async Task TouchUsersInRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var users = await (from user in dbContext.Users join userRole in dbContext.UserRoles on user.Id equals userRole.UserId
                           where userRole.RoleId == roleId select user).ToListAsync(cancellationToken);
        foreach (var user in users) user.SecurityStamp = Guid.NewGuid().ToString("N");
    }

    private async Task<string[]> PermissionCodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        await dbContext.Permissions.Where(x => ids.Contains(x.Id)).Select(x => x.Code).OrderBy(x => x).ToArrayAsync(cancellationToken);

    private void AddAudit(Guid actorId, string action, string entityName, Guid entityId, object? before, object? after) =>
        auditService.Record(new AuditEntry(action, entityName, entityId.ToString(), TurkishDescription(action), Before: before, After: after, UserId: actorId));

    private static string TurkishDescription(string action) => action switch
    {
        "RoleCreated" => "Rol oluşturuldu.",
        "RolePermissionsReplaced" => "Rol izinleri değiştirildi.",
        "RoleRenamed" => "Rol yeniden adlandırıldı.",
        "RoleDeleted" => "Rol silindi.",
        "UserRolesReplaced" => "Kullanıcı rolleri değiştirildi.",
        "UserCreated" => "Kullanıcı oluşturuldu.",
        "UserActiveChanged" => "Kullanıcı aktiflik durumu değiştirildi.",
        _ => "Yetkilendirme kaydı değiştirildi."
    };

    private static string ValidateRoleName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100) throw new RequestValidationException("Rol adı 1-100 karakter olmalıdır.");
        return name;
    }

    private static string NormalizeRoleName(string name) => name.ToUpperInvariant();
}
