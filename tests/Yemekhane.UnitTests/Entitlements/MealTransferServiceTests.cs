using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

public sealed class MealTransferServiceTests
{
    [Fact]
    public async Task TransferMovesRemainingQuantityAndCreatesHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "1", FirstName = "A", LastName = "B" }; var meal = new MealType { Name = "Öğle" };
        context.AddRange(student, meal); await context.SaveChangesAsync();
        var source = new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = new(2026, 9, 11), Quantity = 2, ConsumedQuantity = 1, Status = "Active" };
        context.Add(source); await context.SaveChangesAsync();
        var businessDays = new BusinessDayService(new NoClosures(), new WeekendPolicy());
        var service = new MealTransferService(new EfMealTransferRepository(context), businessDays);

        var result = await service.TransferAsync(new TransferMealEntitlementsRequest([source.Id], "NextBusinessDay", null, new CalendarScope("AllSchool"), "Tatil", Guid.NewGuid()));

        var target = await context.MealEntitlements.SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, 14));
        Assert.Equal(1, result.TransferredQuantity);
        Assert.Equal(1, target.Quantity);
        Assert.Equal("Transferred", (await context.MealEntitlements.SingleAsync(x => x.Id == source.Id)).Status);
        Assert.Single(await context.Set<MealTransfer>().ToListAsync());
    }

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) => Task.FromResult(false);
    }
    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
