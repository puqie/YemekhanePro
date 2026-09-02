using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Audit;

namespace Yemekhane.Infrastructure.Calendar;

public sealed class EfCalendarRepository(YemekhaneDbContext db, IAuditService auditService) : ICalendarRepository
{
    public EfCalendarRepository(YemekhaneDbContext db)
        : this(db, new AuditService(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext())) { }
    public async Task<IReadOnlyCollection<CalendarScopeOption>> ListScopesAsync(CancellationToken cancellationToken)
    {
        var classes = await db.Set<SchoolClass>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new CalendarScopeOption("Class", x.Id, x.Name)).ToListAsync(cancellationToken);
        var groups = await db.Set<StudentGroup>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new CalendarScopeOption("Group", x.Id, x.Name)).ToListAsync(cancellationToken);
        return [new CalendarScopeOption("AllSchool", null, "Tüm okul"), .. classes, .. groups];
    }

    public async Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScope? scope, CancellationToken cancellationToken)
    {
        var first = new DateOnly(month.Year, month.Month, 1); var end = first.AddMonths(1);
        // Yalnizca AKTIF haklar sayilir: iptal edilmis/aktarilmis/yakilmis bir hak o gun
        // yemek hakki degildir. Onceden iptal sonrasi gun rozeti "395 ogrenci" demeye
        // devam ediyor, Hakedisler ekranindaki sayiyla celisiyordu.
        var entitlements = ScopeStudents(db.MealEntitlements.AsNoTracking(), scope)
            .Where(x => x.EntitlementDate >= first && x.EntitlementDate < end && x.Status == "Active");
        var entitlementRows = await entitlements.GroupBy(x => x.EntitlementDate).Select(x => new
        {
            Date = x.Key, Students = x.Select(y => y.StudentId).Distinct().Count(), Count = x.Count(),
            Quantity = x.Sum(y => y.Quantity), Used = x.Sum(y => y.ConsumedQuantity)
        }).ToListAsync(cancellationToken);
        var holidays = await Holidays(first, end, scope, cancellationToken);
        var exceptions = await Exceptions(first, end, scope, cancellationToken);
        var leaves = await ScopeStudents(db.Set<StudentLeave>().AsNoTracking(), scope)
            .Where(x => x.StartsOn < end && x.EndsOn >= first).Select(x => new { x.StudentId, x.StartsOn, x.EndsOn }).ToListAsync(cancellationToken);
        var transfers = await ScopeStudents(db.MealTransfers.AsNoTracking(), scope)
            .Where(x => (x.OriginalDate >= first && x.OriginalDate < end) || (x.TargetDate >= first && x.TargetDate < end))
            .Select(x => new { x.OriginalDate, x.TargetDate }).ToListAsync(cancellationToken);

        var rights = entitlementRows.ToDictionary(x => x.Date);
        var days = Enumerable.Range(0, end.DayNumber - first.DayNumber).Select(offset =>
        {
            var date = first.AddDays(offset); rights.TryGetValue(date, out var right);
            return new CalendarDaySummary(date,
                new CalendarEntitlementSummary(right?.Students ?? 0, right?.Count ?? 0, right?.Quantity ?? 0, right?.Used ?? 0),
                holidays.Where(x => x.Date == date).Select(x => x.Item).ToArray(),
                exceptions.Where(x => x.Date == date).Select(x => x.Item).ToArray(),
                leaves.Count(x => x.StartsOn <= date && x.EndsOn >= date),
                transfers.Count(x => x.TargetDate == date), transfers.Count(x => x.OriginalDate == date));
        }).ToArray();
        return new MonthlyCalendar(first, scope, days);
    }

    public async Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScope? scope, CancellationToken cancellationToken)
    {
        var date = calendarDate;
        // Ay gorunumuyle ayni kural: yalnizca aktif haklar (bkz. GetMonthAsync).
        var rights = ScopeStudents(db.MealEntitlements.AsNoTracking(), scope).Where(x => x.EntitlementDate == date && x.Status == "Active");
        var summary = await rights.GroupBy(_ => 1).Select(x => new CalendarEntitlementSummary(
            x.Select(y => y.StudentId).Distinct().Count(), x.Count(), x.Sum(y => y.Quantity), x.Sum(y => y.ConsumedQuantity)))
            .SingleOrDefaultAsync(cancellationToken) ?? new(0, 0, 0, 0);
        var mealRows = await (from right in rights join meal in db.Set<MealType>().AsNoTracking() on right.MealTypeId equals meal.Id
            select new { meal.Id, meal.Name, right.StudentId, right.Quantity, right.ConsumedQuantity }).ToListAsync(cancellationToken);
        var meals = mealRows.GroupBy(x => new { x.Id, x.Name }).Select(rows => new CalendarMealBreakdown(rows.Key.Id, rows.Key.Name,
            rows.Select(x => x.StudentId).Distinct().Count(), rows.Count(), rows.Sum(x => x.Quantity), rows.Sum(x => x.ConsumedQuantity)))
            .OrderBy(x => x.MealName).ToArray();
        var holidays = await Holidays(date, date.AddDays(1), scope, cancellationToken);
        var exceptions = await Exceptions(date, date.AddDays(1), scope, cancellationToken);
        var leaves = await ScopeStudents(db.Set<StudentLeave>().AsNoTracking(), scope).Where(x => x.StartsOn <= date && x.EndsOn >= date)
            .Select(x => new { x.Id, x.LeaveType, x.Description }).ToListAsync(cancellationToken);
        var transfers = await ScopeStudents(db.MealTransfers.AsNoTracking(), scope)
            .Where(x => x.OriginalDate == date || x.TargetDate == date).Select(x => new { x.Id, x.OriginalDate, x.TargetDate, x.Quantity, x.Reason }).ToListAsync(cancellationToken);
        var operations = holidays.Select(x => new CalendarOperation(x.Item.Id, "Holiday", x.Item.Name, x.Item.TransferBehavior))
            .Concat(exceptions.Select(x => new CalendarOperation(x.Item.Id, "Exception", x.Item.ExceptionType, x.Item.Description)))
            .Concat(leaves.Select(x => new CalendarOperation(x.Id, "Leave", x.LeaveType, x.Description)))
            .Concat(transfers.Select(x => new CalendarOperation(x.Id, x.TargetDate == date ? "TransferIn" : "TransferOut",
                x.TargetDate == date ? "Aktarım girişi" : "Aktarım çıkışı", x.Reason, x.Quantity))).ToArray();
        return new CalendarDayDetails(date, summary, meals, operations, holidays.Select(x => x.Item).ToArray(),
            exceptions.Select(x => x.Item).ToArray(), leaves.Count, transfers.Count(x => x.TargetDate == date), transfers.Count(x => x.OriginalDate == date));
    }

    public async Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken)
    {
        var item = new ScheduleOverride { Date = request.Date, ExceptionType = request.ExceptionType, ScopeType = request.ScopeType,
            ScopeId = request.ScopeId, MealTypeId = request.MealTypeId, EntitlementBehavior = request.EntitlementBehavior,
            TargetDate = request.TargetDate, Description = request.Description, CreatedBy = request.CreatedBy };
        db.Add(item); auditService.Record(new AuditEntry("ScheduleExceptionCreated", nameof(ScheduleOverride), item.Id.ToString(),
            "Takvim istisnası oluşturuldu.", After: request, UserId: request.CreatedBy));
        await db.SaveChangesAsync(cancellationToken);
        return Map(item);
    }

    private async Task<List<(DateOnly Date, CalendarHolidayItem Item)>> Holidays(DateOnly first, DateOnly end, CalendarScope? scope, CancellationToken token)
    {
        var query = db.Holidays.AsNoTracking().Where(x => x.Date >= first && x.Date < end);
        if (scope is not null) query = query.Where(x => db.Set<HolidayScope>().Any(s => s.HolidayId == x.Id &&
            (s.ScopeType == "AllSchool" || (s.ScopeType == scope.ScopeType && s.ScopeId == scope.ScopeId))));
        var rows = await query.OrderBy(x => x.Date).ToListAsync(token); var ids = rows.Select(x => x.Id).ToArray();
        var scopes = await db.Set<HolidayScope>().AsNoTracking().Where(x => ids.Contains(x.HolidayId)).ToListAsync(token);
        return rows.Select(x => (x.Date, new CalendarHolidayItem(x.Id, x.Name, x.HolidayType, x.TransferBehavior,
            scopes.Where(y => y.HolidayId == x.Id).Select(y => new HolidayScopeRequest(y.ScopeType, y.ScopeId)).ToArray()))).ToList();
    }

    private async Task<List<(DateOnly Date, CalendarExceptionItem Item)>> Exceptions(DateOnly first, DateOnly end, CalendarScope? scope, CancellationToken token)
    {
        var query = db.Set<ScheduleOverride>().AsNoTracking().Where(x => x.Date >= first && x.Date < end);
        if (scope is not null) query = query.Where(x => x.ScopeType == "AllSchool" || (x.ScopeType == scope.ScopeType && x.ScopeId == scope.ScopeId));
        return (await query.OrderBy(x => x.Date).ToListAsync(token)).Select(x => (x.Date, Map(x))).ToList();
    }

    private IQueryable<T> ScopeStudents<T>(IQueryable<T> query, CalendarScope? scope) where T : class
    {
        if (scope is null) return query;
        if (typeof(T) == typeof(MealEntitlement))
        {
            var values = (IQueryable<MealEntitlement>)query;
            values = scope.ScopeType == "Class" ? values.Where(x => db.Students.Any(s => s.Id == x.StudentId && s.ClassId == scope.ScopeId))
                : values.Where(x => db.Set<StudentGroupMember>().Any(m => m.GroupId == scope.ScopeId && m.StudentId == x.StudentId));
            return (IQueryable<T>)values;
        }
        if (typeof(T) == typeof(StudentLeave))
        {
            var values = (IQueryable<StudentLeave>)query;
            values = scope.ScopeType == "Class" ? values.Where(x => db.Students.Any(s => s.Id == x.StudentId && s.ClassId == scope.ScopeId))
                : values.Where(x => db.Set<StudentGroupMember>().Any(m => m.GroupId == scope.ScopeId && m.StudentId == x.StudentId));
            return (IQueryable<T>)values;
        }
        var transfers = (IQueryable<MealTransfer>)query;
        transfers = scope.ScopeType == "Class" ? transfers.Where(x => db.Students.Any(s => s.Id == x.StudentId && s.ClassId == scope.ScopeId))
            : transfers.Where(x => db.Set<StudentGroupMember>().Any(m => m.GroupId == scope.ScopeId && m.StudentId == x.StudentId));
        return (IQueryable<T>)transfers;
    }

    private static CalendarExceptionItem Map(ScheduleOverride x) => new(x.Id, x.ExceptionType, x.ScopeType, x.ScopeId,
        x.EntitlementBehavior, x.TargetDate, x.Description);
}
