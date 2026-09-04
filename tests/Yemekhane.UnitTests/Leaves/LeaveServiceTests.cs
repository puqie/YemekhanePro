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

    /// <summary>
    /// COK GUNLU IZIN: bes gunluk bir iznin haklari BES AYRI GUNE dagilmalidir.
    ///
    /// <para>
    /// Onceki davranis her hakki AYNI "sonraki is gunune" tasiyordu ve orada
    /// topluyordu (Quantity += ...): ogrenci 14 Eylul'de bes ogun hakkina sahip
    /// gorunuyor, 15-18 arasi bos kaliyordu. Sahada bildirilen sikayet buydu --
    /// "bes gunu olana bes gun" verilmesi gerekiyordu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CokGunluIzinHaklariAyriGunlereDagitir()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "1", FirstName = "A", LastName = "B" }; var meal = new MealType { Name = "Öğle" };
        context.AddRange(student, meal); await context.SaveChangesAsync();
        // 7-11 Eylul 2026: Pazartesi-Cuma, bes is gunu.
        foreach (var day in new[] { 7, 8, 9, 10, 11 })
            context.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
                EntitlementDate = new(2026, 9, day), Quantity = 1, Status = "Active" });
        await context.SaveChangesAsync();
        var businessDay = new BusinessDayService(new OpenCalendar(), new WeekendPolicy());
        var service = new LeaveService(new EfLeaveRepository(context, businessDay));

        await service.CreateAsync(new CreateLeaveRequest(student.Id, new(2026, 9, 7), new(2026, 9, 11),
            "İzin", null, "NextBusinessDay", Guid.NewGuid()));

        // Bes hak, izleyen bes IS GUNUNE birer birer dagilmali (hafta sonu atlanir).
        var transferred = await context.MealEntitlements.AsNoTracking()
            .Where(x => x.Source == "LeaveTransfer").OrderBy(x => x.EntitlementDate).ToListAsync();
        Assert.Equal([new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16),
            new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18)], transferred.Select(x => x.EntitlementDate));
        // Hicbir gune YIGILMAMALI: her gun tek hak.
        Assert.All(transferred, x => Assert.Equal(1, x.Quantity));
        Assert.Equal(5, (await context.Set<MealTransfer>().ToListAsync()).Count);
    }

    /// <summary>
    /// Hedef gun DOLUYSA hak devamina eklenir, ustune yigilmaz.
    /// </summary>
    [Fact]
    public async Task DoluHedefGunAtlanir()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = CreateContext(connection); await context.Database.MigrateAsync();
        var student = new Student { StudentNo = "1", FirstName = "A", LastName = "B" }; var meal = new MealType { Name = "Öğle" };
        context.AddRange(student, meal); await context.SaveChangesAsync();
        context.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
            EntitlementDate = new(2026, 9, 11), Quantity = 1, Status = "Active" });
        // 14 Eylul ZATEN dolu: devir 15'ine gitmeli.
        context.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id,
            EntitlementDate = new(2026, 9, 14), Quantity = 1, Status = "Active" });
        await context.SaveChangesAsync();
        var businessDay = new BusinessDayService(new OpenCalendar(), new WeekendPolicy());
        var service = new LeaveService(new EfLeaveRepository(context, businessDay));

        await service.CreateAsync(new CreateLeaveRequest(student.Id, new(2026, 9, 11), new(2026, 9, 11),
            "İzin", null, "NextBusinessDay", Guid.NewGuid()));

        var moved = await context.MealEntitlements.AsNoTracking().SingleAsync(x => x.Source == "LeaveTransfer");
        Assert.Equal(new DateOnly(2026, 9, 15), moved.EntitlementDate);
        // Var olan 14 Eylul hakki BOZULMAMALI.
        Assert.Equal(1, (await context.MealEntitlements.AsNoTracking()
            .SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, 14) && x.Source == null)).Quantity);
    }

    private sealed class OpenCalendar : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) => Task.FromResult(false);
    }
    private static YemekhaneDbContext CreateContext(SqliteConnection connection) => new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}
