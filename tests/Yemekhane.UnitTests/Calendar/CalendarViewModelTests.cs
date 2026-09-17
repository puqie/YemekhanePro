using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Students;
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

    /// <summary>Bos tatil adi sunucuya gitmeden Turkce mesajla reddedilir; sunucu basligi (ApiRequestException) da oldugu gibi gosterilir.</summary>
    [Fact]
    public async Task BosVeKisaTatilAdiTurkceMesajlaReddedilir()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14)); vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "   "; vm.CreateHolidayCommand.Execute(null); await Until(() => vm.FormMessage is not null);
        Assert.Contains("Tatil adı", vm.FormMessage); Assert.Equal(0, api.HolidayCalls); Assert.True(vm.IsHolidayFormOpen);

        api.HolidayError = new ApiRequestException("Tatil adı 2-200 karakter olmalıdır.", System.Net.HttpStatusCode.BadRequest);
        vm.HolidayName = "X"; vm.CreateHolidayCommand.Execute(null); await Until(() => vm.FormMessage == "Tatil adı 2-200 karakter olmalıdır.");
        Assert.True(vm.IsHolidayFormOpen); Assert.False(vm.IsOffline);
    }

    /// <summary>
    /// Tatil kaydi haklari KENDISI degistirmez: kullaniciya sonraki adim soylenir ve
    /// "toplu uygula" sihirbazi tatilin davranisi + secili gunle acilir.
    /// </summary>
    [Fact]
    public async Task TatilSonrasiBilgiMetniVeSihirbazOnAyari()
    {
        var api = new FakeApi { DayQuantity = 10 }; var wizard = new BulkOperationWizardViewModel(new FakeBulkApi(), ["entitlements.bulk", "calendar.manage"]);
        await wizard.InitializeAsync();
        var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8), wizard); await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14)); vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayName = "Bayram"; vm.TransferBehavior = "NextBusinessDay";
        vm.CreateHolidayCommand.Execute(null); await Until(() => vm.HasInfo);
        Assert.Contains("10 aktif hak", vm.InfoMessage); Assert.Contains("toplu uygula", vm.InfoMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sonraki iş gününe aktar", vm.InfoMessage);
        vm.OpenBulkCommand.Execute(null);
        Assert.True(wizard.IsOpen); Assert.Equal("NextBusinessDay", wizard.TransferBehavior);
        Assert.Equal(new DateTime(2026, 9, 14), wizard.StartsOn);
    }

    /// <summary>Gun cekmecesindeki olaylar ham kodla (Title="Trip", Detail="Delete") degil Turkce gosterilir.</summary>
    [Fact]
    public async Task OlaylarTurkceGosterilir()
    {
        var api = new FakeApi
        {
            DayOperations = [new(Guid.NewGuid(), "Holiday", "Bayram", "Delete"), new(Guid.NewGuid(), "Exception", "Trip", "Müze"),
                new(Guid.NewGuid(), "TransferOut", "Aktarım çıkışı", "Tatil", 3)]
        };
        var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14));
        Assert.False(vm.HasNoOperations);
        Assert.Equal("Tatil · Bayram", vm.SelectedOperations[0].Title); Assert.Equal("Hak davranışı: Hakları iptal et", vm.SelectedOperations[0].Detail);
        Assert.Equal("İstisna · Gezi", vm.SelectedOperations[1].Title); Assert.Equal("Müze", vm.SelectedOperations[1].Detail);
        Assert.Equal("3 hak · Tatil", vm.SelectedOperations[2].Detail);
    }

    private sealed class FakeBulkApi : IBulkOperationApiClient
    {
        public Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([new("AllSchool", null, "Tüm okul")]);
        public Task<IReadOnlyList<Yemekhane.Application.Meals.MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Yemekhane.Application.Meals.MealTypeDetails>>([]);
        public Task<Yemekhane.Application.BulkOperations.BulkOperationPreview> PreviewAsync(Yemekhane.Application.BulkOperations.BulkCalendarOperationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Yemekhane.Application.BulkOperations.BulkOperationResult> ApplyAsync(Yemekhane.Application.BulkOperations.ApplyBulkOperationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Yemekhane.Application.BulkOperations.BulkOperationHistoryPage> HistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Yemekhane.Application.BulkOperations.BulkOperationHistoryPage([], 1, 30, 0));
        public Task<Yemekhane.Application.BulkOperations.UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    /// <summary>
    /// OGRENCIYE OZEL TATIL: kapsam "Seçili öğrenciler" olunca arama + coklu secim acilir; iki ayri
    /// aramanin secimleri KORUNUR; "Oluştur" secili ogrencilere toplu izin acar, tatil ucunu CAGIRMAZ;
    /// basarisiz ogrenci adiyla bilgi satirinda yazilir; secim temizlenir.
    /// </summary>
    [Fact]
    public async Task StudentScopedHolidayCreatesLeavesForSelectedStudentsAcrossSearches()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        Assert.Contains(vm.HolidayScopes, x => x.ScopeType == "Students");
        Assert.DoesNotContain(vm.Scopes, x => x.ScopeType == "Students");
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14)); vm.OpenHolidayFormCommand.Execute(null);
        Assert.False(vm.IsStudentHolidayScope);
        vm.HolidayScope = CalendarViewModel.StudentsScope;
        Assert.True(vm.IsStudentHolidayScope); Assert.False(vm.IsGeneralHolidayScope);

        vm.HolidayStudentSearch = "a"; vm.SearchHolidayStudentsCommand.Execute(null);
        await Until(() => vm.HolidayPickerMessage is not null);
        Assert.Contains("en az 2 karakter", vm.HolidayPickerMessage);
        vm.HolidayStudentSearch = "ayşe"; vm.SearchHolidayStudentsCommand.Execute(null);
        await Until(() => vm.HolidayStudentPicker.Count == 1);
        Assert.True(vm.HolidayStudentPicker[0].IsSelected); // tek sonuc kendiliginden secilir
        vm.HolidayStudentSearch = "5/A"; vm.SearchHolidayStudentsCommand.Execute(null);
        await Until(() => vm.HolidayStudentPicker.Count == 3);
        Assert.Equal(1, vm.SelectedHolidayStudentCount); // onceki secim korundu, yeni satirlar secili degil
        vm.HolidayStudentPicker.Single(x => x.Name == "CAN YILMAZ").IsSelected = true;
        Assert.Equal("2 öğrenci seçili", vm.SelectedHolidayStudentsText);

        vm.HolidayName = "Gezi"; vm.HolidayEnd = new DateTime(2026, 9, 16); vm.LeaveBehavior = "Cancel";
        api.LeaveFailures = [new BulkLeaveFailure(api.Can.Id, "Aktarım günü bulunamadı.")];
        vm.CreateHolidayCommand.Execute(null);
        await Until(() => api.LastLeaves is not null && !vm.IsHolidayFormOpen);

        Assert.Equal(0, api.HolidayCalls);
        Assert.Equal([api.Ayse.Id, api.Can.Id], api.LastLeaves!.StudentIds);
        Assert.Equal(new DateOnly(2026, 9, 14), api.LastLeaves.StartsOn); Assert.Equal(new DateOnly(2026, 9, 16), api.LastLeaves.EndsOn);
        Assert.Equal("Cancel", api.LastLeaves.EntitlementBehavior); Assert.Equal("Gezi", api.LastLeaves.Description); Assert.Equal("Tatil", api.LastLeaves.LeaveType);
        Assert.Contains("1 öğrenciye 14 Eyl – 16 Eyl için öğrenciye özel tatil", vm.InfoMessage);
        Assert.Contains("CAN YILMAZ: Aktarım günü bulunamadı.", vm.InfoMessage);
        Assert.Empty(vm.HolidayStudentPicker); Assert.Equal(0, vm.SelectedHolidayStudentCount);
    }

    [Fact]
    public async Task StudentScopedHolidayWithoutSelectionIsRefusedBeforeCallingTheServer()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        await vm.SelectDayAsync(new DateOnly(2026, 9, 14)); vm.OpenHolidayFormCommand.Execute(null);
        vm.HolidayScope = CalendarViewModel.StudentsScope; vm.HolidayName = "Gezi";
        vm.CreateHolidayCommand.Execute(null);
        await Until(() => vm.FormMessage is not null);
        Assert.Contains("en az bir öğrenci", vm.FormMessage);
        Assert.Null(api.LastLeaves); Assert.True(vm.IsHolidayFormOpen);
    }

    /// <summary>Gun cekmecesi izinli ogrencileri adiyla listeler; yalnizca "Keep" izin silinebilir.</summary>
    [Fact]
    public async Task DayDrawerListsLeavesByNameAndDeletesOnlyKeepLeaves()
    {
        var api = new FakeApi(); var vm = new CalendarViewModel(api, ["calendar.manage"], new DateOnly(2026, 9, 8)); await vm.InitializeAsync();
        var keep = new LeaveListRow(Guid.NewGuid(), api.Ayse.Id, "11", "AYŞE ÇELİK", "5/A", new(2026, 9, 14), new(2026, 9, 14), "Tatil", "Doktor", "Keep");
        var cancel = new LeaveListRow(Guid.NewGuid(), api.Can.Id, "12", "CAN YILMAZ", null, new(2026, 9, 14), new(2026, 9, 16), "Tatil", null, "Cancel");
        api.Leaves = [keep, cancel];

        await vm.SelectDayAsync(new DateOnly(2026, 9, 14));

        Assert.True(vm.HasLeaveRows);
        Assert.Equal(["AYŞE ÇELİK · 5/A · No 11", "CAN YILMAZ · No 12"], vm.LeaveRows.Select(x => x.Title));
        Assert.Contains("Doktor", vm.LeaveRows[0].Detail); Assert.Contains("haklar korundu", vm.LeaveRows[0].Detail);
        Assert.Contains("14 Eyl – 16 Eyl", vm.LeaveRows[1].Detail); Assert.Contains("iptal edildi", vm.LeaveRows[1].Detail);
        Assert.True(vm.LeaveRows[0].CanDelete); Assert.False(vm.LeaveRows[1].CanDelete);
        Assert.True(vm.DeleteLeaveCommand.CanExecute(vm.LeaveRows[0])); Assert.False(vm.DeleteLeaveCommand.CanExecute(vm.LeaveRows[1]));

        api.Leaves = [cancel];
        vm.DeleteLeaveCommand.Execute(vm.LeaveRows[0]);
        await Until(() => api.DeletedLeaves.Count == 1 && vm.LeaveRows.Count == 1);
        Assert.Equal(keep.Id, api.DeletedLeaves[0]);
        Assert.Contains("AYŞE ÇELİK için izin kaydı silindi", vm.InfoMessage);
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
        public Exception? HolidayError; public int DayQuantity; public List<CalendarOperation> DayOperations = [];
        public Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([all, new("Class", Guid.NewGuid(), "5A"), new("Group", Guid.NewGuid(), "Sporcular")]);
        public async Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, string? classKind = null, CancellationToken cancellationToken = default)
        {
            if (MonthGate is not null) await MonthGate.Task.WaitAsync(cancellationToken);
            if (Fail) throw new HttpRequestException(); MonthCalls++; LastScope = scope; var first = new DateOnly(month.Year, month.Month, 1);
            var days = Enumerable.Range(0, first.AddMonths(1).DayNumber - first.DayNumber).Select(x => new CalendarDaySummary(first.AddDays(x), new(0, 0, 0, 0), [], [], 0, 0, 0)).ToArray();
            return new MonthlyCalendar(first, scope is null ? null : new CalendarScope(scope.ScopeType, scope.ScopeId), days);
        }
        public Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, string? classKind = null, CancellationToken cancellationToken = default)
        { DayCalls++; return Task.FromResult(new CalendarDayDetails(calendarDate, new(DayQuantity, DayQuantity, DayQuantity, 0), [], DayOperations, [], [], 0, 0, 0)); }
        public Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default)
        {
            if (HolidayError is not null) throw HolidayError;
            HolidayCalls++; LastHoliday = request; return Task.FromResult(new HolidayDetails(Guid.NewGuid(), request.Date, request.Name, request.HolidayType, request.Description, request.TransferBehavior, request.Scopes));
        }
        public StudentListItem Ayse { get; } = Row("11", "AYŞE", "ÇELİK", "5/A");
        public StudentListItem Can { get; } = Row("12", "CAN", "YILMAZ", "5/A");
        public StudentListItem Ela { get; } = Row("13", "ELA", "DEMİR", "5/A");
        public CreateBulkLeaveRequest? LastLeaves; public List<BulkLeaveFailure> LeaveFailures = []; public List<LeaveListRow> Leaves = []; public List<Guid> DeletedLeaves = [];
        private static StudentListItem Row(string no, string first, string last, string cls) =>
            new(Guid.NewGuid(), no, null, first, last, cls, null, null, null, true, 0, false, null);
        public Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default)
        {
            var normalized = TurkishSearchText.Normalize(term);
            var items = new[] { Ayse, Can, Ela }.Where(x => TurkishSearchText.Normalize(x.FirstName + " " + x.LastName + " " + x.ClassName).Contains(normalized, StringComparison.Ordinal)).ToList();
            return Task.FromResult(new PagedResult<StudentListItem>(items, 1, 100, items.Count));
        }
        public Task<BulkLeaveResult> CreateLeavesAsync(CreateBulkLeaveRequest request, CancellationToken cancellationToken = default)
        {
            LastLeaves = request;
            return Task.FromResult(new BulkLeaveResult(request.StudentIds.Count - LeaveFailures.Count, LeaveFailures));
        }
        public Task<IReadOnlyList<LeaveListRow>> LeavesInRangeAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LeaveListRow>>(Leaves.Where(x => x.StartsOn <= rangeEnd && x.EndsOn >= rangeStart).ToList());
        public Task DeleteLeaveAsync(Guid id, CancellationToken cancellationToken = default) { DeletedLeaves.Add(id); return Task.CompletedTask; }
        public List<(Guid Id, bool WholeRange)> Deleted = [];
        public Task DeleteHolidayAsync(Guid id, bool wholeRange, CancellationToken cancellationToken = default) { Deleted.Add((id, wholeRange)); return Task.CompletedTask; }
        public Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalendarExceptionItem(Guid.NewGuid(), request.ExceptionType, request.ScopeType, request.ScopeId, request.EntitlementBehavior, request.TargetDate, request.Description));
    }
}
