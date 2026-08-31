using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.DailyTracking;

public sealed class EfDailyTrackingRepository(YemekhaneDbContext dbContext) : IDailyTrackingRepository
{
    public async Task<DailyTrackingPage> GetAsync(DailyTrackingQuery request, DateTimeOffset dayStart,
        DateTimeOffset dayEnd, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        var query = from access in dbContext.AccessLogs.AsNoTracking()
                    join studentValue in dbContext.Students.IgnoreQueryFilters().AsNoTracking() on access.StudentId equals (Guid?)studentValue.Id into students
                    from student in students.DefaultIfEmpty()
                    join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classes
                    from schoolClass in classes.DefaultIfEmpty()
                    join mealValue in dbContext.Set<MealType>().AsNoTracking() on access.MealTypeId equals (Guid?)mealValue.Id into meals
                    from meal in meals.DefaultIfEmpty()
                    join device in dbContext.Devices.AsNoTracking() on access.DeviceId equals device.Id
                    where YemekhaneDbContext.JulianDay(access.Timestamp) >= YemekhaneDbContext.JulianDay(dayStart)
                       && YemekhaneDbContext.JulianDay(access.Timestamp) < YemekhaneDbContext.JulianDay(dayEnd)
                    select new { Access = access, Student = student, Class = schoolClass, Meal = meal, Device = device };

        if (request.Decision is not null) query = query.Where(x => x.Access.Decision == request.Decision);
        if (request.MealTypeId.HasValue) query = query.Where(x => x.Access.MealTypeId == request.MealTypeId);
        if (request.DeviceId.HasValue) query = query.Where(x => x.Access.DeviceId == request.DeviceId);
        if (request.ClassId.HasValue) query = query.Where(x => x.Student != null && x.Student.ClassId == request.ClassId);
        if (request.StudentId.HasValue) query = query.Where(x => x.Access.StudentId == request.StudentId);
        if (request.Search is not null)
        {
            var pattern = $"%{EscapeLike(request.Search)}%";
            query = query.Where(x => EF.Functions.Like(x.Access.CardNumber, pattern, "\\")
                || (x.Student != null && (EF.Functions.Like(x.Student.StudentNo, pattern, "\\")
                    || EF.Functions.Like(x.Student.FirstName + " " + x.Student.LastName, pattern, "\\"))));
        }

        var summary = await query.GroupBy(_ => 1).Select(x => new DailyTrackingSummary(
            x.Count(), x.Count(value => value.Access.Decision == "ALLOW"),
            x.Count(value => value.Access.Decision == "DENY"))).SingleOrDefaultAsync(cancellationToken)
            ?? new DailyTrackingSummary(0, 0, 0);

        if (request.CursorTimestamp is { } cursorTimestamp && request.CursorOperationId is { } cursorOperationId)
            query = query.Where(x => YemekhaneDbContext.JulianDay(x.Access.Timestamp) < YemekhaneDbContext.JulianDay(cursorTimestamp)
                || (YemekhaneDbContext.JulianDay(x.Access.Timestamp) == YemekhaneDbContext.JulianDay(cursorTimestamp)
                    && x.Access.OperationId.CompareTo(cursorOperationId) < 0));
        if (request.SinceTimestamp is { } sinceTimestamp && request.SinceOperationId is { } sinceOperationId)
            query = query.Where(x => YemekhaneDbContext.JulianDay(x.Access.Timestamp) > YemekhaneDbContext.JulianDay(sinceTimestamp)
                || (YemekhaneDbContext.JulianDay(x.Access.Timestamp) == YemekhaneDbContext.JulianDay(sinceTimestamp)
                    && x.Access.OperationId.CompareTo(sinceOperationId) > 0));

        var values = await query.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.Access.Timestamp))
            .ThenByDescending(x => x.Access.OperationId).Take(request.PageSize + 1)
            .Select(x => new DailyTrackingRow(x.Access.OperationId, x.Access.Timestamp, x.Access.CardNumber,
                x.Access.StudentId, x.Student == null ? null : x.Student.StudentNo,
                x.Student == null ? "Tanımsız kart" : x.Student.FirstName + " " + x.Student.LastName,
                x.Student == null ? null : x.Student.ClassId, x.Class == null ? null : x.Class.Name,
                x.Access.MealTypeId, x.Meal == null ? null : x.Meal.Name, x.Access.DeviceId,
                x.Device.Name, x.Access.Decision, x.Access.Reason))
            .ToListAsync(cancellationToken);
        var hasMore = values.Count > request.PageSize;
        if (hasMore) values.RemoveAt(values.Count - 1);
        var last = values.LastOrDefault();
        return new DailyTrackingPage(values, summary, generatedAt,
            hasMore ? last?.Timestamp : null, hasMore ? last?.OperationId : null, hasMore);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
