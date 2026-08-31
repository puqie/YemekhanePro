using Yemekhane.Application.Common;

namespace Yemekhane.Application.Calendar;

public sealed record CalendarScope(string ScopeType, Guid? ScopeId = null);
public sealed record WeekendPolicy(bool SaturdayIsWorking = false, bool SundayIsWorking = false);

public interface ICalendarClosureProvider
{
    Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken);
}

public sealed class BusinessDayService(ICalendarClosureProvider closureProvider, WeekendPolicy weekendPolicy)
{
    public async Task<bool> IsBusinessDayAsync(DateOnly date, CalendarScope scope, CancellationToken cancellationToken = default)
    {
        if (date.DayOfWeek == DayOfWeek.Saturday && !weekendPolicy.SaturdayIsWorking) return false;
        if (date.DayOfWeek == DayOfWeek.Sunday && !weekendPolicy.SundayIsWorking) return false;
        return !await closureProvider.IsClosedAsync(date, scope, cancellationToken);
    }

    public async Task<DateOnly> GetNextBusinessDayAsync(DateOnly date, CalendarScope scope, CancellationToken cancellationToken = default)
    {
        for (var offset = 1; offset <= 3_660; offset++)
        {
            var candidate = date.AddDays(offset);
            if (await IsBusinessDayAsync(candidate, scope, cancellationToken)) return candidate;
        }
        throw new EntityNotFoundException("On yıl içinde uygun iş günü bulunamadı.");
    }
}
