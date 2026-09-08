using System.Net.Http;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Meals;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Calendar;

/// <summary>
/// Takvim cekmecesinde tatil araligi ve silme: form secili gunle acilir, bitis &gt; baslangic
/// ise EndDate gider ve bilgi metni gun sayisini soyler, ters aralik sunucuya gitmeden durur,
/// gunun tatilleri silme dugmeleriyle listelenir (iki adimli, dugmeye ozel onay), sihirbaz
/// aralikla on ayarlanir.
/// </summary>
public sealed class HolidayRangeViewModelTests
{
    private static readonly DateOnly Today = new(2026, 9, 8);

    [Fact]
    public async Task FormOpensWithSelectedDayAsBothEndsAndCountsDays()
    {
        var api = new FakeApi();
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 6, 6));

        vm.OpenHolidayFormCommand.Execute(null);

        Assert.Equal(new DateTime(2026, 6, 6), vm.HolidayStart);
        Assert.Equal(new DateTime(2026, 6, 6), vm.HolidayEnd);
        Assert.Equal(1, vm.HolidayDayCount);
        Assert.Equal("Tek gün.", vm.HolidayRangeText);
        vm.HolidayEnd = new DateTime(2026, 6, 9);
        Assert.Equal(4, vm.HolidayDayCount);
        Assert.StartsWith("4 gün", vm.HolidayRangeText, StringComparison.Ordinal);
        vm.HolidayEnd = new DateTime(2026, 6, 1);
        Assert.Equal(0, vm.HolidayDayCount);
        Assert.Contains("önce olamaz", vm.HolidayRangeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RangeIsSentAsEndDateAndTheMessageNamesTheDays()
    {
        var api = new FakeApi();
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 6, 6));
        vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "Kurban Bayramı"; vm.HolidayEnd = new DateTime(2026, 6, 9);

        await ((AsyncCommand)vm.CreateHolidayCommand).ExecuteAsync(null);

        var request = Assert.Single(api.Created);
        Assert.Equal(new DateOnly(2026, 6, 6), request.Date);
        Assert.Equal(new DateOnly(2026, 6, 9), request.EndDate);
        Assert.Equal(4, request.DayCount);
        Assert.False(vm.IsHolidayFormOpen);
        Assert.Contains("4 gün", vm.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("6 Haz", vm.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleDaySendsNoEndDate()
    {
        var api = new FakeApi();
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 4, 23));
        vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "23 Nisan";

        await ((AsyncCommand)vm.CreateHolidayCommand).ExecuteAsync(null);

        Assert.Null(Assert.Single(api.Created).EndDate);
        Assert.StartsWith("Tatil kaydedildi.", vm.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackwardsRangeStopsBeforeTheServer()
    {
        var api = new FakeApi();
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 6, 9));
        vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "Ters"; vm.HolidayEnd = new DateTime(2026, 6, 6);

        await ((AsyncCommand)vm.CreateHolidayCommand).ExecuteAsync(null);

        Assert.Empty(api.Created);
        Assert.True(vm.IsHolidayFormOpen);
        Assert.Contains("başlangıçtan önce", vm.FormMessage, StringComparison.Ordinal);
    }

    /// <summary>Formda baslangic baska bir gune/aya cekilirse cekmece ve takvim oraya gider; eski gun gorunur kalmaz.</summary>
    [Fact]
    public async Task StartOnAnotherMonthMovesSelectionAndMonth()
    {
        var api = new FakeApi();
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14));
        vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "Cumhuriyet"; vm.HolidayStart = new DateTime(2026, 10, 29); vm.HolidayEnd = new DateTime(2026, 10, 29);

        await ((AsyncCommand)vm.CreateHolidayCommand).ExecuteAsync(null);

        Assert.Equal(new DateOnly(2026, 10, 29), vm.SelectedDate);
        Assert.Equal("Ekim 2026", vm.MonthTitle);
        Assert.Equal(new DateOnly(2026, 10, 29), Assert.Single(api.Created).Date);
    }

    [Fact]
    public async Task HolidayRowsListTheDayWithDeleteButtonsAndHideThemFromOperations()
    {
        var group = Guid.NewGuid();
        var api = new FakeApi
        {
            DayHolidays = [new(Guid.NewGuid(), "Yarıyıl", "Official", "NextBusinessDay", [new("AllSchool")], group, 12), new(Guid.NewGuid(), "5A Gezi", "Trip", "Forfeit", [new("Class", FakeApi.ClassId)])],
            DayOperations = [new(Guid.NewGuid(), "Holiday", "Yarıyıl", "NextBusinessDay"), new(Guid.NewGuid(), "Leave", "Ali Kaya", "Hasta")]
        };
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 2, 2));

        Assert.True(vm.HasHolidayRows);
        Assert.Equal(2, vm.HolidayRows.Count);
        var range = vm.HolidayRows[0];
        Assert.Equal("Yarıyıl", range.Title);
        Assert.Contains("Tüm okul", range.Detail, StringComparison.Ordinal);
        Assert.Contains("Sonraki iş gününe aktar", range.Detail, StringComparison.Ordinal);
        Assert.True(range.IsMultiDay);
        Assert.Equal("12 günlük tatil aralığının bir günü", range.RangeText);
        Assert.Equal("Tüm aralığı sil (12 gün)", range.DeleteRangeText);
        var trip = vm.HolidayRows[1];
        Assert.False(trip.IsMultiDay);
        Assert.Contains("5A", trip.Detail, StringComparison.Ordinal);
        Assert.False(trip.DeleteRangeCommand.CanExecute(null));
        // Olay listesinde tatil satiri tekrar etmez; izin kalir.
        Assert.Single(vm.SelectedOperations);
        Assert.StartsWith("İzin", vm.SelectedOperations[0].Title, StringComparison.Ordinal);
        Assert.False(vm.HasNoOperations);
    }

    [Fact]
    public async Task DeleteIsTwoStepPerButtonAndRefreshesAfterwards()
    {
        var holidayId = Guid.NewGuid();
        var api = new FakeApi { DayHolidays = [new(holidayId, "Yarıyıl", "Official", "Delete", [new("AllSchool")], Guid.NewGuid(), 12)] };
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 2, 2));
        var row = vm.HolidayRows.Single();
        var dayCalls = api.DayCalls;

        await ((AsyncCommand)row.DeleteDayCommand).ExecuteAsync(null);
        Assert.True(row.IsDeleteArmed);
        Assert.Equal("Bu günü silmeyi onayla", row.DeleteDayText);
        Assert.Empty(api.Deleted);

        // Silahlanan dugme "bu gun"; "tum aralik" ilk basista SILMEZ, kendi onayini ister.
        await ((AsyncCommand)row.DeleteRangeCommand).ExecuteAsync(null);
        Assert.Empty(api.Deleted);
        Assert.Equal("Tüm aralığı silmeyi onayla (12 gün)", row.DeleteRangeText);
        Assert.Equal("Bu günü sil", row.DeleteDayText);

        row.CancelDeleteCommand.Execute(null);
        Assert.False(row.IsDeleteArmed);

        api.DayHolidays = [];
        await ((AsyncCommand)row.DeleteRangeCommand).ExecuteAsync(null);
        await ((AsyncCommand)row.DeleteRangeCommand).ExecuteAsync(null);

        Assert.Equal((holidayId, true), Assert.Single(api.Deleted));
        Assert.True(api.DayCalls > dayCalls);
        Assert.Empty(vm.HolidayRows);
        Assert.Contains("12 günlük tatil aralığı silindi", vm.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteFailureShowsTheServerMessage()
    {
        var api = new FakeApi
        {
            DayHolidays = [new(Guid.NewGuid(), "Bayram", "Official", "Delete", [new("AllSchool")])],
            DeleteError = new ApiRequestException("Tatil kaydı bulunamadı; silinmiş olabilir.", System.Net.HttpStatusCode.NotFound)
        };
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 2, 2));
        var row = vm.HolidayRows.Single();

        await ((AsyncCommand)row.DeleteDayCommand).ExecuteAsync(null);
        await ((AsyncCommand)row.DeleteDayCommand).ExecuteAsync(null);

        Assert.Equal("Tatil kaydı bulunamadı; silinmiş olabilir.", vm.FormMessage);
    }

    [Fact]
    public async Task WizardIsPresetWithTheWholeRange()
    {
        var api = new FakeApi();
        var wizard = new BulkOperationWizardViewModel(new ThrowingBulkApi(), ["entitlements.bulk", "calendar.manage"]);
        var vm = new CalendarViewModel(api, ["calendar.manage"], Today, wizard);
        await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 6, 6));
        vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "Kurban Bayramı"; vm.HolidayEnd = new DateTime(2026, 6, 9); vm.TransferBehavior = "Forfeit";
        await ((AsyncCommand)vm.CreateHolidayCommand).ExecuteAsync(null);

        vm.OpenBulkCommand.Execute(null);

        Assert.True(wizard.IsOpen);
        Assert.Equal(new DateTime(2026, 6, 6), wizard.StartsOn);
        Assert.Equal(new DateTime(2026, 6, 9), wizard.EndsOn);
        Assert.Equal("Forfeit", wizard.TransferBehavior);
    }

    [Fact]
    public void WizardPresetIgnoresAnEndBeforeTheStart()
    {
        var wizard = new BulkOperationWizardViewModel(new ThrowingBulkApi(), ["entitlements.bulk"]);

        wizard.Preset(new DateOnly(2026, 6, 6), endDate: new DateOnly(2026, 6, 1));
        Assert.Equal(new DateTime(2026, 6, 6), wizard.EndsOn);

        wizard.Preset(new DateOnly(2026, 6, 6), endDate: new DateOnly(2026, 6, 9));
        Assert.Equal(new DateTime(2026, 6, 9), wizard.EndsOn);
    }

    private sealed class ThrowingBulkApi : IBulkOperationApiClient
    {
        public Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BulkOperationHistoryPage> HistoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeApi : ICalendarApiClient
    {
        public static readonly Guid ClassId = Guid.NewGuid();
        public List<CreateHolidayRequest> Created { get; } = [];
        public List<(Guid Id, bool WholeRange)> Deleted { get; } = [];
        public List<CalendarHolidayItem> DayHolidays { get; set; } = [];
        public List<CalendarOperation> DayOperations { get; set; } = [];
        public Exception? DeleteError { get; init; }
        public int DayCalls { get; private set; }

        public Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([new("AllSchool", null, "Tüm okul"), new("Class", ClassId, "5A")]);
        public Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, CancellationToken cancellationToken = default)
        {
            var first = new DateOnly(month.Year, month.Month, 1);
            var days = Enumerable.Range(0, first.AddMonths(1).DayNumber - first.DayNumber)
                .Select(x => new CalendarDaySummary(first.AddDays(x), new(0, 0, 0, 0), [], [], 0, 0, 0)).ToArray();
            return Task.FromResult(new MonthlyCalendar(first, null, days));
        }
        public Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, CancellationToken cancellationToken = default)
        {
            DayCalls++;
            return Task.FromResult(new CalendarDayDetails(calendarDate, new(0, 0, 0, 0), [], DayOperations.ToArray(), DayHolidays.ToArray(), [], 0, 0, 0));
        }
        public Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Task.FromResult(new HolidayDetails(Guid.NewGuid(), request.Date, request.Name, request.HolidayType, request.Description,
                request.TransferBehavior, request.Scopes, request.DayCount > 1 ? Guid.NewGuid() : null, request.DayCount));
        }
        public Task DeleteHolidayAsync(Guid id, bool wholeRange, CancellationToken cancellationToken = default)
        {
            if (DeleteError is not null) throw DeleteError;
            Deleted.Add((id, wholeRange));
            return Task.CompletedTask;
        }
        public Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
