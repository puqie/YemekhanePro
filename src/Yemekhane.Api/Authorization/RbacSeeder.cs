using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Api.Authorization;

public static class BuiltInRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Operator = "Operator";
    public const string ReportUser = "ReportUser";
}

public sealed class RbacSeeder(YemekhaneDbContext dbContext, TimeProvider timeProvider)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Matrix =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            [BuiltInRoles.Admin] = Permissions.All,
            [BuiltInRoles.Manager] = Permissions.All.Where(x => x is not Permissions.UsersManage and not Permissions.BackupsManage and not Permissions.SettingsManage).ToArray(),
            [BuiltInRoles.Operator] =
            [
                Permissions.StudentsRead, Permissions.StudentsWrite, Permissions.StudentsDeactivate,
                Permissions.CardsManage, Permissions.EntitlementsManage, Permissions.EntitlementsBulk,
                Permissions.CalendarManage, Permissions.ReportsRead, Permissions.ReportsExport, Permissions.CashRead,
                Permissions.CashWrite, Permissions.CashManage, Permissions.SmsRead, Permissions.SmsSend, Permissions.SmsManage, Permissions.SettingsRead, Permissions.DashboardRead, Permissions.AccessRead, Permissions.NotificationsRead
            ],
            [BuiltInRoles.ReportUser] = [Permissions.StudentsRead, Permissions.ReportsRead, Permissions.ReportsExport, Permissions.CashRead, Permissions.DashboardRead, Permissions.AccessRead]
        };

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var code in Permissions.All)
        {
            var permission = await dbContext.Permissions.SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
            if (permission is null)
                dbContext.Permissions.Add(new PermissionDefinition { Id = Guid.NewGuid(), Code = code, Name = code, CreatedAt = now });
            else
                permission.Name = code;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var entry in Matrix)
        {
            var normalized = entry.Key.ToUpperInvariant();
            var role = await dbContext.Roles.SingleOrDefaultAsync(x => x.NormalizedName == normalized, cancellationToken);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = entry.Key, NormalizedName = normalized, IsBuiltIn = true, CreatedAt = now };
                dbContext.Roles.Add(role);
            }
            else
            {
                role.Name = entry.Key;
                role.IsBuiltIn = true;
            }
            await dbContext.SaveChangesAsync(cancellationToken);

            var permissionIds = await dbContext.Permissions.Where(x => entry.Value.Contains(x.Code)).Select(x => x.Id).ToListAsync(cancellationToken);
            var existing = await dbContext.RolePermissions.Where(x => x.RoleId == role.Id).ToListAsync(cancellationToken);
            dbContext.RolePermissions.RemoveRange(existing.Where(x => !permissionIds.Contains(x.PermissionId)));
            dbContext.RolePermissions.AddRange(permissionIds.Where(id => existing.All(x => x.PermissionId != id))
                .Select(id => new RolePermissionAssignment { RoleId = role.Id, PermissionId = id }));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
