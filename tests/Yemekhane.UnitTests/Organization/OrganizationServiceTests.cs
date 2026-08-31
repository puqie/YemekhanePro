using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Organization;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Organization;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Organization;

public sealed class OrganizationServiceTests
{
    [Fact]
    public async Task ManualGroupMembershipIsReplacedAtomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var students = new[]
        {
            new Student { StudentNo = "1", FirstName = "A", LastName = "A" },
            new Student { StudentNo = "2", FirstName = "B", LastName = "B" }
        };
        context.AddRange(students); await context.SaveChangesAsync();
        var service = new OrganizationService(new EfOrganizationRepository(context));
        var group = await service.CreateGroupAsync(new SaveGroupRequest("Gezi Grubu", "Manual"));

        await service.ReplaceMembersAsync(group.Id, [students[0].Id, students[0].Id, students[1].Id]);

        Assert.Equal(2, (await service.ListGroupsAsync()).Single().MemberCount);
    }

    [Fact]
    public async Task CriteriaGroupRequiresValidJsonAndRejectsManualMembers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var service = new OrganizationService(new EfOrganizationRepository(context));

        await Assert.ThrowsAsync<RequestValidationException>(() => service.CreateGroupAsync(new SaveGroupRequest("5. Sınıflar", "Criteria", "invalid")));
        var group = await service.CreateGroupAsync(new SaveGroupRequest("5. Sınıflar", "Criteria", "{\"grade\":5}"));
        await Assert.ThrowsAsync<EntityConflictException>(() => service.ReplaceMembersAsync(group.Id, []));
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
