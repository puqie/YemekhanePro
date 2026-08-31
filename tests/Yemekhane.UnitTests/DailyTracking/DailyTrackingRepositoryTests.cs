using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.DailyTracking;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.DailyTracking;

public sealed class DailyTrackingRepositoryTests
{
    [Fact]
    public async Task FiltersTodayAndReturnsStableCursorPage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var start = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        var schoolClass = new SchoolClass { Name = "10-A" };
        var otherClass = new SchoolClass { Name = "11-B" };
        var meal = new MealType { Name = "Öğle" };
        var otherMeal = new MealType { Name = "Akşam" };
        var device = Device("Ana Turnike");
        var otherDevice = Device("Yan Turnike");
        var student = new Student { StudentNo = "1001", FirstName = "Ada", LastName = "Yılmaz", ClassId = schoolClass.Id };
        var otherStudent = new Student { StudentNo = "2002", FirstName = "Ece", LastName = "Demir", ClassId = otherClass.Id };
        db.AddRange(schoolClass, otherClass, meal, otherMeal, device, otherDevice, student, otherStudent);
        var first = Access(student.Id, device.Id, meal.Id, start.AddHours(2), "ALLOW", "CARD-ADA");
        var second = Access(student.Id, device.Id, meal.Id, start.AddHours(1), "ALLOW", "CARD-ADA");
        db.AccessLogs.AddRange(first, second,
            Access(otherStudent.Id, otherDevice.Id, otherMeal.Id, start.AddHours(3), "DENY", "CARD-ECE"),
            Access(student.Id, device.Id, meal.Id, start.AddDays(-1), "ALLOW", "OLD"));
        await db.SaveChangesAsync();
        var repository = new EfDailyTrackingRepository(db);
        var filter = new DailyTrackingQuery(1, "ALLOW", meal.Id, device.Id, schoolClass.Id, "Ada");

        var page = await repository.GetAsync(filter, start, start.AddDays(1), start.AddHours(4), default);
        var next = await repository.GetAsync(filter with
        {
            CursorTimestamp = page.NextCursorTimestamp,
            CursorOperationId = page.NextCursorOperationId
        }, start, start.AddDays(1), start.AddHours(4), default);

        Assert.Equal(2, page.Summary.Total);
        Assert.Equal(first.OperationId, Assert.Single(page.Items).OperationId);
        Assert.True(page.HasMore);
        Assert.Equal(second.OperationId, Assert.Single(next.Items).OperationId);
        Assert.False(next.HasMore);
    }

    [Fact]
    public async Task SinceCursorReturnsOnlyReconnectGap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var start = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        var device = Device("Turnike");
        var meal = new MealType { Name = "Öğle" };
        db.AddRange(device, meal);
        var old = Access(null, device.Id, meal.Id, start.AddHours(1), "DENY", "OLD");
        var recent = Access(null, device.Id, meal.Id, start.AddHours(2), "DENY", "NEW");
        db.AccessLogs.AddRange(old, recent);
        await db.SaveChangesAsync();

        var result = await new EfDailyTrackingRepository(db).GetAsync(
            new DailyTrackingQuery(SinceTimestamp: old.Timestamp, SinceOperationId: old.OperationId),
            start, start.AddDays(1), start.AddHours(3), default);

        Assert.Equal(recent.OperationId, Assert.Single(result.Items).OperationId);
    }

    [Fact]
    public async Task OperationIdBreaksTimestampTiesWithoutRepeatingCursorRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var start = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        var device = Device("Turnike");
        var meal = new MealType { Name = "Öğle" };
        db.AddRange(device, meal);
        db.AccessLogs.AddRange(Enumerable.Range(0, 3).Select(i => Access(null, device.Id, meal.Id,
            start.AddHours(1), "ALLOW", $"CARD-{i}")));
        await db.SaveChangesAsync();
        var repository = new EfDailyTrackingRepository(db);
        var first = await repository.GetAsync(new DailyTrackingQuery(1), start, start.AddDays(1), start, default);
        var second = await repository.GetAsync(new DailyTrackingQuery(1, CursorTimestamp: first.NextCursorTimestamp,
            CursorOperationId: first.NextCursorOperationId), start, start.AddDays(1), start, default);

        Assert.NotEqual(Assert.Single(first.Items).OperationId, Assert.Single(second.Items).OperationId);
    }

    [Fact]
    public async Task CursorTieBreakWorksWhenSameInstantHasDifferentOffsets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var start = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        var device = Device("Turnike");
        var meal = new MealType { Name = "Öğle" };
        db.AddRange(device, meal);
        // Ayni mutlak an, farkli offset: julianday esit, ham metin karsilastirmasi esit degil.
        var instantUtc = new DateTimeOffset(2026, 8, 30, 22, 0, 0, TimeSpan.Zero);
        var instantTr = instantUtc.ToOffset(TimeSpan.FromHours(3));
        db.AccessLogs.AddRange(
            Access(null, device.Id, meal.Id, instantUtc, "ALLOW", "CARD-UTC"),
            Access(null, device.Id, meal.Id, instantTr, "ALLOW", "CARD-TR"));
        await db.SaveChangesAsync();
        var repository = new EfDailyTrackingRepository(db);

        var first = await repository.GetAsync(new DailyTrackingQuery(1), start, start.AddDays(1), start, default);
        var second = await repository.GetAsync(new DailyTrackingQuery(1, CursorTimestamp: first.NextCursorTimestamp,
            CursorOperationId: first.NextCursorOperationId), start, start.AddDays(1), start, default);

        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].OperationId, second.Items[0].OperationId);
    }

    private static Device Device(string name) => new() { Name = name, DeviceType = "SF300", ConnectionType = "TCP", Direction = "Entry", ConnectionStatus = "Online" };
    private static AccessLog Access(Guid? studentId, Guid deviceId, Guid mealId, DateTimeOffset timestamp, string decision, string card) => new()
    {
        StudentId = studentId, DeviceId = deviceId, MealTypeId = mealId, Timestamp = timestamp,
        CardNumber = card, Decision = decision, Reason = decision, Direction = "Entry", ReaderSource = "Reader", OperationId = Guid.NewGuid()
    };
}
