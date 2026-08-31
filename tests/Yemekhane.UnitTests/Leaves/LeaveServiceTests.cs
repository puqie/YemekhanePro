using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Leaves;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Leaves;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Leaves;

public sealed class LeaveServiceTests
{
    [Fact]
    public async Task LeaveTransfersEntitlementToNextBusinessDay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "1", FirstName = "A", LastName = "B" }; var meal = new MealType { Name = "Öğle" };
        context.AddRange(student, meal); await context.SaveChangesAsync();
        context.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = new(2026, 9, 11), Quantity = 1, Status = "Active" });
        await context.SaveChangesAsync();
        var businessDay = new BusinessDayService(new OpenCalendar(), new WeekendPolicy());
        var service = new LeaveService(new EfLeaveRepository(context, businessDay));

        await service.CreateAsync(new CreateLeaveRequest(student.Id, new(2026, 9, 11), new(2026, 9, 11), "İzin", null, "NextBusinessDay", Guid.NewGuid()));

        Assert.True(await service.IsOnLeaveAsync(student.Id, new DateOnly(2026, 9, 11)));
        Assert.Equal("Transferred", (await context.MealEntitlements.SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, 11))).Status);
        Assert.Equal(1, (await context.MealEntitlements.SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, 14))).Quantity);
        Assert.Single(await context.Set<MealTransfer>().ToListAsync());
    }

    private sealed class OpenCalendar : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) => Task.FromResult(false);
    }
    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
