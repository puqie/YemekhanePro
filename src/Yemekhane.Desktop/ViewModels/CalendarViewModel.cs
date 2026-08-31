using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Calendar;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class CalendarDayViewModel(CalendarDaySummary value, bool currentMonth, DateOnly today) : ObservableObject
{
    private bool isSelected;
    public CalendarDaySummary Value { get; } = value;
    public DateOnly Date => Value.Date;
    public string DayNumber => Date.Day.ToString(CultureInfo.InvariantCulture);
    public bool IsCurrentMonth { get; } = currentMonth;
    public bool IsToday { get; } = value.Date == today;
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    public bool HasHoliday => Value.Holidays.Count > 0;
    public bool HasTrip => Value.Exceptions.Any(x => x.ExceptionType.Equals("Trip", StringComparison.OrdinalIgnoreCase) || x.ExceptionType.Contains("Gezi", StringComparison.OrdinalIgnoreCase));
    public bool HasSpecial => Value.Exceptions.Any(x => !x.ExceptionType.Equals("Trip", StringComparison.OrdinalIgnoreCase) && !x.ExceptionType.Contains("Gezi", StringComparison.OrdinalIgnoreCase));
    public bool HasLeave => Value.LeaveCount > 0;
    public bool HasMeals => Value.Entitlements.Quantity > 0;
    public string MealText => $"{Value.Entitlements.StudentCount} öğrenci · {Value.Entitlements.Used}/{Value.Entitlements.Quantity}";
    public string HolidayText => Value.Holidays.FirstOrDefault()?.Name ?? "Tatil";
    public string TransferText => Value.TransferInCount + Value.TransferOutCount == 0 ? "" : $"Aktarım +{Value.TransferInCount} / -{Value.TransferOutCount}";
}

public sealed class CalendarViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly ICalendarApiClient api;
    private DateOnly month, selectedDate;
    private CalendarScopeOption? selectedScope, holidayScope, exceptionScope;
    private CalendarDayDetails? selectedDetails;
    private bool isLoading, isOffline, isDrawerOpen, isHolidayFormOpen, isExceptionFormOpen;
    private string? errorMessage, formMessage;
    private string holidayName = "", holidayType = "Official", transferBehavior = "Delete";
    private string exceptionType = "Special", exceptionBehavior = "Keep", exceptionDescription = "";

    public CalendarViewModel(ICalendarApiClient api, IEnumerable<string> permissions, DateOnly? today = null,
        BulkOperationWizardViewModel? bulkWizard = null)
    {
        this.api = api; BulkWizard = bulkWizard; Today = today ?? DateOnly.FromDateTime(DateTime.Today); month = new DateOnly(Today.Year, Today.Month, 1); selectedDate = Today;
        CanManage = permissions.Contains("calendar.manage", StringComparer.Ordinal);
        PreviousMonthCommand = new AsyncCommand(() => ChangeMonthAsync(-1), () => CanManage);
        NextMonthCommand = new AsyncCommand(() => ChangeMonthAsync(1), () => CanManage);
        TodayCommand = new AsyncCommand(GoTodayAsync, () => CanManage);
        RefreshCommand = new AsyncCommand(LoadAsync, () => CanManage);
        ApplyScopeCommand = new AsyncCommand(LoadAsync, () => CanManage);
        SelectDayCommand = new RelayCommand<CalendarDayViewModel>(item => _ = SelectDayAsync(item.Date));
        CloseDrawerCommand = new RelayCommand(CloseDrawer);
        OpenHolidayFormCommand = new RelayCommand(() => { HolidayScope = ScopeForForm(); IsHolidayFormOpen = true; FormMessage = null; }, () => CanManage);
        CloseHolidayFormCommand = new RelayCommand(() => IsHolidayFormOpen = false);
        CreateHolidayCommand = new AsyncCommand(CreateHolidayAsync, () => CanManage);
        OpenExceptionFormCommand = new RelayCommand(() => { ExceptionScope = ScopeForForm(); IsExceptionFormOpen = true; FormMessage = null; }, () => CanManage);
        CloseExceptionFormCommand = new RelayCommand(() => IsExceptionFormOpen = false);
        CreateExceptionCommand = new AsyncCommand(CreateExceptionAsync, () => CanManage);
        OpenBulkCommand = new RelayCommand(OpenBulk, () => BulkWizard?.CanBulk == true);
    }

    public DateOnly Today { get; }
    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];
    public ObservableCollection<CalendarScopeOption> Scopes { get; } = [];
    public IReadOnlyList<string> DayNames { get; } = ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];
    public IReadOnlyList<string> HolidayTypes { get; } = ["Official", "Administrative", "Trip", "Other"];
    public IReadOnlyList<string> TransferBehaviors { get; } = ["Delete", "NextBusinessDay", "SpecifiedDate", "Forfeit"];
    public IReadOnlyList<string> ExceptionTypes { get; } = ["Trip", "Special", "ScheduleChange"];
    public IReadOnlyList<string> ExceptionBehaviors { get; } = ["Keep", "Cancel", "NextBusinessDay", "SpecifiedDate", "Forfeit"];
    public bool CanManage { get; }
    public BulkOperationWizardViewModel? BulkWizard { get; }
    public string MonthTitle => Turkish.TextInfo.ToTitleCase(month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", Turkish));
    public CalendarScopeOption? SelectedScope { get => selectedScope; set => Set(ref selectedScope, value); }
    public CalendarScopeOption? HolidayScope { get => holidayScope; set => Set(ref holidayScope, value); }
    public CalendarScopeOption? ExceptionScope { get => exceptionScope; set => Set(ref exceptionScope, value); }
    public DateOnly SelectedDate { get => selectedDate; private set { if (Set(ref selectedDate, value)) Raise(nameof(SelectedDateTitle)); } }
    public string SelectedDateTitle => SelectedDate.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy, dddd", Turkish);
    public CalendarDayDetails? SelectedDetails { get => selectedDetails; private set { if (Set(ref selectedDetails, value)) Raise(nameof(HasDayDetails)); } }
    public bool HasDayDetails => SelectedDetails is not null;
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) Raise(nameof(IsEmpty)); } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) { Raise(nameof(HasError)); Raise(nameof(IsEmpty)); } } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && Days.All(x => !x.HasMeals && !x.HasHoliday && !x.HasLeave && !x.HasSpecial && !x.HasTrip);
    public bool IsDrawerOpen { get => isDrawerOpen; private set => Set(ref isDrawerOpen, value); }
    public bool IsHolidayFormOpen { get => isHolidayFormOpen; private set => Set(ref isHolidayFormOpen, value); }
    public bool IsExceptionFormOpen { get => isExceptionFormOpen; private set => Set(ref isExceptionFormOpen, value); }
    public string HolidayName { get => holidayName; set => Set(ref holidayName, value); }
    public string HolidayType { get => holidayType; set => Set(ref holidayType, value); }
    public string TransferBehavior { get => transferBehavior; set => Set(ref transferBehavior, value); }
    public string ExceptionType { get => exceptionType; set => Set(ref exceptionType, value); }
    public string ExceptionBehavior { get => exceptionBehavior; set => Set(ref exceptionBehavior, value); }
    public string ExceptionDescription { get => exceptionDescription; set => Set(ref exceptionDescription, value); }
    public string? FormMessage { get => formMessage; private set => Set(ref formMessage, value); }
    public ICommand PreviousMonthCommand { get; }
    public ICommand NextMonthCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ApplyScopeCommand { get; }
    public ICommand SelectDayCommand { get; }
    public ICommand CloseDrawerCommand { get; }
    public ICommand OpenHolidayFormCommand { get; }
    public ICommand CloseHolidayFormCommand { get; }
    public ICommand CreateHolidayCommand { get; }
    public ICommand OpenExceptionFormCommand { get; }
    public ICommand CloseExceptionFormCommand { get; }
    public ICommand CreateExceptionCommand { get; }
    public ICommand OpenBulkCommand { get; }

    public async Task InitializeAsync()
    {
        if (!CanManage) return;
        try
        {
            foreach (var item in await api.GetScopesAsync()) Scopes.Add(item);
            SelectedScope = Scopes.FirstOrDefault(); HolidayScope = SelectedScope; ExceptionScope = SelectedScope;
            await LoadAsync();
        }
        catch (Exception ex) { HandleError(ex); }
    }

    public async Task LoadAsync()
    {
        IsLoading = true; IsOffline = false; ErrorMessage = null;
        try
        {
            var result = await api.GetMonthAsync(month, FilterScope());
            var byDate = result.Days.ToDictionary(x => x.Date); Days.Clear();
            var first = month; var offset = ((int)first.DayOfWeek + 6) % 7; var gridStart = first.AddDays(-offset);
            for (var index = 0; index < 42; index++)
            {
                var date = gridStart.AddDays(index); byDate.TryGetValue(date, out var value);
                value ??= new CalendarDaySummary(date, new(0, 0, 0, 0), [], [], 0, 0, 0);
                Days.Add(new CalendarDayViewModel(value, date.Month == month.Month && date.Year == month.Year, Today) { IsSelected = date == SelectedDate });
            }
            Raise(nameof(IsEmpty));
        }
        catch (Exception ex) { HandleError(ex); }
        finally { IsLoading = false; }
    }

    public async Task SelectDayAsync(DateOnly date)
    {
        SelectedDate = date; foreach (var item in Days) item.IsSelected = item.Date == date;
        IsDrawerOpen = true; FormMessage = null;
        try { SelectedDetails = await api.GetDayAsync(date, FilterScope()); }
        catch (Exception ex) { SelectedDetails = null; HandleError(ex); }
    }

    public async Task MoveSelectionAsync(int days)
    {
        var date = SelectedDate.AddDays(days);
        if (date.Month != month.Month || date.Year != month.Year) { month = new DateOnly(date.Year, date.Month, 1); Raise(nameof(MonthTitle)); await LoadAsync(); }
        await SelectDayAsync(date);
    }

    public async Task NavigateToAsync(DateOnly date)
    {
        month = new DateOnly(date.Year, date.Month, 1); SelectedDate = date; Raise(nameof(MonthTitle));
        await LoadAsync(); await SelectDayAsync(date);
    }

    public void CloseDrawer() { IsDrawerOpen = false; IsHolidayFormOpen = false; IsExceptionFormOpen = false; }

    private async Task ChangeMonthAsync(int months) { month = month.AddMonths(months); SelectedDate = month; Raise(nameof(MonthTitle)); await LoadAsync(); }
    private async Task GoTodayAsync() { month = new DateOnly(Today.Year, Today.Month, 1); SelectedDate = Today; Raise(nameof(MonthTitle)); await LoadAsync(); }
    private async Task CreateHolidayAsync()
    {
        FormMessage = null;
        try
        {
            var scope = HolidayScope ?? Scopes.FirstOrDefault() ?? new("AllSchool", null, "Tüm okul");
            await api.CreateHolidayAsync(new CreateHolidayRequest(SelectedDate, HolidayName, HolidayType, null, TransferBehavior,
                [new HolidayScopeRequest(scope.ScopeType, scope.ScopeId)]));
            IsHolidayFormOpen = false; HolidayName = ""; await RefreshAfterCreateAsync();
        }
        catch (Exception ex) { FormMessage = Friendly(ex, "Tatil oluşturulamadı."); }
    }
    private async Task CreateExceptionAsync()
    {
        FormMessage = null;
        try
        {
            var scope = ExceptionScope ?? Scopes.FirstOrDefault() ?? new("AllSchool", null, "Tüm okul");
            await api.CreateExceptionAsync(new CreateScheduleExceptionRequest(SelectedDate, ExceptionType, scope.ScopeType,
                scope.ScopeId, null, ExceptionBehavior, null, string.IsNullOrWhiteSpace(ExceptionDescription) ? null : ExceptionDescription, Guid.Empty));
            IsExceptionFormOpen = false; ExceptionDescription = ""; await RefreshAfterCreateAsync();
        }
        catch (Exception ex) { FormMessage = Friendly(ex, "Özel istisna oluşturulamadı."); }
    }
    private async Task RefreshAfterCreateAsync() { await LoadAsync(); await SelectDayAsync(SelectedDate); }
    private void OpenBulk() { BulkWizard?.Preset(SelectedDate); BulkWizard?.OpenCommand.Execute(null); }
    private CalendarScopeOption? FilterScope() => SelectedScope?.ScopeType == "AllSchool" ? null : SelectedScope;
    private CalendarScopeOption ScopeForForm() => SelectedScope ?? Scopes.FirstOrDefault() ?? new("AllSchool", null, "Tüm okul");
    private void HandleError(Exception ex) { IsOffline = ex is HttpRequestException or TaskCanceledException or InvalidDataException; ErrorMessage = Friendly(ex, "Takvim verisi alınamadı."); }
    private static string Friendly(Exception ex, string fallback) => ex is LoginRequiredException ? "Takvim için calendar.manage yetkili oturum gerekiyor." : ex is InvalidOperationException ? ex.Message : fallback;
}
