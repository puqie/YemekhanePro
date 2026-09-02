using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Input;
using Yemekhane.Application.Reports;
using Yemekhane.Desktop.Converters;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

[Flags]
public enum ReportFilters
{
    None = 0, Student = 1, Card = 2, Name = 4, Organization = 8, Meal = 16, Device = 32,
    Decision = 64, Status = 128,
    /// <summary>Baslangic/bitis tarihi. Sicil Listesi'nde yok: kayit tarihi filtresi listeyi bos gosterirdi.</summary>
    Date = 256,
    /// <summary>Aktif / Pasif / Tumu acilir kutusu (Sicil Listesi). Serbest metin "Durum" yerine gecer.</summary>
    ActiveState = 512
}

/// <summary>Rapor turu; <paramref name="Subtitle"/> listede adin altinda kucuk aciklama olarak gorunur.</summary>
public sealed record ReportTypeOption(ReportType Type, string Name, ReportFilters Filters, string Subtitle = "");

/// <summary>Sicil Listesi'nin Aktif / Pasif secenegi; Value sunucuya giden durum kodu (null = Tumu).</summary>
public sealed record ReportStateOption(string Name, string? Value);

public sealed class ReportColumnViewModel : ObservableObject
{
    private bool isVisible = true;
    private int displayIndex;
    private double width;

    public ReportColumnViewModel(string key, string header, string? sortKey, double width)
    { Key = key; Header = header; SortKey = sortKey; this.width = width; }
    public string Key { get; }
    public string Header { get; }
    public string? SortKey { get; }
    public bool IsVisible { get => isVisible; set => Set(ref isVisible, value); }
    public int DisplayIndex { get => displayIndex; set => Set(ref displayIndex, value); }
    public double Width { get => width; set => Set(ref width, value); }
}

public sealed class ReportGridRow
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();
    private readonly ReportRow source;
    public ReportGridRow(ReportRow source) { this.source = source; Source = source; }
    public ReportRow Source { get; }
    // Milisaniye (.fff) kaldirildi: "dd.MM.yyyy HH:mm:ss.fff" 23 karakter tutuyordu
    // ve 145px'lik TARIH sutununa sigmadigi icin "02.09.2026 11:58:00.(" seklinde,
    // karakterin ortasindan kesiliyordu -- raporun asil sorusu olan "ne zaman oldu"
    // hicbir satirda okunamiyordu. Saniye bir yemekhane gecisinde fazlasiyla yeterli.
    public string Date => source.Timestamp.HasValue
        ? TimeZoneInfo.ConvertTime(source.Timestamp.Value, Istanbul).ToString("dd.MM.yyyy HH:mm:ss", Turkish)
        : source.ReportDate?.ToString("dd.MM.yyyy", Turkish) ?? "";
    public string StudentNo => source.StudentNo ?? "";
    public string CardNo => source.CardNo ?? "";
    public string Name => $"{source.FirstName} {source.LastName}".Trim();
    // Sicil Listesi sutunlari.
    public string FirstName => source.FirstName ?? "";
    public string LastName => source.LastName ?? "";
    public string ParentName => source.ParentName ?? "";
    public string ParentPhone => source.ParentPhone ?? "";
    public string NationalId => source.NationalId ?? "";
    public string RegisteredOn => source.ReportDate?.ToString("dd.MM.yyyy", Turkish) ?? "";
    public string Class => source.Class ?? "";
    public string Section => source.Section ?? "";
    public string Department => source.Department ?? "";
    public string Job => source.Job ?? "";
    public string MealType => source.MealType ?? "";
    public string Device => source.Device ?? "";
    // Rapor sutunlari kod icinde uretiliyor (ReportsView.xaml.cs/RebuildColumns), yani
    // XAML'de tek tek converter takilamaz. Ceviriyi burada, yalnizca GORUNTULENEN
    // metinde yapiyoruz; sunucuya giden filtre (ReportsViewModel.Decision/Status)
    // ayri bir ozelliktir ve ham İngilizce kod olarak kalir.
    public string Decision => EnumTextConverter.Translate(source.Decision, "Decision");
    // "Status" sutunu rapor turune gore farkli sey tasir: gecis raporlarinda AccessLog.Reason
    // ("OK"), turnike raporunda TurnstileEvent.Result ("OK", "TIMEOUT"), digerlerinde durum kodu.
    // Ayni "OK" metni turnikede "Basarili", geciste "Gecis onaylandi" demektir; sozluk ture gore secilir.
    public string Status => source.Type switch
    {
        ReportType.Turnstile => EnumTextConverter.Translate(source.Status, "TurnstileResult"),
        ReportType.DailyAccess or ReportType.DeniedAccess => EnumTextConverter.Translate(source.Status, "Reason"),
        _ => EnumTextConverter.Translate(source.Status, "Status")
    };
    public string Description => source.Type == ReportType.Turnstile
        ? TranslateTurnstileDescription(source.Description)
        : source.Description ?? "";

    /// <summary>"OPEN / hata metni" -> "Aç / hata metni"; hata yoksa yalnizca komut.</summary>
    private static string TranslateTurnstileDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var parts = value.Split(" / ", 2, StringSplitOptions.None);
        var command = EnumTextConverter.Translate(parts[0], "TurnstileResult");
        return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? command + " / " + parts[1] : command;
    }
    public string MealCount => source.MealCount.ToString("N0", Turkish);
    public string Amount => source.Amount.ToString("C2", Turkish);

    public string Value(string key) => GetType().GetProperty(key)?.GetValue(this)?.ToString() ?? "";
    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}

public sealed class ReportsViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly IReportApiClient api;
    private readonly IReportLayoutStore layouts;
    private readonly IReportDialogService dialogs;
    private readonly bool canRead;
    private readonly bool canReadSensitive;
    private CancellationTokenSource? operation;
    private ReportTypeOption selectedReport;
    private ReportSummary summary = new(0, 0, 0, 0, 0);
    private ReportQuery appliedQuery = new();
    private bool isLoading, isOffline, hasApplied;
    private string? errorMessage, statusMessage;
    private int page = 1, pageSize = 50;
    private int sortVersion;
    private DateTime? startDate, endDate;
    private string? studentNo, cardNo, firstName, lastName, className, section, department, job, mealType, device, decision, status;
    private ReportStateOption? selectedActiveState;

    public ReportsViewModel(IReportApiClient api, IEnumerable<string> permissions, IReportLayoutStore? layouts = null,
        IReportDialogService? dialogs = null, TimeProvider? clock = null)
    {
        this.api = api; this.layouts = layouts ?? new FileReportLayoutStore(); this.dialogs = dialogs ?? new ReportDialogService();
        var permissionSet = permissions.ToHashSet(StringComparer.Ordinal);
        canRead = permissionSet.Contains("reports.read"); CanExport = permissionSet.Contains("reports.export");
        canReadSensitive = permissionSet.Contains("students.sensitive.read");
        const ReportFilters Date = ReportFilters.Date;
        ReportTypes =
        [
            // Eski programin "Raporlar" menusundeki ilk madde; tarih filtresi yok (bkz. ReportFilters.Date).
            new(ReportType.StudentList, "Sicil Listesi", ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Organization | ReportFilters.ActiveState, "Öğrenci, kart ve veli"),
            new(ReportType.DailyAccess, "Günlük Geçiş", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Meal | ReportFilters.Device | ReportFilters.Decision | ReportFilters.Status, "Detaylı geçiş dökümü"),
            new(ReportType.MealEntitlement, "Yemek Hakediş", Date | ReportFilters.Student | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Meal | ReportFilters.Status),
            new(ReportType.StudentMealUsage, "Öğrenci Kullanımı", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Meal | ReportFilters.Status),
            new(ReportType.ClassMeal, "Sınıf Yemek", Date | ReportFilters.Student | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Meal),
            new(ReportType.DailyCash, "Günlük Kasa", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Status),
            new(ReportType.Income, "Gelir", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Status),
            new(ReportType.Sms, "SMS", Date | ReportFilters.Student | ReportFilters.Name | ReportFilters.Status),
            new(ReportType.Turnstile, "Turnike", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Device | ReportFilters.Decision | ReportFilters.Status),
            new(ReportType.DeniedAccess, "Reddedilen Geçiş", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Meal | ReportFilters.Device | ReportFilters.Status),
            new(ReportType.CardMovements, "Kart Hareketleri", Date | ReportFilters.Student | ReportFilters.Card | ReportFilters.Name | ReportFilters.Organization | ReportFilters.Status),
            new(ReportType.HolidayTransfer, "Tatil / Aktarım", Date | ReportFilters.Student | ReportFilters.Name | ReportFilters.Meal | ReportFilters.Status)
        ];
        // Acilista Gunluk Gecis secili kalir: memurun gunluk sorusu "bugun kim gecti"dir; Sicil Listesi
        // listede ilk siradadir ama 420 satirlik tam listeyi her aciliste cekmek gereksiz.
        selectedReport = ReportTypes.First(x => x.Type == ReportType.DailyAccess);
        var today = (clock ?? TimeProvider.System).GetLocalNow().Date;
        startDate = endDate = today;
        ApplyCommand = new AsyncCommand(() => ApplyAsync());
        ResetCommand = new AsyncCommand(ResetAsync);
        PreviousPageCommand = new AsyncCommand(() => LoadPageAsync(Page - 1), () => Page > 1 && !IsLoading);
        NextPageCommand = new AsyncCommand(() => LoadPageAsync(Page + 1), () => Page * PageSize < Summary.TotalRecords && !IsLoading);
        ExportPdfCommand = new AsyncCommand(() => ExportAsync(ReportExportFormat.Pdf), () => CanExport && !IsLoading);
        ExportExcelCommand = new AsyncCommand(() => ExportAsync(ReportExportFormat.Excel), () => CanExport && !IsLoading);
        ExportCsvCommand = new AsyncCommand(() => ExportAsync(ReportExportFormat.Csv), () => CanExport && !IsLoading);
        CopySelectedCommand = new RelayCommand(CopySelected, () => SelectedRows.Count > 0);
        BuildColumns();
    }

    public IReadOnlyList<ReportTypeOption> ReportTypes { get; }
    public ObservableCollection<ReportColumnViewModel> Columns { get; } = [];
    public ObservableCollection<ReportGridRow> Rows { get; } = [];
    public ObservableCollection<ReportGridRow> SelectedRows { get; } = [];
    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 200];
    public IReadOnlyList<ReportStateOption> ActiveStates { get; } = [new("Tümü", null), new("Aktif", "ACTIVE"), new("Pasif", "INACTIVE")];
    /// <summary>Rapor turu listesinin alt basligi: "12 canlı rapor".</summary>
    public string ReportCountText => $"{ReportTypes.Count} canlı rapor";
    public ReportTypeOption SelectedReport { get => selectedReport; set { if (selectedReport == value) return; selectedReport = value; Page = 1; BuildColumns(); Raise(); Raise(nameof(SummaryText)); RaiseFilterProperties(); _ = ApplyAsync(); } }
    public ReportSummary Summary { get => summary; private set { if (Set(ref summary, value)) { Raise(nameof(SummaryText)); Raise(nameof(PageText)); Raise(nameof(IsEmpty)); RefreshCommands(); } } }
    public string SummaryText => SelectedReport.Type switch
    {
        // Sicil Listesi'nde TotalMeals aktif ogrenci sayisini tasir (EfReportRepository.StudentList).
        ReportType.StudentList => $"Toplam {Summary.TotalRecords:N0}   •   Aktif {Summary.TotalMeals:N0}   •   Pasif {Summary.TotalRecords - Summary.TotalMeals:N0}",
        // Gunluk Kasa gruplu (gun x gelir turu) dondugu icin "Toplam" satir sayisi degil,
        // TotalMeals'te tasinan islem adedi kullaniciya anlamli olan sayidir.
        ReportType.DailyCash => $"Toplam {Summary.TotalRecords:N0}   •   İşlem {Summary.TotalMeals:N0}   •   Tutar {Summary.Amount.ToString("C2", Turkish)}",
        ReportType.Income => $"Toplam {Summary.TotalRecords:N0}   •   Tutar {Summary.Amount.ToString("C2", Turkish)}",
        ReportType.MealEntitlement or ReportType.StudentMealUsage or ReportType.ClassMeal or ReportType.HolidayTransfer => $"Toplam {Summary.TotalRecords:N0}   •   Yemek {Summary.TotalMeals:N0}",
        ReportType.DailyAccess or ReportType.Turnstile or ReportType.DeniedAccess => $"Toplam {Summary.TotalRecords:N0}   •   Geçen {Summary.Passed:N0}   •   Reddedilen {Summary.Denied:N0}   •   Yemek {Summary.TotalMeals:N0}",
        _ => $"Toplam {Summary.TotalRecords:N0}"
    };
    public string PageText => $"Sayfa {Page} / {Math.Max(1, (int)Math.Ceiling(Summary.TotalRecords / (double)PageSize))}";
    public int Page { get => page; private set { if (Set(ref page, value)) Raise(nameof(PageText)); } }
    public int PageSize { get => pageSize; set { if (Set(ref pageSize, value) && hasApplied) _ = LoadPageAsync(1); } }
    public bool CanExport { get; }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) { Raise(nameof(IsEmpty)); RefreshCommands(); } } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public bool IsEmpty => !IsLoading && !HasError && Summary.TotalRecords == 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) { Raise(nameof(HasError)); Raise(nameof(IsEmpty)); } } }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public bool ShowDateFilters => HasFilter(ReportFilters.Date);
    /// <summary>Tarih filtresi olmayan raporda kullaniciya nedeni soylenir; sessizce yok sayilmaz.</summary>
    public bool ShowDateNote => !HasFilter(ReportFilters.Date);
    public bool ShowActiveStateFilter => HasFilter(ReportFilters.ActiveState);
    public bool ShowStudentFilters => HasFilter(ReportFilters.Student);
    public bool ShowCardFilter => HasFilter(ReportFilters.Card);
    public bool ShowNameFilters => HasFilter(ReportFilters.Name);
    public bool ShowOrganizationFilters => HasFilter(ReportFilters.Organization);
    public bool ShowMealFilter => HasFilter(ReportFilters.Meal);
    public bool ShowDeviceFilter => HasFilter(ReportFilters.Device);
    public bool ShowDecisionFilter => HasFilter(ReportFilters.Decision);
    public bool ShowStatusFilter => HasFilter(ReportFilters.Status);
    public DateTime? StartDate { get => startDate; set => Set(ref startDate, value); }
    public DateTime? EndDate { get => endDate; set => Set(ref endDate, value); }
    public string? StudentNo { get => studentNo; set => Set(ref studentNo, value); }
    public string? CardNo { get => cardNo; set => Set(ref cardNo, value); }
    public string? FirstName { get => firstName; set => Set(ref firstName, value); }
    public string? LastName { get => lastName; set => Set(ref lastName, value); }
    public string? ClassName { get => className; set => Set(ref className, value); }
    public string? Section { get => section; set => Set(ref section, value); }
    public string? Department { get => department; set => Set(ref department, value); }
    public string? Job { get => job; set => Set(ref job, value); }
    public string? MealType { get => mealType; set => Set(ref mealType, value); }
    public string? Device { get => device; set => Set(ref device, value); }
    public string? Decision { get => decision; set => Set(ref decision, value); }
    public string? Status { get => status; set => Set(ref status, value); }
    /// <summary>Aktif / Pasif / Tumu; SelectedItem ile baglanir (WPF null SelectedValue'yu "secim yok" sayar, "Tümü" bos gorunurdu).</summary>
    public ReportStateOption? SelectedActiveState { get => selectedActiveState ?? ActiveStates[0]; set => Set(ref selectedActiveState, value); }
    public ICommand ApplyCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand ExportExcelCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand CopySelectedCommand { get; }

    public Task InitializeAsync() => ApplyAsync();

    /// <summary>
    /// "reports/{ReportType}" rotasi (orn. Ogrenciler ekranindaki "Dışa Aktar" -> Sicil Listesi).
    /// Bilinmeyen tur hicbir sey yapmaz: yanlis bir derin baglanti ekrani bozmamali.
    /// </summary>
    public void HandleRoute(string route)
    {
        var separator = route.LastIndexOf('/');
        if (separator < 0 || !Enum.TryParse<ReportType>(route[(separator + 1)..], ignoreCase: true, out var type)) return;
        var option = ReportTypes.FirstOrDefault(x => x.Type == type);
        if (option is not null) SelectedReport = option;
    }

    public async Task ApplyAsync()
    {
        ErrorMessage = null; StatusMessage = null;
        if (!canRead) { ErrorMessage = "Raporlar için reports.read izni gerekiyor."; return; }
        if (StartDate > EndDate) { ErrorMessage = "Başlangıç tarihi bitiş tarihinden sonra olamaz."; return; }
        appliedQuery = BuildQuery(1);
        hasApplied = true;
        await LoadPageAsync(1);
    }

    public async Task SortAsync(string sortKey)
    {
        var version = ++sortVersion;
        var descending = string.Equals(appliedQuery.SortBy, sortKey, StringComparison.OrdinalIgnoreCase)
            ? !appliedQuery.Descending : false;
        appliedQuery = appliedQuery with { SortBy = sortKey, Descending = descending, Page = 1 };
        await Task.Delay(180);
        if (version != sortVersion) return;
        await LoadPageAsync(1);
    }

    public void SaveLayout(IReadOnlyList<ReportColumnLayout> value)
    {
        foreach (var layout in value)
        {
            var column = Columns.FirstOrDefault(x => x.Key == layout.Key);
            if (column is null) continue;
            column.DisplayIndex = layout.DisplayIndex; column.Width = layout.Width; column.IsVisible = layout.IsVisible;
        }
        layouts.Save(SelectedReport.Type, value);
    }

    public void ReplaceSelection(IEnumerable<ReportGridRow> values)
    {
        SelectedRows.Clear(); foreach (var value in values) SelectedRows.Add(value);
        (CopySelectedCommand as RelayCommand)?.Refresh();
    }

    private async Task LoadPageAsync(int targetPage)
    {
        operation?.Cancel(); operation?.Dispose(); operation = new CancellationTokenSource();
        var token = operation.Token;
        IsLoading = true; ErrorMessage = null; IsOffline = false; StatusMessage = null;
        try
        {
            var query = appliedQuery with { Page = Math.Max(1, targetPage), PageSize = PageSize };
            var result = await api.QueryAsync(SelectedReport.Type, query, token);
            if (token.IsCancellationRequested) return;
            Rows.Clear(); foreach (var item in result.Items) Rows.Add(new ReportGridRow(item));
            Page = result.Page; Summary = result.Summary; appliedQuery = query;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (LoginRequiredException) { ErrorMessage = "Raporlar için yetkili oturum gerekiyor."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        { IsOffline = true; ErrorMessage = "Rapor verisi alınamadı. API bağlantısını kontrol edin."; }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    private async Task ResetAsync()
    {
        StartDate = EndDate = DateTime.Today;
        StudentNo = CardNo = FirstName = LastName = ClassName = Section = Department = Job = MealType = Device = Decision = Status = null;
        SelectedActiveState = ActiveStates[0];
        await ApplyAsync();
    }

    private async Task ExportAsync(ReportExportFormat format)
    {
        var path = dialogs.ChoosePath(SelectedReport.Type, format);
        if (path is null) return;
        IsLoading = true; ErrorMessage = null; StatusMessage = null;
        try
        {
            await api.ExportAsync(SelectedReport.Type, appliedQuery, format, path);
            StatusMessage = $"Rapor kaydedildi: {path}";
        }
        // Sunucu 429 (dakikada 5 disa aktarma) veya 400 dondugunde nedeni kullaniciya soylenir;
        // genel "kaydedilemedi" mesaji hemen tekrar denemeye itip yine 429 aldiriyordu.
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or LoginRequiredException)
        { ErrorMessage = "Rapor dosyası kaydedilemedi. Hedef yolu ve API bağlantısını kontrol edin."; }
        finally { IsLoading = false; }
    }

    private void CopySelected()
    {
        var visible = Columns.Where(x => x.IsVisible).OrderBy(x => x.DisplayIndex).ToArray();
        var text = new StringBuilder().AppendLine(string.Join('\t', visible.Select(x => x.Header)));
        foreach (var row in SelectedRows)
            text.AppendLine(string.Join('\t', visible.Select(x => row.Value(x.Key).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '))));
        dialogs.CopyText(text.ToString());
        StatusMessage = $"{SelectedRows.Count:N0} satır panoya kopyalandı.";
    }

    private ReportQuery BuildQuery(int targetPage) => new(
        // Tarih filtresi olmayan raporda (Sicil Listesi) tarih hic gonderilmez; sunucu da yok sayar.
        HasFilter(ReportFilters.Date) && StartDate.HasValue ? ToIstanbulOffset(StartDate.Value.Date) : null,
        HasFilter(ReportFilters.Date) && EndDate.HasValue ? ToIstanbulOffset(EndDate.Value.Date.AddDays(1)).AddTicks(-1) : null,
        HasFilter(ReportFilters.Student) ? Clean(StudentNo) : null,
        HasFilter(ReportFilters.Card) ? Clean(CardNo) : null,
        HasFilter(ReportFilters.Name) ? Clean(FirstName) : null,
        HasFilter(ReportFilters.Name) ? Clean(LastName) : null,
        HasFilter(ReportFilters.Organization) ? Clean(ClassName) : null,
        HasFilter(ReportFilters.Organization) ? Clean(Department) : null,
        HasFilter(ReportFilters.Organization) ? Clean(Section) : null,
        HasFilter(ReportFilters.Organization) ? Clean(Job) : null,
        HasFilter(ReportFilters.Meal) ? Clean(MealType) : null,
        HasFilter(ReportFilters.Device) ? Clean(Device) : null,
        HasFilter(ReportFilters.Decision) ? Clean(Decision) : null,
        HasFilter(ReportFilters.Status) ? Clean(Status) : HasFilter(ReportFilters.ActiveState) ? SelectedActiveState?.Value : null,
        appliedQuery.SortBy, appliedQuery.Descending, targetPage, PageSize);

    private void BuildColumns()
    {
        Columns.Clear();
        // TC kimlik sutunu yetkisiz kullaniciya HIC uretilmez: "Kolonlar" menusunden bile acilamaz.
        // (Sunucu zaten degeri gondermez; bu yalnizca bos bir sutunun kafa karistirmasini onler.)
        var definitions = Definitions[SelectedReport.Type].Where(x => canReadSensitive || x.Key != "NationalId").ToArray();
        var saved = layouts.Load(SelectedReport.Type).ToDictionary(x => x.Key, StringComparer.Ordinal);
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index]; var column = new ReportColumnViewModel(definition.Key, definition.Header, definition.Sort, definition.Width) { DisplayIndex = index, IsVisible = definition.Visible };
            if (saved.TryGetValue(column.Key, out var layout))
            { column.DisplayIndex = layout.DisplayIndex; column.Width = layout.Width; column.IsVisible = layout.IsVisible; }
            Columns.Add(column);
        }
    }

    private bool HasFilter(ReportFilters value) => SelectedReport.Filters.HasFlag(value);
    private void RaiseFilterProperties()
    {
        Raise(nameof(ShowDateFilters)); Raise(nameof(ShowDateNote)); Raise(nameof(ShowActiveStateFilter));
        Raise(nameof(ShowStudentFilters)); Raise(nameof(ShowCardFilter)); Raise(nameof(ShowNameFilters));
        Raise(nameof(ShowOrganizationFilters)); Raise(nameof(ShowMealFilter)); Raise(nameof(ShowDeviceFilter));
        Raise(nameof(ShowDecisionFilter)); Raise(nameof(ShowStatusFilter));
    }
    private void RefreshCommands()
    {
        (PreviousPageCommand as AsyncCommand)?.Refresh(); (NextPageCommand as AsyncCommand)?.Refresh();
        (ExportPdfCommand as AsyncCommand)?.Refresh(); (ExportExcelCommand as AsyncCommand)?.Refresh(); (ExportCsvCommand as AsyncCommand)?.Refresh();
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTimeOffset ToIstanbulOffset(DateTime value)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), zone.GetUtcOffset(value));
    }
    public void Dispose() { operation?.Cancel(); operation?.Dispose(); }

    private sealed record Definition(string Key, string Header, string? Sort, double Width, bool Visible = true);
    private static Definition C(string key, string title, string? sort = null, double width = 110) => new(key, title, sort, width);
    /// <summary>Varsayilan olarak GIZLI sutun; kullanici "Kolonlar" menusunden acar (duzen kaydedilir).</summary>
    private static Definition H(string key, string title, string? sort = null, double width = 110) => new(key, title, sort, width, false);
    // Genislik 0 = "*" (kalan alani doldurur). Onceki surumde tum sutunlar sabit pikseldi ve
    // Gunluk Gecis'te toplam 1017px, 1440px pencerede ~980px'lik tabloya sigmayip yatay
    // kaydirma cikariyordu. Her raporda en uzun serbest metin sutunu yildizdir; sabitler
    // hucre dolgusu (22px) dahil icerige gore olculdu (ReportsJourney kesik hucre denetimi).
    private static readonly Dictionary<ReportType, Definition[]> Definitions = new()
    {
        // Sicil Listesi (eski program: No, Ad, Soyad, Sinif, Sube, Bolum, Gorev, Kart, Veli, Veli Tel, Durum, Kayit, TC).
        // Ekranda ad-soyad diger 11 raporla ayni tek sutundur; disa aktarimda (CSV/Excel/PDF) Ad ve Soyad ayridir.
        // 1440px'te ~980px tabloya 13 sutun sigmaz: Bolum/Gorev (cogu okulda bos) ve TC varsayilan GIZLI,
        // "Kolonlar" menusunden acilir. TC zaten yalnizca students.sensitive.read ile uretilir (BuildColumns).
        [ReportType.StudentList] = [C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Star), C("Class", "SINIF", "class", Auto), C("Section", "ŞUBE", "section", Auto), H("Department", "BÖLÜM", "department", Auto), H("Job", "GÖREV", "job", Auto), C("CardNo", "KART NO", "cardNo", Auto), H("ParentName", "VELİ", null, Auto), C("ParentPhone", "VELİ TEL", null, Auto), C("Status", "DURUM", "status", Auto), C("RegisteredOn", "KAYIT", null, Auto), H("NationalId", "TC KİMLİK", null, Auto)],
        // Gunluk Gecis "detayli": eski programdaki Bolum / Gorev sutunlari ve Neden (Status) burada. Bolum/Gorev
        // cogu okulda bostur ve 1440px'te 11 sutun sigmaz; varsayilan gizli, Kolonlar menusunden acilir.
        // 9 gorunur sutun: ReportsView 7px hucre dolgusuyla (14px/sutun) olculdu; "Yemekhane Çıkış" 126, "Öğle Yemeği" 108.
        [ReportType.DailyAccess] = [C("Date", "TARİH", "timestamp", Auto), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("Class", "SINIF", "class", 55), H("Department", "BÖLÜM", "department", Auto), H("Job", "GÖREV", "job", Auto), C("CardNo", "KART", "cardNo", Auto), C("MealType", "ÖĞÜN", "mealType", Auto), C("Device", "CİHAZ", "device", Auto), C("Decision", "KARAR", "decision", Auto), C("Status", "NEDEN", "status", Star)],
        [ReportType.MealEntitlement] = [C("Date", "TARİH", "timestamp", 100), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("Class", "SINIF", "class", 62), C("MealType", "ÖĞÜN", "mealType", 120), C("MealCount", "ADET", "mealCount", 70), C("Status", "DURUM", "status", Star)],
        [ReportType.StudentMealUsage] = [C("Date", "TARİH", "timestamp", 150), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("Class", "SINIF", "class", 62), C("MealType", "ÖĞÜN", "mealType", 120), C("CardNo", "KART", "cardNo", Auto), C("Status", "DURUM", "status", Star)],
        [ReportType.ClassMeal] = [C("Date", "TARİH", "timestamp", 150), C("Class", "SINIF", "class", 62), C("Section", "ŞUBE", "section", 62), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("MealType", "ÖĞÜN", "mealType", Star), C("MealCount", "ADET", "mealCount", 80)],
        // Gunluk Kasa = kasa defteri (gun x gelir turu x durum); Gelir = islem islem liste.
        [ReportType.DailyCash] = [C("Date", "TARİH", "timestamp", 100), C("Description", "GELİR TÜRÜ", null, Star), C("MealCount", "İŞLEM", "mealCount", 80), C("Status", "DURUM", "status", 110), C("Amount", "TUTAR", "amount", 120)],
        [ReportType.Income] = MoneyColumns(),
        [ReportType.Sms] = [C("Date", "TARİH", "timestamp", 150), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("Description", "AÇIKLAMA", null, Star), C("Status", "DURUM", "status", 110)],
        [ReportType.Turnstile] = [C("Date", "TARİH", "timestamp", 150), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("CardNo", "KART", "cardNo", Auto), C("Device", "CİHAZ", "device", 125), C("Decision", "KARAR", "decision", 100), C("Status", "SONUÇ", "status", 110), C("Description", "AÇIKLAMA", null, Star)],
        [ReportType.DeniedAccess] = [C("Date", "TARİH", "timestamp", 150), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("CardNo", "KART", "cardNo", Auto), C("MealType", "ÖĞÜN", "mealType", 110), C("Device", "CİHAZ", "device", 125), C("Status", "NEDEN", "status", Star)],
        [ReportType.CardMovements] = [C("Date", "TARİH", "timestamp", 150), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("Class", "SINIF", "class", 62), C("CardNo", "KART", "cardNo", Auto), C("Status", "DURUM", "status", 100), C("Description", "AÇIKLAMA", null, Star)],
        [ReportType.HolidayTransfer] = [C("Date", "TARİH", "timestamp", 100), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("MealType", "ÖĞÜN", "mealType", 120), C("MealCount", "ADET", "mealCount", 70), C("Status", "DURUM", "status", 110), C("Description", "AÇIKLAMA", null, Star)]
    };
    /// <summary>Sutun genisligi degeri: kalan alani doldur (DataGridLength Star).</summary>
    public const double Star = 0;
    /// <summary>
    /// Icerige gore genisler: ad soyad, ogrenci no ve kart no gibi uzunlugu veriye bagli sutunlar.
    /// Sabit piksel "GÜNCEL ÖĞRENCİ BİR" (128px > 126px) ve "IMP115158-22" (84px > 56px) gibi
    /// gercek degerleri kesiyordu; okul numaralari ve kart numaralari kurumdan kuruma degisir.
    /// </summary>
    public const double Auto = -1;
    // Gelir: "Aylık Yemek Ücreti / Eylül ayı ödemesi" (217px) yildiz sutuna sigsin diye sabitler dar tutuldu.
    private static Definition[] MoneyColumns() => [C("Date", "TARİH", "timestamp", 142), C("StudentNo", "NO", "studentNo", Auto), C("Name", "AD SOYAD", "firstName", Auto), C("CardNo", "KART", "cardNo", Auto), C("Description", "AÇIKLAMA", null, Star), C("Status", "DURUM", "status", 90), C("Amount", "TUTAR", "amount", 105)];
}
