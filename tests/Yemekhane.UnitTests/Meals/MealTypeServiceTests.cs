using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Meals;
using Yemekhane.Infrastructure.Meals;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Meals;

public sealed class MealTypeServiceTests
{
    [Fact]
    public async Task MealTypeCrudUsesPersistentDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var service = new MealTypeService(new EfMealTypeRepository(context));
        var created = await service.CreateAsync(new SaveMealTypeRequest("Öğle Yemeği", new TimeOnly(11, 30), new TimeOnly(14, 0)));
        var updated = await service.UpdateAsync(created.Id, new SaveMealTypeRequest("Öğle", new TimeOnly(12, 0), new TimeOnly(14, 30)));
        await service.DeactivateAsync(created.Id);

        Assert.Equal("Öğle", updated.Name);
        Assert.Empty(await service.ListAsync());
        Assert.Single(await service.ListAsync(true));
    }

    [Fact]
    public async Task InvalidTimeRangeIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection);
        var service = new MealTypeService(new EfMealTypeRepository(context));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.CreateAsync(new SaveMealTypeRequest("Öğle", new TimeOnly(14, 0), new TimeOnly(12, 0))));
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
