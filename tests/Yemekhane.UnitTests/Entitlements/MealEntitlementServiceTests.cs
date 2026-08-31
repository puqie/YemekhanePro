using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

public sealed class MealEntitlementServiceTests
{
    [Fact]
    public async Task BulkGrantSkipsWeekendAndConsumptionIsAtomic()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "6811", FirstName = "Ayşe", LastName = "Yılmaz" };
        var meal = new MealType { Name = "Öğle" }; context.AddRange(student, meal); await context.SaveChangesAsync();
        var service = new MealEntitlementService(new EfMealEntitlementRepository(context),
            new BusinessDayService(new FixedClosureProvider(), new WeekendPolicy()));

        var result = await service.GrantBulkAsync(new BulkEntitlementRequest([student.Id], meal.Id,
            new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 14)));
        var rights = await service.ListAsync(student.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var firstUse = await service.TryConsumeAsync(rights[0].Id);
        var secondUse = await service.TryConsumeAsync(rights[0].Id);

        Assert.Equal(2, result.DayCount);
        Assert.Equal(2, result.CreatedCount);
        Assert.True(firstUse);
        Assert.False(secondUse);
    }

    [Fact]
    public async Task BulkGrantSkipsHolidaysFromCalendar()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "6812", FirstName = "Mehmet", LastName = "Demir" };
        var meal = new MealType { Name = "Öğle" }; context.AddRange(student, meal); await context.SaveChangesAsync();
        var holiday = new DateOnly(2026, 9, 15);
        var service = new MealEntitlementService(new EfMealEntitlementRepository(context),
            new BusinessDayService(new FixedClosureProvider(holiday), new WeekendPolicy()));

        var result = await service.GrantBulkAsync(new BulkEntitlementRequest([student.Id], meal.Id,
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16)));
        var rights = await service.ListAsync(student.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        Assert.Equal(2, result.DayCount);
        Assert.DoesNotContain(rights, x => x.Date == holiday);
    }

    [Fact]
    public async Task BulkGrantRejectsRangeLongerThan366Days()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var service = new MealEntitlementService(new EfMealEntitlementRepository(context),
            new BusinessDayService(new FixedClosureProvider(), new WeekendPolicy()));
        var startsOn = new DateOnly(2026, 1, 1);

        var error = await Assert.ThrowsAsync<RequestValidationException>(() => service.GrantBulkAsync(
            new BulkEntitlementRequest([Guid.NewGuid()], Guid.NewGuid(), startsOn, startsOn.AddDays(366),
                IncludeSaturday: true, IncludeSunday: true)));

        Assert.Contains("366", error.Message);
    }

    [Fact]
    public async Task SearchFiltersJoinedFieldsAndReturnsSummaryBeforePaging()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var schoolClass = new SchoolClass { Name = "5A" };
        var meal = new MealType { Name = "Öğle" };
        var ada = new Student { StudentNo = "100", FirstName = "Ada", LastName = "Yılmaz", ClassId = schoolClass.Id };
        var ali = new Student { StudentNo = "101", FirstName = "Ali", LastName = "Demir", ClassId = schoolClass.Id };
        context.AddRange(schoolClass, meal, ada, ali,
            new StudentCard { StudentId = ada.Id, CardNumber = "CARD-100", IsActive = true },
            new MealEntitlement { StudentId = ada.Id, MealTypeId = meal.Id, EntitlementDate = new DateOnly(2026, 9, 1), Quantity = 3, ConsumedQuantity = 1, Status = "Active" },
            new MealEntitlement { StudentId = ada.Id, MealTypeId = meal.Id, EntitlementDate = new DateOnly(2026, 9, 2), Quantity = 2, Status = "Active" },
            new MealEntitlement { StudentId = ali.Id, MealTypeId = meal.Id, EntitlementDate = new DateOnly(2026, 9, 1), Quantity = 9, Status = "Cancelled" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var page = await service.SearchAsync(new MealEntitlementQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            CardNumber: "CARD-100", Name: "Ada", ClassName: "5A", MealTypeId: meal.Id, Status: "Active", PageSize: 1));

        Assert.Single(page.Items);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new MealEntitlementSummary(5, 1, 4), page.Summary);
        Assert.Equal("CARD-100", page.Items[0].CardNumber);
    }

    [Fact]
    public async Task TargetsResolveAndPreviewApplyRejectsChangedData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var schoolClass = new SchoolClass { Name = "6B" }; var group = new StudentGroup { Name = "Sporcular", GroupType = "Manual" };
        var meal = new MealType { Name = "Akşam" };
        var student = new Student { StudentNo = "200", FirstName = "Ece", LastName = "Kaya", ClassId = schoolClass.Id };
        context.AddRange(schoolClass, group, meal, student, new StudentGroupMember { GroupId = group.Id, StudentId = student.Id });
        await context.SaveChangesAsync();
        var service = CreateService(context);
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Group", GroupId: group.Id), meal.Id,
            new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 6), IncludeSaturday: true, IncludeSunday: true);

        var preview = await service.PreviewAsync(grant);
        Assert.Equal(1, preview.StudentCount); Assert.Equal(2, preview.DayCount); Assert.Equal(2, preview.RightsCount);
        context.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = new DateOnly(2026, 9, 5), Quantity = 1, Status = "Active" });
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<EntityConflictException>(() => service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken)));

        var gradeIds = await new EfMealEntitlementRepository(context).ResolveTargetAsync(new EntitlementTarget("Grade", Grade: "6"), default);
        Assert.Equal([student.Id], gradeIds);
    }

    [Fact]
    public async Task ApplyAndBulkCancelAreAtomicAndConsumedRightsCannotBeCancelled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "300", FirstName = "Can", LastName = "Ak" };
        var meal = new MealType { Name = "Kahvaltı" }; context.AddRange(student, meal); await context.SaveChangesAsync();
        var service = CreateService(context);
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [student.Id]), meal.Id,
            new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 8));
        var preview = await service.PreviewAsync(grant);
        var applied = await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
        Assert.Equal(2, applied.CreatedCount);
        var rights = await service.ListAsync(student.Id, grant.StartsOn, grant.EndsOn);
        Assert.True(await service.TryConsumeAsync(rights[0].Id));
        await Assert.ThrowsAsync<EntityConflictException>(() => service.CancelBulkAsync(new CancelEntitlementsRequest(rights.Select(x => x.Id).ToArray(), 2)));
        Assert.All(await service.ListAsync(student.Id, grant.StartsOn, grant.EndsOn), x => Assert.Equal("Active", x.Status));
        var cancelled = await service.CancelBulkAsync(new CancelEntitlementsRequest([rights[1].Id], 1));
        Assert.Equal(1, cancelled.CancelledCount);
    }

    private sealed class FixedClosureProvider(params DateOnly[] closed) : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(closed.Contains(calendarDate));
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
    private static MealEntitlementService CreateService(YemekhaneDbContext context) => new(new EfMealEntitlementRepository(context),
        new BusinessDayService(new FixedClosureProvider(), new WeekendPolicy()));
}
