using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Dashboard;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Dashboard;

public sealed class DashboardRepositoryTests
{
    [Fact]
    public async Task ComputesTodayKpisAndServerProjectionsCorrectly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var currentDate = new DateOnly(2026, 8, 31);
        var start = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        var schoolClass = new SchoolClass { Name = "10-A" };
        var meal = new MealType { Name = "Öğle" };
        var device = new Device { Name = "Ana Turnike", DeviceType = "SF300", ConnectionType = "TCP", Direction = "Entry", ConnectionStatus = "Online" };
        var first = Student("100", "Ada", schoolClass.Id, true);
        var second = Student("101", "Ece", schoolClass.Id, true);
        var inactive = Student("102", "Can", schoolClass.Id, false);
        db.AddRange(schoolClass, meal, device, first, second, inactive);
        db.AddRange(
            Entitlement(first.Id, meal.Id, currentDate, 2, 1),
            Entitlement(second.Id, meal.Id, currentDate, 1, 1),
            Entitlement(first.Id, meal.Id, currentDate.AddDays(-1), 8, 8));
        db.Set<StudentLeave>().Add(new StudentLeave { StudentId = second.Id, StartsOn = currentDate, EndsOn = currentDate.AddDays(1), LeaveType = "Medical", EntitlementBehavior = "Keep" });
        db.AccessLogs.AddRange(
            Access(first.Id, device.Id, meal.Id, start.AddHours(1), "ALLOW"),
            Access(second.Id, device.Id, meal.Id, start.AddHours(2), "DENY"),
            Access(second.Id, device.Id, meal.Id, start.AddDays(-1), "DENY"));
        db.DeviceEvents.Add(new DeviceEvent { DeviceId = device.Id, Timestamp = start.AddHours(3), EventType = "Connection", Severity = "Error", Message = "Bağlantı kesildi" });
        await db.SaveChangesAsync();

        var result = await new EfDashboardRepository(db).GetAsync(currentDate, start, start.AddDays(1), start.AddHours(4), default);

        Assert.Equal(2, result.Kpis.ActiveStudents);
        Assert.Equal(2, result.Kpis.EntitledStudents);
        Assert.Equal(3, result.Kpis.EntitlementQuantity);
        Assert.Equal(2, result.Kpis.Used);
        Assert.Equal(1, result.Kpis.Remaining);
        Assert.Equal(1, result.Kpis.OnLeave);
        Assert.Equal(1, result.Kpis.Denied);
        Assert.Equal(2, result.RecentAccess.Count);
        Assert.Equal("10-A", Assert.Single(result.ClassUsage).ClassName);
        Assert.Equal(1, result.DeviceSummary.Online);
        Assert.Equal("Bağlantı kesildi", Assert.Single(result.RecentErrors).Message);
    }

    private static Student Student(string number, string name, Guid classId, bool active) => new()
    {
        StudentNo = number, FirstName = name, LastName = "Yılmaz", ClassId = classId, IsActive = active
    };

    private static MealEntitlement Entitlement(Guid studentId, Guid mealId, DateOnly date, int quantity, int consumed) => new()
    {
        StudentId = studentId, MealTypeId = mealId, EntitlementDate = date,
        Quantity = quantity, ConsumedQuantity = consumed, Status = "Active"
    };

    private static AccessLog Access(Guid studentId, Guid deviceId, Guid mealId, DateTimeOffset timestamp, string decision) => new()
    {
        StudentId = studentId, DeviceId = deviceId, MealTypeId = mealId, Timestamp = timestamp,
        CardNumber = "CARD", Decision = decision, Reason = decision, Direction = "Entry",
        ReaderSource = "Reader", OperationId = Guid.NewGuid()
    };
}
