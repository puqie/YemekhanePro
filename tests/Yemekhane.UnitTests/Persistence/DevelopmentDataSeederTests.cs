using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Persistence.Seeding;

namespace Yemekhane.UnitTests.Persistence;

public sealed class DevelopmentDataSeederTests
{
    [Fact]
    public async Task SeedCreatesRequiredDatasetAndCanRunTwice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var seeder = new DevelopmentDataSeeder(context);

        await seeder.SeedAsync("Development");
        await seeder.SeedAsync("Development");

        Assert.Equal(1_000, await context.Students.CountAsync());
        Assert.Equal(1_000, await context.StudentCards.CountAsync());
        Assert.Equal(10, await context.Set<SchoolClass>().CountAsync());
        Assert.Equal(3, await context.Devices.CountAsync());
        Assert.Equal(4, await context.Set<MealType>().CountAsync());
        Assert.Equal(10_000, await context.AccessLogs.CountAsync());
    }

    [Fact]
    public async Task SeedRejectsProductionEnvironment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DevelopmentDataSeeder(context).SeedAsync("Production"));

        Assert.Contains("Development", exception.Message, StringComparison.Ordinal);
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
