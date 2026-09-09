using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Calendar;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Calendar;

/// <summary>
/// Mutfaga verilen gunluk sayi ILKOKULUN sayisidir: anasinifi ayri ucretlendirildigi icin
/// o rakama karismamalidir. Sinifi girilmemis ogrenci ise SAYILIR; yemege geliyorsa
/// sayimdan dusmek mutfaga eksik kisi bildirir.
/// </summary>
public sealed class ClassKindCountingTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Day = new(2026, 10, 12);
    private Guid mealTypeId;

    public ClassKindCountingTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var meal = new MealType { Name = "Öğle Yemeği" };
        db.Add(meal);
        db.SaveChanges();
        mealTypeId = meal.Id;
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private EfCalendarRepository Repository() => new(db);

    private Guid AddClass(string name, string kind)
    {
        var row = new SchoolClass { Name = name, Kind = kind };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    /// <param name="classId">null: sinifi girilmemis ogrenci.</param>
    private void AddStudentWithEntitlement(string no, Guid? classId, int quantity = 1, int consumed = 0)
    {
        var student = new Student { StudentNo = no, FirstName = "Ad" + no, LastName = "Soyad", ClassId = classId };
        db.Add(student);
        db.SaveChanges();
        db.Add(new MealEntitlement
        {
            StudentId = student.Id, MealTypeId = mealTypeId, EntitlementDate = Day,
            Quantity = quantity, ConsumedQuantity = consumed, Status = "Active"
        });
        db.SaveChanges();
    }

    private void Seed()
    {
        var normal = AddClass("5/A", ClassKinds.Normal);
        var preschool = AddClass("Anasınıfı A", ClassKinds.Preschool);
        AddStudentWithEntitlement("1001", normal, consumed: 1);
        AddStudentWithEntitlement("1002", normal);
        AddStudentWithEntitlement("2001", preschool, consumed: 1);
        AddStudentWithEntitlement("3001", null);           // sinifi girilmemis
    }

    private async Task<CalendarEntitlementSummary> MonthAsync(string? classKind)
    {
        var scope = classKind is null ? null : new CalendarScope("AllSchool", null, classKind);
        var month = await Repository().GetMonthAsync(Day, scope, default);
        return month.Days.Single(x => x.Date == Day).Entitlements;
    }

    /// <summary>Suzgec yokken herkes sayilir: 4 ogrenci.</summary>
    [Fact]
    public async Task WithoutAFilterEveryoneIsCounted()
    {
        Seed();

        var summary = await MonthAsync(null);

        Assert.Equal(4, summary.StudentCount);
        Assert.Equal(4, summary.Quantity);
        Assert.Equal(2, summary.Used);
    }

    /// <summary>Ilkokul: anasinifi haric, SINIFSIZ ogrenci DAHIL.</summary>
    [Fact]
    public async Task NormalExcludesPreschoolButKeepsUnassignedStudents()
    {
        Seed();

        var summary = await MonthAsync(ClassKinds.Normal);

        Assert.Equal(3, summary.StudentCount);
        Assert.Equal(3, summary.Quantity);
        Assert.Equal(1, summary.Used);
    }

    [Fact]
    public async Task PreschoolCountsOnlyPreschool()
    {
        Seed();

        var summary = await MonthAsync(ClassKinds.Preschool);

        Assert.Equal(1, summary.StudentCount);
        Assert.Equal(1, summary.Quantity);
        Assert.Equal(1, summary.Used);
    }

    /// <summary>Gun cekmecesi de ayni suzgeci uygular; ay ve gun sayilari birbirini tutmali.</summary>
    [Fact]
    public async Task TheDayDrawerUsesTheSameFilter()
    {
        Seed();

        var all = await Repository().GetDayAsync(Day, null, default);
        var normal = await Repository().GetDayAsync(Day, new CalendarScope("AllSchool", null, ClassKinds.Normal), default);

        Assert.Equal(4, all.Entitlements.Quantity);
        Assert.Equal(3, normal.Entitlements.Quantity);
        // Ogun dagilimi da suzulur; yoksa toplamla celisirdi.
        Assert.Equal(3, normal.Meals.Single().Quantity);
    }

    /// <summary>Izinliler de suzulur: anasinifi izni ilkokul sayisini etkilememeli.</summary>
    [Fact]
    public async Task LeavesAreFilteredToo()
    {
        var normal = AddClass("5/A", ClassKinds.Normal);
        var preschool = AddClass("Anasınıfı A", ClassKinds.Preschool);
        AddStudentWithEntitlement("1001", normal);
        AddStudentWithEntitlement("2001", preschool);
        foreach (var student in await db.Students.ToListAsync())
            db.Add(new StudentLeave { StudentId = student.Id, StartsOn = Day, EndsOn = Day, LeaveType = "Rapor", EntitlementBehavior = "Delete" });
        await db.SaveChangesAsync();

        var all = await Repository().GetDayAsync(Day, null, default);
        var normalOnly = await Repository().GetDayAsync(Day, new CalendarScope("AllSchool", null, ClassKinds.Normal), default);

        Assert.Equal(2, all.LeaveCount);
        Assert.Equal(1, normalOnly.LeaveCount);
    }

    /// <summary>Sinif kapsami ile tur suzgeci birlikte calisir; biri otekini yutmamali.</summary>
    [Fact]
    public async Task ScopeAndKindComposeTogether()
    {
        var normal = AddClass("5/A", ClassKinds.Normal);
        var otherNormal = AddClass("6/B", ClassKinds.Normal);
        AddStudentWithEntitlement("1001", normal);
        AddStudentWithEntitlement("1002", otherNormal);

        var scoped = await MonthScopeAsync(new CalendarScope("Class", normal, ClassKinds.Normal));

        Assert.Equal(1, scoped.StudentCount);
    }

    private async Task<CalendarEntitlementSummary> MonthScopeAsync(CalendarScope scope)
    {
        var month = await Repository().GetMonthAsync(Day, scope, default);
        return month.Days.Single(x => x.Date == Day).Entitlements;
    }
}
