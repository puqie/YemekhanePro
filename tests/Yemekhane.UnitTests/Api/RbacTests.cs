using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Yemekhane.Api.Authentication;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Api;

public sealed class RbacTests : IClassFixture<YemekhaneApiFactory>
{
    private readonly YemekhaneApiFactory factory;

    public RbacTests(YemekhaneApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task BuiltInRolesHaveExpectedAccessMatrixAndSeedIsIdempotent()
    {
        _ = factory.Server;
        await using var scope = factory.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<RbacSeeder>();
        await seeder.SeedAsync();
        await seeder.SeedAsync();
        var roles = await scope.ServiceProvider.GetRequiredService<RbacService>().ListRolesAsync(default);

        Assert.Equal(4, roles.Count(x => x.IsBuiltIn));
        Assert.Equal(Permissions.All.Order(), roles.Single(x => x.Name == BuiltInRoles.Admin).Permissions.Order());
        Assert.DoesNotContain(Permissions.UsersManage, roles.Single(x => x.Name == BuiltInRoles.Manager).Permissions);
        Assert.Contains(Permissions.EntitlementsBulk, roles.Single(x => x.Name == BuiltInRoles.Operator).Permissions);
        Assert.DoesNotContain(Permissions.UsersManage, roles.Single(x => x.Name == BuiltInRoles.Operator).Permissions);
        Assert.Equal(
            new[] { Permissions.AccessRead, Permissions.CashRead, Permissions.DashboardRead, Permissions.ReportsExport, Permissions.ReportsRead, Permissions.StudentsRead }.Order(),
            roles.Single(x => x.Name == BuiltInRoles.ReportUser).Permissions.Order());
    }

    [Fact]
    public async Task PermissionPoliciesReturnUnauthorizedThenForbiddenAndAllowMatchingPermission()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/students")).StatusCode);

        using var reportOnly = await CreateClientAsync(Permissions.ReportsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reportOnly.GetAsync("/api/students")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reportOnly.GetAsync("/api/reports/DailyAccess")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reportOnly.GetAsync("/api/reports/DailyAccess/csv")).StatusCode);
        using var reportExporter = await CreateClientAsync(Permissions.ReportsExport);
        using var csv = await reportExporter.GetAsync("/api/reports/DailyAccess/csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);

        using var adminReader = await CreateClientAsync(Permissions.UsersManage);
        Assert.Equal(HttpStatusCode.OK, (await adminReader.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reportOnly.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reportOnly.GetAsync("/api/audit-logs")).StatusCode);
        using var auditReader = await CreateClientAsync(Permissions.AuditRead);
        Assert.Equal(HttpStatusCode.OK, (await auditReader.GetAsync("/api/audit-logs?page=1&pageSize=10")).StatusCode);
    }

    [Fact]
    public async Task DynamicRoleAssignmentChangesLoginClaimsAndIsAudited()
    {
        var username = $"dynamic-{Guid.NewGuid():N}";
        const string password = "Strong dynamic password!";
        var user = await CreateUserWithoutRoleAsync(username, password);
        Guid roleId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RbacService>();
            var role = await service.CreateRoleAsync(
                new CreateRoleRequest($"Custom-{Guid.NewGuid():N}", [Permissions.ReportsRead]), user.Id, default);
            roleId = role.Id;
            await service.ReplaceUserRolesAsync(user.Id, new ReplaceUserRolesRequest([role.Id]), user.Id, default);
        }

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        var result = await login.Content.ReadFromJsonAsync<LoginResponse>();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result!.AccessToken);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(jwt.Claims, x => x.Type == ClaimTypes.Role && x.Value.StartsWith("Custom-", StringComparison.Ordinal));
        Assert.Contains(jwt.Claims, x => x.Type == Permissions.ClaimType && x.Value == Permissions.ReportsRead);
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/DailyAccess")).StatusCode);
        await using (var mutation = factory.Services.CreateAsyncScope())
            await mutation.ServiceProvider.GetRequiredService<RbacService>().ReplaceRolePermissionsAsync(
                roleId, new ReplaceRolePermissionsRequest([Permissions.StudentsRead]), user.Id, default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reports/DailyAccess")).StatusCode);

        await using var verification = factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        Assert.True(await db.AuditLogs().AnyAsync(x => x.EntityId == roleId.ToString() && x.Action == "RoleCreated"));
        Assert.True(await db.AuditLogs().AnyAsync(x => x.EntityId == user.Id.ToString() && x.Action == "UserRolesReplaced"));
    }

    [Fact]
    public async Task RoleNamesAreCaseInsensitiveUniqueAndFailedAssignmentIsAtomic()
    {
        var user = await CreateUserWithoutRoleAsync($"atomic-{Guid.NewGuid():N}", "Strong atomic password!");
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RbacService>();
        var name = $"Unique-{Guid.NewGuid():N}";
        var role = await service.CreateRoleAsync(new CreateRoleRequest(name, [Permissions.StudentsRead]), user.Id, default);

        await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.CreateRoleAsync(new CreateRoleRequest(name.ToLowerInvariant(), []), user.Id, default));
        await service.ReplaceUserRolesAsync(user.Id, new ReplaceUserRolesRequest([role.Id]), user.Id, default);
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.ReplaceUserRolesAsync(user.Id, new ReplaceUserRolesRequest([Guid.NewGuid()]), user.Id, default));

        var assigned = await scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>().UserRoles
            .Where(x => x.UserId == user.Id).Select(x => x.RoleId).ToListAsync();
        Assert.Equal([role.Id], assigned);
    }

    [Fact]
    public async Task LastActiveAdminCannotLoseRoleOrBeDeactivated()
    {
        var first = await CreateUserWithoutRoleAsync($"admin-{Guid.NewGuid():N}", "Strong admin password!");
        var second = await CreateUserWithoutRoleAsync($"admin-{Guid.NewGuid():N}", "Strong admin password!");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<RbacService>();
        var adminId = await db.Roles.Where(x => x.NormalizedName == "ADMIN").Select(x => x.Id).SingleAsync();
        await service.ReplaceUserRolesAsync(first.Id, new ReplaceUserRolesRequest([adminId]), first.Id, default);
        await service.ReplaceUserRolesAsync(second.Id, new ReplaceUserRolesRequest([adminId]), first.Id, default);
        await service.ReplaceUserRolesAsync(first.Id, new ReplaceUserRolesRequest([]), second.Id, default);

        await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.ReplaceUserRolesAsync(second.Id, new ReplaceUserRolesRequest([]), second.Id, default));
        await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.SetUserActiveAsync(second.Id, false, second.Id, default));
    }

    private async Task<HttpClient> CreateClientAsync(params string[] permissions)
    {
        var user = await CreateUserWithoutRoleAsync($"claims-{Guid.NewGuid():N}", "Strong claims password!");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username),
            new("security_stamp", user.SecurityStamp)
        };
        claims.AddRange(permissions.Select(x => new Claim(Permissions.ClaimType, x)));
        var token = new JwtSecurityToken("yemekhane-test", "yemekhane-test", claims,
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(YemekhaneApiFactory.SigningKey)), SecurityAlgorithms.HmacSha256));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private async Task<User> CreateUserWithoutRoleAsync(string username, string password)
    {
        _ = factory.Server;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User { Id = Guid.NewGuid(), Username = username, NormalizedUsername = LoginService.NormalizeUsername(username), PasswordHash = string.Empty,
            SecurityStamp = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
}

internal static class RbacTestDbExtensions
{
    public static DbSet<AuditLog> AuditLogs(this YemekhaneDbContext context) => context.Set<AuditLog>();
}
