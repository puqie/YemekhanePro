using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Dashboard;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Dashboard;

public sealed class EfDashboardRepository(YemekhaneDbContext dbContext) : IDashboardRepository
{
    public async Task<DashboardSnapshot> GetAsync(DateOnly currentDate, DateTimeOffset dayStart,
        DateTimeOffset dayEnd, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        var activeStudents = await dbContext.Students.AsNoTracking().CountAsync(x => x.IsActive, cancellationToken);
        var entitlementQuery = dbContext.MealEntitlements.AsNoTracking()
            .Where(x => x.EntitlementDate == currentDate && x.Status == "Active");
        var entitlementSummary = await entitlementQuery.GroupBy(_ => 1).Select(x => new
        {
            Students = x.Select(value => value.StudentId).Distinct().Count(),
            Quantity = x.Sum(value => value.Quantity),
            Used = x.Sum(value => value.ConsumedQuantity)
        }).SingleOrDefaultAsync(cancellationToken);
        var entitledStudents = entitlementSummary?.Students ?? 0;
        var entitlementQuantity = entitlementSummary?.Quantity ?? 0;
        var used = entitlementSummary?.Used ?? 0;
        var onLeave = await (from leave in dbContext.Set<StudentLeave>().AsNoTracking()
                           join student in dbContext.Students.AsNoTracking() on leave.StudentId equals student.Id
                           where student.IsActive && leave.StartsOn <= currentDate && leave.EndsOn >= currentDate
                           select leave.StudentId).Distinct().CountAsync(cancellationToken);
        var denied = await dbContext.AccessLogs.AsNoTracking()
            .CountAsync(x => YemekhaneDbContext.JulianDay(x.Timestamp) >= YemekhaneDbContext.JulianDay(dayStart) &&
                             YemekhaneDbContext.JulianDay(x.Timestamp) < YemekhaneDbContext.JulianDay(dayEnd) &&
                             x.Decision == "DENY", cancellationToken);

        var recentAccess = await (from access in dbContext.AccessLogs.AsNoTracking()
                                join studentValue in dbContext.Students.AsNoTracking() on access.StudentId equals (Guid?)studentValue.Id into students
                                from student in students.DefaultIfEmpty()
                                join device in dbContext.Devices.AsNoTracking() on access.DeviceId equals device.Id
                                join mealValue in dbContext.Set<MealType>().AsNoTracking() on access.MealTypeId equals (Guid?)mealValue.Id into meals
                                from meal in meals.DefaultIfEmpty()
                                where YemekhaneDbContext.JulianDay(access.Timestamp) >= YemekhaneDbContext.JulianDay(dayStart)
                                   && YemekhaneDbContext.JulianDay(access.Timestamp) < YemekhaneDbContext.JulianDay(dayEnd)
                                orderby YemekhaneDbContext.JulianDay(access.Timestamp) descending
                                select new DashboardAccessRow(access.Id, access.Timestamp,
                                    student == null ? "Tanımsız kart" : student.FirstName + " " + student.LastName,
                                    student == null ? null : student.StudentNo, access.CardNumber, device.Name,
                                    meal == null ? null : meal.Name, access.Decision, access.Reason))
            .Take(20).ToListAsync(cancellationToken);

        var devices = await dbContext.Devices.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new DashboardDeviceRow(x.Id, x.Name, x.DeviceType, x.ConnectionStatus, x.LastConnectedAt))
            .ToListAsync(cancellationToken);

        var classUsage = await (from entitlement in entitlementQuery
                              join student in dbContext.Students.AsNoTracking() on entitlement.StudentId equals student.Id
                              join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classes
                              from schoolClass in classes.DefaultIfEmpty()
                              group entitlement by schoolClass == null ? "Sınıfsız" : schoolClass.Name into grouped
                              orderby grouped.Sum(x => x.ConsumedQuantity) descending, grouped.Key
                              select new DashboardClassUsage(grouped.Key, grouped.Sum(x => x.ConsumedQuantity), grouped.Sum(x => x.Quantity)))
            .Take(10).ToListAsync(cancellationToken);

        var errors = await (from item in dbContext.DeviceEvents.AsNoTracking()
                          join device in dbContext.Devices.AsNoTracking() on item.DeviceId equals device.Id
                          where item.Severity == "Error" || item.Severity == "Critical"
                          orderby YemekhaneDbContext.JulianDay(item.Timestamp) descending
                          select new DashboardErrorRow(item.Id, item.Timestamp, device.Name, item.Severity, item.Message))
            .Take(10).ToListAsync(cancellationToken);

        var summary = new DashboardDeviceSummary(devices.Count,
            devices.Count(x => string.Equals(x.Status, "Online", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(x.Status, "Connected", StringComparison.OrdinalIgnoreCase)),
            devices.Count(x => string.Equals(x.Status, "Offline", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(x.Status, "Disconnected", StringComparison.OrdinalIgnoreCase)),
            devices.Count(x => string.Equals(x.Status, "Error", StringComparison.OrdinalIgnoreCase)));
        return new DashboardSnapshot(currentDate, generatedAt,
            new DashboardKpis(activeStudents, entitledStudents, entitlementQuantity, used,
                Math.Max(0, entitlementQuantity - used), onLeave, denied),
            recentAccess, summary, devices, classUsage, errors);
    }
}
