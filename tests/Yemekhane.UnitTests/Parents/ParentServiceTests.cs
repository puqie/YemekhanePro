using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Parents;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Parents;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Parents;

public sealed class ParentServiceTests
{
    [Fact]
    public async Task PhoneIsNormalizedAndOnlyOneParentIsPrimary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "6811", FirstName = "Ayşe", LastName = "Yılmaz" };
        context.Add(student); await context.SaveChangesAsync();
        var service = new ParentService(new EfParentRepository(context));

        var first = await service.CreateAsync(student.Id, new SaveParentRequest("Anne", "0532 777 63 21", IsPrimary: true));
        var second = await service.CreateAsync(student.Id, new SaveParentRequest("Baba", "+90 533 111 22 33", IsPrimary: true));
        var parents = await service.ListAsync(student.Id);

        Assert.Equal("+905327776321", first.Phone);
        Assert.False(parents.Single(x => x.Id == first.Id).IsPrimary);
        Assert.True(parents.Single(x => x.Id == second.Id).IsPrimary);
    }

    [Fact]
    public async Task InvalidPhoneIsRejectedBeforePersistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        var service = new ParentService(new EfParentRepository(context));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(Guid.NewGuid(), new SaveParentRequest("Veli", "123")));
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
