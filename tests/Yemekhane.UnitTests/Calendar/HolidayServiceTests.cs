using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Infrastructure.Calendar;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Calendar;

public sealed class HolidayServiceTests
{
    [Fact]
    public async Task GlobalAndClassScopedHolidaysAreResolvedCorrectly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var repository = new EfHolidayRepository(context); var service = new HolidayService(repository);
        var class5A = Guid.NewGuid(); var class5B = Guid.NewGuid();
        await service.CreateAsync(new CreateHolidayRequest(new DateOnly(2026, 4, 23), "23 Nisan", "Official", null, "NextBusinessDay", [new("AllSchool")]));
        await service.CreateAsync(new CreateHolidayRequest(new DateOnly(2026, 9, 14), "5A Gezi", "Trip", null, "Forfeit", [new("Class", class5A)]));

        Assert.True(await repository.IsClosedAsync(new DateOnly(2026, 4, 23), new CalendarScope("Class", class5B), default));
        Assert.True(await repository.IsClosedAsync(new DateOnly(2026, 9, 14), new CalendarScope("Class", class5A), default));
        Assert.False(await repository.IsClosedAsync(new DateOnly(2026, 9, 14), new CalendarScope("Class", class5B), default));
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
