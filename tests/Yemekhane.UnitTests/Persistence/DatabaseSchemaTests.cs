using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Persistence;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public async Task InitialMigrationCreatesVersionedSchemaAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);

        await context.Database.MigrateAsync();
        var firstRun = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        await context.Database.MigrateAsync();

        var secondRun = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var tables = await ReadNames(connection, "table");
        Assert.Equal(firstRun, secondRun);
        Assert.Contains(secondRun, migration => migration.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Contains("__EFMigrationsHistory", tables);
        Assert.Contains("students", tables);
    }

    [Fact]
    public async Task SchemaCreatesWithForeignKeysAndRequiredIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);

        await context.Database.EnsureCreatedAsync();

        var tables = await ReadNames(connection, "table");
        var indexes = await ReadNames(connection, "index");
        Assert.Contains("students", tables);
        Assert.Contains("student_cards", tables);
        Assert.Contains("meal_entitlements", tables);
        Assert.Contains("access_logs", tables);
        Assert.Contains("sync_operations", tables);
        Assert.Contains(indexes, x => x.Contains("card_number", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(indexes, x => x.Contains("student_no", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StudentNumberAndCardNumberAreIndependentUniqueFields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var student = new Student { StudentNo = "6811", FirstName = "Ahmet", LastName = "Yılmaz" };
        context.Students.Add(student);
        context.StudentCards.Add(new StudentCard { StudentId = student.Id, CardNumber = "8222704", ValidFrom = DateTimeOffset.UtcNow });

        await context.SaveChangesAsync();

        Assert.Equal("6811", (await context.Students.SingleAsync()).StudentNo);
        Assert.Equal("8222704", (await context.StudentCards.SingleAsync()).CardNumber);
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);

    private static async Task<HashSet<string>> ReadNames(SqliteConnection connection, string type)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        command.Parameters.AddWithValue("$type", type);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
