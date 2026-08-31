using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Calendar;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Calendar;

public sealed class CalendarRepositoryTests
{
    [Fact]
    public async Task MonthlyAggregationAppliesClassScopeAcrossOperationalSources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Context(connection); await db.Database.EnsureCreatedAsync();
        var classA = new SchoolClass { Name = "5A" }; var classB = new SchoolClass { Name = "5B" };
        var group = new StudentGroup { Name = "Gezi", GroupType = "Manual" };
        var a = Student("100", classA.Id); var b = Student("200", classB.Id); var meal = new MealType { Name = "Öğle" };
        var source = Right(a.Id, meal.Id, new DateOnly(2026, 9, 14), 3, 1);
        db.AddRange(classA, classB, group, a, b, meal, source, Right(b.Id, meal.Id, source.EntitlementDate, 8, 4));
        db.Add(new StudentGroupMember { GroupId = group.Id, StudentId = a.Id });
        db.Add(new StudentLeave { StudentId = a.Id, StartsOn = source.EntitlementDate, EndsOn = source.EntitlementDate, LeaveType = "Rapor", EntitlementBehavior = "Keep" });
        db.Add(new MealTransfer { StudentId = a.Id, MealTypeId = meal.Id, SourceEntitlementId = source.Id,
            OriginalDate = source.EntitlementDate, TargetDate = source.EntitlementDate.AddDays(1), Quantity = 1, Reason = "Tatil", CreatedBy = Guid.NewGuid() });
        db.Add(new ScheduleOverride { Date = source.EntitlementDate, ExceptionType = "Trip", ScopeType = "Class", ScopeId = classA.Id,
            EntitlementBehavior = "Keep", CreatedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var holidayService = new HolidayService(new EfHolidayRepository(db));
        await holidayService.CreateAsync(new(source.EntitlementDate, "Okul tatili", "Official", null, "Delete", [new("AllSchool")]));

        var result = await new EfCalendarRepository(db).GetMonthAsync(new DateOnly(2026, 9, 1), new CalendarScope("Class", classA.Id), default);
        var day = Assert.Single(result.Days, x => x.Date == source.EntitlementDate);
        Assert.Equal(new CalendarEntitlementSummary(1, 1, 3, 1), day.Entitlements);
        Assert.Equal(1, day.LeaveCount); Assert.Equal(1, day.TransferOutCount); Assert.Single(day.Holidays); Assert.Single(day.Exceptions);
        Assert.Equal(30, result.Days.Count);
        var groupResult = await new EfCalendarRepository(db).GetMonthAsync(new DateOnly(2026, 9, 1), new CalendarScope("Group", group.Id), default);
        Assert.Equal(3, groupResult.Days.Single(x => x.Date == source.EntitlementDate).Entitlements.Quantity);
    }

    [Fact]
    public async Task DayDetailsListsMealsAndAllOperations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Context(connection); await db.Database.EnsureCreatedAsync();
        var student = Student("42", null); var meal = new MealType { Name = "Kahvaltı" }; var date = new DateOnly(2026, 9, 8);
        var right = Right(student.Id, meal.Id, date, 2, 1); db.AddRange(student, meal, right);
        db.Add(new StudentLeave { StudentId = student.Id, StartsOn = date, EndsOn = date, LeaveType = "İzin", EntitlementBehavior = "Keep" });
        db.Add(new MealTransfer { StudentId = student.Id, MealTypeId = meal.Id, SourceEntitlementId = right.Id,
            OriginalDate = date, TargetDate = date.AddDays(1), Quantity = 1, Reason = "Plan", CreatedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();
        await new HolidayService(new EfHolidayRepository(db)).CreateAsync(new(date, "Tatil", "Official", null, "Delete", [new("AllSchool")]));

        var result = await new EfCalendarRepository(db).GetDayAsync(date, null, default);
        Assert.Equal(2, result.Entitlements.Quantity); Assert.Equal(1, result.Entitlements.Used);
        Assert.Equal("Kahvaltı", Assert.Single(result.Meals).MealName);
        Assert.Contains(result.Operations, x => x.Kind == "Holiday"); Assert.Contains(result.Operations, x => x.Kind == "Leave");
        Assert.Contains(result.Operations, x => x.Kind == "TransferOut");
    }

    private static YemekhaneDbContext Context(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
    private static Student Student(string no, Guid? classId) => new() { StudentNo = no, FirstName = "Ada", LastName = no, ClassId = classId };
    private static MealEntitlement Right(Guid studentId, Guid mealId, DateOnly date, int quantity, int used) => new()
    { StudentId = studentId, MealTypeId = mealId, EntitlementDate = date, Quantity = quantity, ConsumedQuantity = used, Status = "Active" };
}
