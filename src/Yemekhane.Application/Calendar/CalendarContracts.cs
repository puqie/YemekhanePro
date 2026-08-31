namespace Yemekhane.Application.Calendar;

public sealed record CalendarEntitlementSummary(int StudentCount, int Count, int Quantity, int Used);

public sealed record CalendarHolidayItem(Guid Id, string Name, string HolidayType, string TransferBehavior,
    IReadOnlyCollection<HolidayScopeRequest> Scopes);

public sealed record CalendarExceptionItem(Guid Id, string ExceptionType, string ScopeType, Guid? ScopeId,
    string EntitlementBehavior, DateOnly? TargetDate, string? Description);

public sealed record CalendarDaySummary(DateOnly Date, CalendarEntitlementSummary Entitlements,
    IReadOnlyCollection<CalendarHolidayItem> Holidays, IReadOnlyCollection<CalendarExceptionItem> Exceptions,
    int LeaveCount, int TransferInCount, int TransferOutCount);

public sealed record MonthlyCalendar(DateOnly Month, CalendarScope? Scope, IReadOnlyCollection<CalendarDaySummary> Days);

public sealed record CalendarScopeOption(string ScopeType, Guid? ScopeId, string Name);

public sealed record CalendarMealBreakdown(Guid MealTypeId, string MealName, int StudentCount, int Count,
    int Quantity, int Used);

public sealed record CalendarOperation(Guid Id, string Kind, string Title, string? Detail, int Quantity = 0);

public sealed record CalendarDayDetails(DateOnly Date, CalendarEntitlementSummary Entitlements,
    IReadOnlyCollection<CalendarMealBreakdown> Meals, IReadOnlyCollection<CalendarOperation> Operations,
    IReadOnlyCollection<CalendarHolidayItem> Holidays, IReadOnlyCollection<CalendarExceptionItem> Exceptions,
    int LeaveCount, int TransferInCount, int TransferOutCount);

public sealed record CreateScheduleExceptionRequest(DateOnly Date, string ExceptionType, string ScopeType,
    Guid? ScopeId, Guid? MealTypeId, string EntitlementBehavior, DateOnly? TargetDate, string? Description,
    Guid CreatedBy);

public interface ICalendarRepository
{
    Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScope? scope, CancellationToken cancellationToken);
    Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScope? scope, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<CalendarScopeOption>> ListScopesAsync(CancellationToken cancellationToken);
    Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken);
}
