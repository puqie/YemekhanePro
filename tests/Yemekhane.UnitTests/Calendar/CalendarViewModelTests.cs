using Yemekhane.Application.Calendar;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Calendar;

public sealed class CalendarViewModelTests
{
    [Fact]
    public async Task InitializesMondayFirstGridWithTurkishLabelsAndScope()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8));
        await vm.InitializeAsync();
        Assert.Equal("Eylül 2026", vm.MonthTitle); Assert.Equal("Pzt", vm.DayNames[0]); Assert.Equal("Paz", vm.DayNames[6]);
        Assert.Equal(42, vm.Days.Count); Assert.Equal(new DateOnly(2026, 8, 31), vm.Days[0].Date);
        Assert.True(vm.IsEmpty);
        vm.SelectedScope = vm.Scopes.Single(x => x.ScopeType == "Class"); vm.ApplyScopeCommand.Execute(null);
        await Until(() => api.LastScope?.ScopeType == "Class");
    }

    [Fact]
    public async Task NavigationSelectionAndOfflineStatesAreExposed()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        vm.NextMonthCommand.Execute(null); await Until(() => vm.MonthTitle == "Ekim 2026");
        await vm.SelectDayAsync(new DateOnly(2026, 10, 5)); Assert.True(vm.IsDrawerOpen); Assert.NotNull(vm.SelectedDetails);
        await vm.MoveSelectionAsync(1); Assert.Equal(new DateOnly(2026, 10, 6), vm.SelectedDate);
        vm.CloseDrawer(); Assert.False(vm.IsDrawerOpen);
        api.Fail = true; await vm.LoadAsync(); Assert.True(vm.IsOffline); Assert.True(vm.HasError);
    }

    [Fact]
    public async Task CreatingHolidayRefreshesMonthAndSelectedDay()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14)); vm.OpenHolidayFormCommand.Execute(null); vm.HolidayName = "Gezi tatili";
        var previousLoads = api.MonthCalls; vm.CreateHolidayCommand.Execute(null); await Until(() => api.HolidayCalls == 1 && api.MonthCalls > previousLoads);
        Assert.False(vm.IsHolidayFormOpen); Assert.Equal(new DateOnly(2026, 9, 14), api.LastHoliday!.Date); Assert.True(api.DayCalls >= 2);
    }

    [Fact]
    public void MissingPermissionDisablesCalendarActions()
    {
        var vm = new CalendarViewModel(new FakeApi(), []);
        Assert.False(vm.CanManage); Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadingStateRemainsVisibleUntilMonthRequestCompletes()
    {
        var api = new FakeApi { MonthGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); var initialization = vm.InitializeAsync();
        await Until(() => vm.IsLoading); api.MonthGate.SetResult(); await initialization; Assert.False(vm.IsLoading);
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3); while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10); Assert.True(condition());
    }

    private sealed class FakeApi : ICalendarApiClient
    {
        private readonly CalendarScopeOption all = new("AllSchool", null, "Tüm okul");
        public bool Fail; public int MonthCalls, DayCalls, HolidayCalls; public CalendarScopeOption? LastScope; public CreateHolidayRequest? LastHoliday;
        public TaskCompletionSource? MonthGate;
        public Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([all, new("Class", Guid.NewGuid(), "5A"), new("Group", Guid.NewGuid(), "Sporcular")]);
        public async Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, CancellationToken cancellationToken = default)
        {
            if (MonthGate is not null) await MonthGate.Task.WaitAsync(cancellationToken);
            if (Fail) throw new HttpRequestException(); MonthCalls++; LastScope = scope; var first = new DateOnly(month.Year, month.Month, 1);
            var days = Enumerable.Range(0, first.AddMonths(1).DayNumber - first.DayNumber).Select(x => new CalendarDaySummary(first.AddDays(x), new(0, 0, 0, 0), [], [], 0, 0, 0)).ToArray();
            return new MonthlyCalendar(first, scope is null ? null : new CalendarScope(scope.ScopeType, scope.ScopeId), days);
        }
        public Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, CancellationToken cancellationToken = default)
        { DayCalls++; return Task.FromResult(new CalendarDayDetails(calendarDate, new(0, 0, 0, 0), [], [], [], [], 0, 0, 0)); }
        public Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default)
        { HolidayCalls++; LastHoliday = request; return Task.FromResult(new HolidayDetails(Guid.NewGuid(), request.Date, request.Name, request.HolidayType, request.Description, request.TransferBehavior, request.Scopes)); }
        public Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalendarExceptionItem(Guid.NewGuid(), request.ExceptionType, request.ScopeType, request.ScopeId, request.EntitlementBehavior, request.TargetDate, request.Description));
    }
}
