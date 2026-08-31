using Yemekhane.Application.Calendar;

namespace Yemekhane.UnitTests.Calendar;

public sealed class BusinessDayServiceTests
{
    [Fact]
    public async Task NextBusinessDaySkipsHolidayAndWeekend()
    {
        var closed = new HashSet<DateOnly> { new(2026, 4, 23), new(2026, 4, 24) };
        var service = new BusinessDayService(new FakeClosureProvider(closed), new WeekendPolicy());

        var result = await service.GetNextBusinessDayAsync(new DateOnly(2026, 4, 23), new CalendarScope("AllSchool"));

        Assert.Equal(new DateOnly(2026, 4, 27), result);
    }

    [Fact]
    public async Task SaturdayCanBeConfiguredAsWorkingDay()
    {
        var service = new BusinessDayService(new FakeClosureProvider(new HashSet<DateOnly>()), new WeekendPolicy(SaturdayIsWorking: true));

        var result = await service.GetNextBusinessDayAsync(new DateOnly(2026, 9, 11), new CalendarScope("Class", Guid.NewGuid()));

        Assert.Equal(new DateOnly(2026, 9, 12), result);
    }

    private sealed class FakeClosureProvider(IReadOnlySet<DateOnly> closed) : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(closed.Contains(calendarDate));
    }
}
