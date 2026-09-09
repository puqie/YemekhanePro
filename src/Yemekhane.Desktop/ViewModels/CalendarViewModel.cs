using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Calendar;
using Yemekhane.Desktop.Converters;
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

/// <summary>
/// Gun cekmecesindeki "Olaylar ve Islemler" satiri. API <see cref="CalendarOperation"/>
/// icinde ham kod tasir (Title="Trip", Detail="Delete"); ekranda Turkce ad ve
/// anlasilir aciklama gosterilir.
/// </summary>
public sealed record CalendarOperationDisplay(string Title, string? Detail);

/// <summary>
/// Gun cekmecesindeki tatil satiri. Silme iki adimli ve dugmeye ozel silahlanir: "Bu günü sil"
/// ile silahlanip "Tüm aralığı sil"e basmak araligi silmez, yeniden onay ister.
/// </summary>
public sealed class HolidayRowViewModel : ObservableObject
{
    private readonly CalendarViewModel owner;
    private bool? armedRange;

    public HolidayRowViewModel(CalendarHolidayItem item, CalendarViewModel owner, bool canManage)
    {
        Item = item; this.owner = owner; CanManage = canManage;
        DeleteDayCommand = new AsyncCommand(() => DeleteAsync(false), () => CanManage);
        DeleteRangeCommand = new AsyncCommand(() => DeleteAsync(true), () => CanManage && IsMultiDay);
        CancelDeleteCommand = new RelayCommand(() => Armed = null, () => IsDeleteArmed);
    }

    public CalendarHolidayItem Item { get; }
    public bool CanManage { get; }
    public string Title => Item.Name;
    public string Detail => string.Join(" · ", EnumTextConverter.Translate(Item.HolidayType, "HolidayType"),
        string.Join(", ", Item.Scopes.Select(owner.ScopeName)), "Hak davranışı: " + EnumTextConverter.Translate(Item.TransferBehavior, "TransferBehavior"));
    public bool IsMultiDay => Item.GroupDayCount > 1;
    public string RangeText => IsMultiDay ? $"{Item.GroupDayCount} günlük tatil aralığının bir günü" : "";
    public bool IsDeleteArmed => armedRange.HasValue;
    public string DeleteDayText => armedRange == false ? "Bu günü silmeyi onayla" : "Bu günü sil";
    public string DeleteRangeText => armedRange == true ? $"Tüm aralığı silmeyi onayla ({Item.GroupDayCount} gün)" : $"Tüm aralığı sil ({Item.GroupDayCount} gün)";
    public ICommand DeleteDayCommand { get; }
    public ICommand DeleteRangeCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    private bool? Armed
    {
        get => armedRange;
        set
        {
            if (armedRange == value) return;
            armedRange = value;
            Raise(nameof(IsDeleteArmed)); Raise(nameof(DeleteDayText)); Raise(nameof(DeleteRangeText));
            (CancelDeleteCommand as RelayCommand)?.Refresh();
        }
    }

    private async Task DeleteAsync(bool wholeRange)
    {
        if (armedRange != wholeRange) { Armed = wholeRange; return; }
        Armed = null;
        await owner.DeleteHolidayAsync(this, wholeRange);
    }
}

public sealed class CalendarViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly ICalendarApiClient api;
    private DateOnly month, selectedDate;
    private CalendarScopeOption? selectedScope, holidayScope, exceptionScope;
    private ClassKindFilterOption selectedClassKind = new("İlkokul (anasınıfı hariç)", Yemekhane.Domain.Entities.ClassKinds.Normal);
    private CalendarDayDetails? selectedDetails;
    private bool isLoading, isOffline, isDrawerOpen, isHolidayFormOpen, isExceptionFormOpen;
    private string? errorMessage, formMessage, infoMessage, pendingBehavior;
    private string holidayName = "", holidayType = "Official", transferBehavior = "Delete";
    private string exceptionType = "Special", exceptionBehavior = "Keep", exceptionDescription = "";
    private DateTime? holidayStart, holidayEnd;
    private DateOnly? pendingRangeEnd;

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
        OpenHolidayFormCommand = new RelayCommand(() =>
        {
            HolidayScope = ScopeForForm();
            // Aralik varsayilani secili gun: tek gunluk tatil icin ek tiklama gerekmez.
            HolidayStart = HolidayEnd = SelectedDate.ToDateTime(TimeOnly.MinValue);
            IsHolidayFormOpen = true; FormMessage = null; InfoMessage = null;
        }, () => CanManage);
        CloseHolidayFormCommand = new RelayCommand(() => IsHolidayFormOpen = false);
        CreateHolidayCommand = new AsyncCommand(CreateHolidayAsync, () => CanManage);
        OpenExceptionFormCommand = new RelayCommand(() => { ExceptionScope = ScopeForForm(); IsExceptionFormOpen = true; FormMessage = null; InfoMessage = null; }, () => CanManage);
        CloseExceptionFormCommand = new RelayCommand(() => IsExceptionFormOpen = false);
        CreateExceptionCommand = new AsyncCommand(CreateExceptionAsync, () => CanManage);
        OpenBulkCommand = new RelayCommand(OpenBulk, () => BulkWizard?.CanBulk == true);
        // Sihirbaz bir islem uygulayinca/geri alinca gun rozetleri ve acik cekmece ESKI kalmasin.
        if (BulkWizard is not null && CanManage) BulkWizard.Changed += (_, _) => _ = RefreshAfterCreateAsync();
    }

    public DateOnly Today { get; }
    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];
    public ObservableCollection<CalendarScopeOption> Scopes { get; } = [];
    public IReadOnlyList<string> DayNames { get; } = ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];
    // Kodlar API sozlesmesidir (HolidayService/CalendarService dogrular); ekranda EnumTextConverter ile Turkcelesir.
    public IReadOnlyList<string> HolidayTypes { get; } = ["Official", "Administrative", "Trip", "Other"];
    public IReadOnlyList<string> TransferBehaviors { get; } = ["Delete", "NextBusinessDay", "SpecifiedDate", "Forfeit"];
    public IReadOnlyList<string> ExceptionTypes { get; } = ["Trip", "Special", "ScheduleChange"];
    public IReadOnlyList<string> ExceptionBehaviors { get; } = ["Keep", "Cancel", "NextBusinessDay", "SpecifiedDate", "Forfeit"];
    public bool CanManage { get; }
    public BulkOperationWizardViewModel? BulkWizard { get; }
    public string MonthTitle => Turkish.TextInfo.ToTitleCase(month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", Turkish));
    public CalendarScopeOption? SelectedScope { get => selectedScope; set => Set(ref selectedScope, value); }

    /// <summary>
    /// Sinif turu suzgeci. Mutfaga verilen gunluk sayi ILKOKULUN sayisidir: anasinifi ayri
    /// ucretlendirildigi icin o rakama karismamalidir, bu yuzden varsayilan "İlkokul"dur.
    /// "İlkokul" secildiginde sinifi girilmemis ogrenciler de sayilir (yemege geliyorlarsa
    /// sayimdan dusmemeli); yalnizca anasinifi disarida kalir.
    /// </summary>
    public IReadOnlyList<ClassKindFilterOption> ClassKinds { get; } =
    [
        new("İlkokul (anasınıfı hariç)", Yemekhane.Domain.Entities.ClassKinds.Normal),
        new("Yalnızca anasınıfı", Yemekhane.Domain.Entities.ClassKinds.Preschool),
        new("Tümü", null)
    ];

    public ClassKindFilterOption SelectedClassKind
    {
        get => selectedClassKind;
        set { if (Set(ref selectedClassKind, value)) Raise(nameof(ClassKindHint)); }
    }

    /// <summary>Sayilarin kimi kapsadigini ekranda yazar; rakamin anlami gorunur olmali.</summary>
    public string ClassKindHint => SelectedClassKind.Value switch
    {
        Yemekhane.Domain.Entities.ClassKinds.Preschool => "Sayılar yalnızca anasınıfını kapsıyor.",
        null => "Sayılar ilkokul ve anasınıfını birlikte kapsıyor.",
        _ => "Sayılar anasınıfı hariç; sınıfı girilmemiş öğrenciler dahil."
    };
    public CalendarScopeOption? HolidayScope { get => holidayScope; set => Set(ref holidayScope, value); }
    public CalendarScopeOption? ExceptionScope { get => exceptionScope; set => Set(ref exceptionScope, value); }
    public DateOnly SelectedDate { get => selectedDate; private set { if (Set(ref selectedDate, value)) Raise(nameof(SelectedDateTitle)); } }
    public string SelectedDateTitle => SelectedDate.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy, dddd", Turkish);
    public CalendarDayDetails? SelectedDetails
    {
        get => selectedDetails;
        private set
        {
            if (!Set(ref selectedDetails, value)) return;
            RebuildHolidayRows();
            Raise(nameof(HasDayDetails)); Raise(nameof(SelectedOperations)); Raise(nameof(HasNoOperations)); Raise(nameof(HasHolidayRows));
        }
    }
    public bool HasDayDetails => SelectedDetails is not null;
    /// <summary>Gunun olaylari Turkce baslik/aciklamayla.</summary>
    public IReadOnlyList<CalendarOperationDisplay> SelectedOperations =>
        // Tatiller kendi bloklarinda (silme dugmeleriyle) listelenir; ayni satir iki kez gorunmesin.
        (SelectedDetails?.Operations ?? []).Where(x => x.Kind != "Holiday" || (SelectedDetails?.Holidays.Count ?? 0) == 0).Select(Describe).ToArray();
    public bool HasNoOperations => SelectedDetails is not null && SelectedDetails.Operations.Count == 0 && SelectedDetails.Holidays.Count == 0;
    /// <summary>Secili gunun tatilleri: tur, kapsam, davranis ve silme (bu gun / tum aralik).</summary>
    public ObservableCollection<HolidayRowViewModel> HolidayRows { get; } = [];
    public bool HasHolidayRows => HolidayRows.Count > 0;
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
    /// <summary>Tatil araligi: bayram/yariyil icin gun gun eklemek yerine baslangic-bitis secilir; form acilinca ikisi de secili gundur.</summary>
    public DateTime? HolidayStart { get => holidayStart; set { if (Set(ref holidayStart, value)) { Raise(nameof(HolidayDayCount)); Raise(nameof(HolidayRangeText)); } } }
    public DateTime? HolidayEnd { get => holidayEnd; set { if (Set(ref holidayEnd, value)) { Raise(nameof(HolidayDayCount)); Raise(nameof(HolidayRangeText)); } } }
    public int HolidayDayCount => HolidayStart is { } start && HolidayEnd is { } end && end.Date >= start.Date ? (end.Date - start.Date).Days + 1 : 0;
    public string HolidayRangeText => HolidayDayCount switch
    {
        0 => "Bitiş, başlangıçtan önce olamaz.",
        1 => "Tek gün.",
        var days => $"{days} gün: her gün ayrı kayıt olur, gerekirse tek hamlede silinir."
    };
    public string ExceptionType { get => exceptionType; set => Set(ref exceptionType, value); }
    public string ExceptionBehavior { get => exceptionBehavior; set => Set(ref exceptionBehavior, value); }
    public string ExceptionDescription { get => exceptionDescription; set => Set(ref exceptionDescription, value); }
    /// <summary>Form hatasi (kirmizi).</summary>
    public string? FormMessage { get => formMessage; private set => Set(ref formMessage, value); }
    /// <summary>
    /// Basari/bilgi metni (mavi). Tatil kaydi haklari KENDISI degistirmez; kullaniciya
    /// bunun bir sonraki adim oldugu soylenmezse "tatil ekledim ama haklar duruyor" olur.
    /// </summary>
    public string? InfoMessage { get => infoMessage; private set { if (Set(ref infoMessage, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => !string.IsNullOrWhiteSpace(InfoMessage);
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
            var result = await api.GetMonthAsync(month, FilterScope(), SelectedClassKind.Value);
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
        IsDrawerOpen = true; FormMessage = null; InfoMessage = null; pendingBehavior = null;
        try { SelectedDetails = await api.GetDayAsync(date, FilterScope(), SelectedClassKind.Value); }
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

    public void CloseDrawer() { IsDrawerOpen = false; IsHolidayFormOpen = false; IsExceptionFormOpen = false; InfoMessage = null; }

    private async Task ChangeMonthAsync(int months) { month = month.AddMonths(months); SelectedDate = month; Raise(nameof(MonthTitle)); await LoadAsync(); }
    private async Task GoTodayAsync() { month = new DateOnly(Today.Year, Today.Month, 1); SelectedDate = Today; Raise(nameof(MonthTitle)); await LoadAsync(); }
    private async Task CreateHolidayAsync()
    {
        FormMessage = null; InfoMessage = null;
        try
        {
            // Sunucu da dogrular; ama bos ad icin yolculuk sunucuya gitmeden de anlasilir olsun.
            if (string.IsNullOrWhiteSpace(HolidayName)) throw new InvalidOperationException("Tatil adı zorunludur (2-200 karakter).");
            var start = DateOnly.FromDateTime(HolidayStart ?? SelectedDate.ToDateTime(TimeOnly.MinValue));
            var end = DateOnly.FromDateTime(HolidayEnd ?? start.ToDateTime(TimeOnly.MinValue));
            if (end < start) throw new InvalidOperationException("Tatil bitiş tarihi başlangıçtan önce olamaz.");
            var scope = HolidayScope ?? Scopes.FirstOrDefault() ?? new("AllSchool", null, "Tüm okul");
            var created = await api.CreateHolidayAsync(new CreateHolidayRequest(start, HolidayName, HolidayType, null, TransferBehavior,
                [new HolidayScopeRequest(scope.ScopeType, scope.ScopeId)], end > start ? end : null));
            var behavior = TransferBehavior;
            IsHolidayFormOpen = false; HolidayName = "";
            // Form baska bir gune/aya yazdiysa cekmece ve takvim o gune gider; aksi halde eski gun gorunur kalirdi.
            if (start != SelectedDate)
            {
                SelectedDate = start;
                if (new DateOnly(start.Year, start.Month, 1) != month) { month = new DateOnly(start.Year, start.Month, 1); Raise(nameof(MonthTitle)); }
            }
            await RefreshAfterCreateAsync();
            pendingBehavior = behavior; pendingRangeEnd = end > start ? end : null;
            var prefix = created.DayCount > 1
                ? $"Tatil kaydedildi: {created.DayCount} gün ({start.ToDateTime(TimeOnly.MinValue).ToString("d MMM", Turkish)} – {end.ToDateTime(TimeOnly.MinValue).ToString("d MMM", Turkish)})."
                : "Tatil kaydedildi.";
            InfoMessage = AfterCreateMessage(prefix, behavior);
        }
        catch (Exception ex) { FormMessage = Friendly(ex, "Tatil oluşturulamadı."); }
    }
    private async Task CreateExceptionAsync()
    {
        FormMessage = null; InfoMessage = null;
        try
        {
            var scope = ExceptionScope ?? Scopes.FirstOrDefault() ?? new("AllSchool", null, "Tüm okul");
            await api.CreateExceptionAsync(new CreateScheduleExceptionRequest(SelectedDate, ExceptionType, scope.ScopeType,
                scope.ScopeId, null, ExceptionBehavior, null, string.IsNullOrWhiteSpace(ExceptionDescription) ? null : ExceptionDescription, Guid.Empty));
            // Istisna davranisi "Keep" ise hak degismez; "Cancel" sihirbazdaki "Delete"ye karsilik gelir.
            var behavior = ExceptionBehavior == "Cancel" ? "Delete" : ExceptionBehavior;
            IsExceptionFormOpen = false; ExceptionDescription = ""; await RefreshAfterCreateAsync();
            // "Koru" hak degistirmez; daha once bir tatil davranisi bekliyorsa o korunur.
            if (behavior != "Keep") pendingBehavior = behavior;
            InfoMessage = behavior == "Keep" ? "Özel istisna kaydedildi; haklar korunur." : AfterCreateMessage("Özel istisna kaydedildi.", behavior);
        }
        catch (Exception ex) { FormMessage = Friendly(ex, "Özel istisna oluşturulamadı."); }
    }
    private string AfterCreateMessage(string prefix, string behavior)
    {
        var active = SelectedDetails?.Entitlements.Quantity ?? 0;
        return active == 0 ? prefix + " Bu güne ait aktif hak yok."
            : $"{prefix} Bu güne ait {active:N0} aktif hak henüz değişmedi; \"{EnumTextConverter.Translate(behavior, "TransferBehavior")}\" davranışını uygulamak için \"Hakediş etkilerini toplu uygula\" düğmesini kullanın.";
    }
    private async Task RefreshAfterCreateAsync()
    {
        await LoadAsync();
        if (IsDrawerOpen)
        {
            // SelectDayAsync bilgi metnini temizler; yenileme sirasinda korunur.
            var info = InfoMessage; var behavior = pendingBehavior;
            await SelectDayAsync(SelectedDate);
            InfoMessage = info; pendingBehavior = behavior;
        }
    }
    private void OpenBulk() { BulkWizard?.Preset(SelectedDate, transferBehavior: pendingBehavior, endDate: pendingRangeEnd); BulkWizard?.OpenCommand.Execute(null); }

    internal async Task DeleteHolidayAsync(HolidayRowViewModel row, bool wholeRange)
    {
        FormMessage = null; InfoMessage = null;
        try
        {
            await api.DeleteHolidayAsync(row.Item.Id, wholeRange);
            await RefreshAfterCreateAsync();
            InfoMessage = wholeRange && row.Item.GroupDayCount > 1
                ? $"{row.Item.GroupDayCount} günlük tatil aralığı silindi: {row.Item.Name}. Daha önce uygulanmış hak değişiklikleri geri alınmadı; gerekiyorsa sihirbazdan geri alın."
                : $"Tatil silindi: {row.Item.Name}. Daha önce uygulanmış hak değişiklikleri geri alınmadı.";
        }
        catch (Exception ex) { FormMessage = Friendly(ex, "Tatil silinemedi."); }
    }

    internal string ScopeName(HolidayScopeRequest scope) => scope.ScopeType == "AllSchool" ? "Tüm okul"
        : Scopes.FirstOrDefault(x => x.ScopeType == scope.ScopeType && x.ScopeId == scope.ScopeId)?.Name
          ?? (scope.ScopeType == "Class" ? "Sınıf" : "Grup");

    private void RebuildHolidayRows()
    {
        HolidayRows.Clear();
        foreach (var item in SelectedDetails?.Holidays ?? []) HolidayRows.Add(new HolidayRowViewModel(item, this, CanManage));
    }
    private CalendarScopeOption? FilterScope() => SelectedScope?.ScopeType == "AllSchool" ? null : SelectedScope;
    private CalendarScopeOption ScopeForForm() => SelectedScope ?? Scopes.FirstOrDefault() ?? new("AllSchool", null, "Tüm okul");
    private void HandleError(Exception ex) { IsOffline = ex is HttpRequestException or TaskCanceledException or InvalidDataException; ErrorMessage = Friendly(ex, "Takvim verisi alınamadı."); }
    // ApiRequestException sunucunun Turkce ProblemDetails basligini tasir; oldugu gibi gosterilir.
    private static string Friendly(Exception ex, string fallback) => ex is LoginRequiredException ? "Takvim için calendar.manage yetkili oturum gerekiyor."
        : ex is InvalidOperationException or ApiRequestException ? ex.Message : fallback;

    private static CalendarOperationDisplay Describe(CalendarOperation operation) => operation.Kind switch
    {
        "Holiday" => new($"Tatil · {operation.Title}", "Hak davranışı: " + EnumTextConverter.Translate(operation.Detail, "TransferBehavior")),
        "Exception" => new($"İstisna · {EnumTextConverter.Translate(operation.Title, "ExceptionType")}", operation.Detail),
        "Leave" => new($"İzin · {operation.Title}", operation.Detail),
        "TransferIn" or "TransferOut" => new(operation.Title, operation.Quantity > 0 ? $"{operation.Quantity:N0} hak · {operation.Detail}" : operation.Detail),
        _ => new(operation.Title, operation.Detail)
    };
}

/// <summary>Takvim sinif turu suzgeci secenegi: ekranda Turkce etiket, sunucuya anahtar gider.</summary>
public sealed record ClassKindFilterOption(string Label, string? Value)
{
    public override string ToString() => Label;
}
