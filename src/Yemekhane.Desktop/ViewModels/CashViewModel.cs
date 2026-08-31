using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class CashViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly ICashApiClient api;
    private readonly TimeProvider clock;
    private readonly HashSet<string> permissions;
    private CashSummary? daily, weekly, monthly, custom;
    private IncomeTransactionDetails? selectedTransaction;
    private IncomeTypeDetails? selectedFilterType, selectedAddType, selectedManagedType;
    private StudentListItem? lookupStudent, filterStudent;
    private string? studentNumber, lookupCardNumber, filterStudentNumber, filterCardNumber, errorMessage, addError, voidReason, typeName;
    private string amountText = "";
    private string transactionTime = "";
    private bool? filterIsVoided;
    private bool isLoading, isOffline, isAddOpen, isVoidOpen, addConfirmed, voidConfirmed, typeIsActive = true;
    private int page = 1, pageSize = 50, totalCount;
    private Guid operationId = Guid.NewGuid();

    public CashViewModel(ICashApiClient api, IEnumerable<string> permissions, TimeProvider? clock = null,
        bool reportCenterAvailable = false, IShellNavigationService? navigation = null)
    {
        this.api = api; this.permissions = permissions.ToHashSet(StringComparer.Ordinal); this.clock = clock ?? TimeProvider.System;
        IsExportAvailable = reportCenterAvailable || navigation?.IsAvailable(ShellRoutes.Reports) == true;
        OpenReportsCommand = new RelayCommand(() => navigation?.Navigate(ShellRoutes.Reports), () => IsExportAvailable);
        var now = IstanbulNow();
        FilterFrom = FilterTo = AddDate = DailyDate = CustomFrom = CustomTo = now.Date;
        TransactionTime = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ApplyFiltersCommand = new AsyncCommand(() => LoadTransactionsAsync(1));
        LookupFilterStudentCommand = new AsyncCommand(LookupFilterStudentAsync);
        PreviousPageCommand = new AsyncCommand(() => LoadTransactionsAsync(Page - 1), () => Page > 1);
        NextPageCommand = new AsyncCommand(() => LoadTransactionsAsync(Page + 1), () => Page * PageSize < TotalCount);
        LoadDailyCommand = new AsyncCommand(LoadDailyAsync);
        LoadCustomCommand = new AsyncCommand(LoadCustomAsync);
        OpenAddCommand = new RelayCommand(OpenAdd, () => CanWrite);
        CloseAddCommand = new RelayCommand(() => IsAddOpen = false);
        LookupStudentCommand = new AsyncCommand(LookupStudentAsync, () => CanWrite);
        AddCommand = new AsyncCommand(AddAsync, () => CanWrite && AddConfirmed);
        OpenVoidCommand = new RelayCommand(OpenVoid, () => CanWrite && SelectedTransaction is { IsVoided: false });
        CloseVoidCommand = new RelayCommand(() => IsVoidOpen = false);
        VoidCommand = new AsyncCommand(VoidAsync, () => CanWrite && VoidConfirmed && !string.IsNullOrWhiteSpace(VoidReason));
        NewTypeCommand = new RelayCommand(NewType, () => CanManage);
        EditTypeCommand = new RelayCommand(EditType, () => CanManage && SelectedManagedType is not null);
        SaveTypeCommand = new AsyncCommand(SaveTypeAsync, () => CanManage);
        DeactivateTypeCommand = new AsyncCommand(DeactivateTypeAsync, () => CanManage && SelectedManagedType?.IsActive == true);
    }

    public ObservableCollection<IncomeTransactionDetails> Transactions { get; } = [];
    public ObservableCollection<IncomeTypeDetails> IncomeTypes { get; } = [];
    public ObservableCollection<IncomeTypeDetails> ManagedTypes { get; } = [];
    public IReadOnlyList<VoidStatusOption> VoidStatuses { get; } = [new("Tümü", null), new("Aktif", false), new("İptal", true)];
    public CashSummary? Daily { get => daily; private set { if (Set(ref daily, value)) Raise(nameof(DailyTotal)); } }
    public CashSummary? Weekly { get => weekly; private set { if (Set(ref weekly, value)) Raise(nameof(WeeklyTotal)); } }
    public CashSummary? Monthly { get => monthly; private set { if (Set(ref monthly, value)) Raise(nameof(MonthlyTotal)); } }
    public CashSummary? Custom { get => custom; private set => Set(ref custom, value); }
    public decimal DailyTotal => Daily?.TotalAmount ?? 0;
    public decimal WeeklyTotal => Weekly?.TotalAmount ?? 0;
    public decimal MonthlyTotal => Monthly?.TotalAmount ?? 0;
    public DateTime FilterFrom { get; set; }
    public DateTime FilterTo { get; set; }
    public DateTime DailyDate { get; set; }
    public DateTime CustomFrom { get; set; }
    public DateTime CustomTo { get; set; }
    public DateTime AddDate { get; set; }
    public string TransactionTime { get => transactionTime; set => Set(ref transactionTime, value); }
    public string AmountText { get => amountText; set => Set(ref amountText, value); }
    public string? Description { get; set; }
    public string? StudentNumber { get => studentNumber; set { if (Set(ref studentNumber, value)) LookupStudent = null; } }
    public string? LookupCardNumber { get => lookupCardNumber; set { if (Set(ref lookupCardNumber, value)) LookupStudent = null; } }
    public string? FilterCardNumber { get => filterCardNumber; set => Set(ref filterCardNumber, value); }
    public string? FilterStudentNumber { get => filterStudentNumber; set { if (Set(ref filterStudentNumber, value)) FilterStudent = null; } }
    public StudentListItem? FilterStudent { get => filterStudent; private set { if (Set(ref filterStudent, value)) Raise(nameof(FilterStudentText)); } }
    public string FilterStudentText => FilterStudent is null ? "Öğrenci filtresi yok" : $"{FilterStudent.StudentNo} • {FilterStudent.FirstName} {FilterStudent.LastName}";
    public bool? FilterIsVoided { get => filterIsVoided; set => Set(ref filterIsVoided, value); }
    public IncomeTypeDetails? SelectedFilterType { get => selectedFilterType; set => Set(ref selectedFilterType, value); }
    public IncomeTypeDetails? SelectedAddType { get => selectedAddType; set => Set(ref selectedAddType, value); }
    public IncomeTypeDetails? SelectedManagedType { get => selectedManagedType; set { if (Set(ref selectedManagedType, value)) RefreshCommands(); } }
    public IncomeTransactionDetails? SelectedTransaction { get => selectedTransaction; set { if (Set(ref selectedTransaction, value)) RefreshCommands(); } }
    public StudentListItem? LookupStudent { get => lookupStudent; private set { if (Set(ref lookupStudent, value)) Raise(nameof(LookupStudentText)); } }
    public string LookupStudentText => LookupStudent is null ? "Öğrenci doğrulanmadı" : $"{LookupStudent.StudentNo} • {LookupStudent.FirstName} {LookupStudent.LastName} • Kart: {LookupStudent.CardNumber ?? "-"}";
    public string VoidConfirmationText => SelectedTransaction is null ? "" : $"{SelectedTransaction.Amount.ToString("C2", Turkish)} • {SelectedTransaction.StudentName ?? "Öğrencisiz işlem"}";
    public string? VoidReason { get => voidReason; set { if (Set(ref voidReason, value)) RefreshCommands(); } }
    public string TypeName { get => typeName ?? ""; set => Set(ref typeName, value); }
    public bool TypeIsActive { get => typeIsActive; set => Set(ref typeIsActive, value); }
    public bool AddConfirmed { get => addConfirmed; set { if (Set(ref addConfirmed, value)) RefreshCommands(); } }
    public bool VoidConfirmed { get => voidConfirmed; set { if (Set(ref voidConfirmed, value)) RefreshCommands(); } }
    public bool IsAddOpen { get => isAddOpen; private set => Set(ref isAddOpen, value); }
    public bool IsVoidOpen { get => isVoidOpen; private set => Set(ref isVoidOpen, value); }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) { Raise(nameof(IsEmpty)); Raise(nameof(ShowContent)); } } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public string? AddError { get => addError; private set => Set(ref addError, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && TotalCount == 0;
    public bool ShowContent => !IsLoading;
    public bool CanRead => permissions.Contains("cash.read");
    public bool CanWrite => permissions.Contains("cash.write");
    public bool CanManage => permissions.Contains("cash.manage");
    public bool IsExportAvailable { get; }
    public int Page { get => page; private set { if (Set(ref page, value)) Raise(nameof(PageText)); } }
    public int PageSize { get => pageSize; set => Set(ref pageSize, value); }
    public int TotalCount { get => totalCount; private set { if (Set(ref totalCount, value)) { Raise(nameof(PageText)); Raise(nameof(IsEmpty)); } } }
    public string PageText => $"Sayfa {Page} / {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize))} • {TotalCount:N0} kayıt";

    public ICommand RefreshCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand LookupFilterStudentCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand LoadDailyCommand { get; }
    public ICommand LoadCustomCommand { get; }
    public ICommand OpenAddCommand { get; }
    public ICommand CloseAddCommand { get; }
    public ICommand LookupStudentCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand OpenVoidCommand { get; }
    public ICommand CloseVoidCommand { get; }
    public ICommand VoidCommand { get; }
    public ICommand NewTypeCommand { get; }
    public ICommand EditTypeCommand { get; }
    public ICommand SaveTypeCommand { get; }
    public ICommand DeactivateTypeCommand { get; }
    public ICommand OpenReportsCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        if (!CanRead) { ErrorMessage = "Kasa ekranı için cash.read izni gerekiyor."; return; }
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        try
        {
            var today = DateOnly.FromDateTime(IstanbulNow().Date);
            var dailyTask = api.SummaryAsync(CashSummaryPeriod.Daily, today);
            var weeklyTask = api.SummaryAsync(CashSummaryPeriod.IsoWeek, today);
            var monthlyTask = api.SummaryAsync(CashSummaryPeriod.Monthly, today);
            var typesTask = api.TypesAsync(CanManage);
            await Task.WhenAll(dailyTask, weeklyTask, monthlyTask, typesTask);
            Daily = await dailyTask; Weekly = await weeklyTask; Monthly = await monthlyTask;
            ReplaceTypes(await typesTask);
            await LoadTransactionsCoreAsync(1);
        }
        catch (LoginRequiredException) { ErrorMessage = "Kasa verileri için yetkili oturum gerekiyor."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        { IsOffline = true; ErrorMessage = "Kasa verileri alınamadı. API bağlantısını kontrol edin."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    public async Task LoadTransactionsAsync(int targetPage)
    {
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        try { await LoadTransactionsCoreAsync(targetPage); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { IsOffline = ex is not LoginRequiredException; ErrorMessage = "Kasa işlemleri alınamadı."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    private async Task LoadTransactionsCoreAsync(int targetPage)
    {
        if (FilterFrom.Date > FilterTo.Date) throw new InvalidDataException("Tarih aralığı geçersiz.");
        var result = await api.TransactionsAsync(new IncomeTransactionFilter(
            ToIstanbulOffset(FilterFrom.Date), ToIstanbulOffset(FilterTo.Date.AddDays(1)).AddTicks(-1), SelectedFilterType?.Id,
            FilterStudent?.Id, Empty(FilterCardNumber), FilterIsVoided, targetPage, PageSize));
        Transactions.Clear(); foreach (var item in result.Items) Transactions.Add(item);
        Page = result.Page; TotalCount = result.TotalCount;
    }

    private async Task LoadDailyAsync() => Daily = await api.SummaryAsync(CashSummaryPeriod.Daily, DateOnly.FromDateTime(DailyDate));
    private async Task LoadCustomAsync()
    {
        if (CustomFrom.Date > CustomTo.Date) { ErrorMessage = "Özel aralık başlangıcı bitişten sonra olamaz."; return; }
        ErrorMessage = null;
        Custom = await api.SummaryAsync(CashSummaryPeriod.Custom, startDate: DateOnly.FromDateTime(CustomFrom), endDate: DateOnly.FromDateTime(CustomTo));
    }

    private void OpenAdd()
    {
        var now = IstanbulNow(); AddDate = now.Date; Raise(nameof(AddDate)); TransactionTime = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        AmountText = ""; Description = null; Raise(nameof(Description)); StudentNumber = LookupCardNumber = null;
        LookupStudent = null; SelectedAddType = IncomeTypes.FirstOrDefault(); AddConfirmed = false; AddError = null; operationId = Guid.NewGuid(); IsAddOpen = true;
    }

    private async Task LookupStudentAsync()
    {
        AddError = null; LookupStudent = null;
        if (string.IsNullOrWhiteSpace(StudentNumber) == string.IsNullOrWhiteSpace(LookupCardNumber))
        { AddError = "Tam öğrenci numarası veya tam kart numarasından yalnızca birini girin."; return; }
        try
        {
            var result = await api.FindStudentAsync(Empty(StudentNumber), Empty(LookupCardNumber));
            LookupStudent = result.Items.Count == 1 ? result.Items[0] : null;
            if (LookupStudent is null) AddError = "Girilen tam değerle eşleşen tek bir aktif öğrenci bulunamadı.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { AddError = "Öğrenci doğrulanamadı."; }
    }

    private async Task LookupFilterStudentAsync()
    {
        ErrorMessage = null; FilterStudent = null;
        if (string.IsNullOrWhiteSpace(FilterStudentNumber)) return;
        try
        {
            var result = await api.FindStudentAsync(FilterStudentNumber.Trim(), null);
            FilterStudent = result.Items.Count == 1 ? result.Items[0] : null;
            if (FilterStudent is null) ErrorMessage = "Tam öğrenci numarasıyla eşleşen tek bir aktif öğrenci bulunamadı.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { ErrorMessage = "Öğrenci filtresi doğrulanamadı."; }
    }

    private async Task AddAsync()
    {
        AddError = ValidateAdd(); if (AddError is not null) return;
        var time = TimeOnly.ParseExact(TransactionTime.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        var local = AddDate.Date.Add(time.ToTimeSpan());
        var amount = decimal.Parse(AmountText.Trim(), NumberStyles.Number, Turkish);
        try
        {
            await api.AddAsync(new CreateIncomeTransactionRequest(operationId, LookupStudent!.Id,
                LookupStudent.CardNumber, ToIstanbulOffset(local), SelectedAddType!.Id, amount, Empty(Description)));
            operationId = Guid.NewGuid(); IsAddOpen = false; await RefreshAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { AddError = "Gelir kaydedilemedi. Aynı işlem kimliğiyle güvenle tekrar deneyebilirsiniz."; }
    }

    public string? ValidateAdd()
    {
        if (LookupStudent is null) return "Öğrenci veya kart doğrulaması zorunludur.";
        if (SelectedAddType is null || !SelectedAddType.IsActive) return "Aktif gelir türü seçin.";
        if (!TimeOnly.TryParseExact(TransactionTime?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "Saat SS:dd biçiminde olmalıdır.";
        if (!decimal.TryParse(AmountText?.Trim(), NumberStyles.Number, Turkish, out var amount) || amount <= 0 || decimal.Round(amount, 2) != amount)
            return "Tutar sıfırdan büyük ve en fazla iki ondalıklı olmalıdır (örn. 125,50).";
        if (Description?.Trim().Length > 500) return "Açıklama en fazla 500 karakter olmalıdır.";
        if (!AddConfirmed) return "Kayıt bilgilerini onaylayın.";
        return null;
    }

    private void OpenVoid()
    {
        if (SelectedTransaction is null) return;
        VoidReason = ""; VoidConfirmed = false; Raise(nameof(VoidConfirmationText)); IsVoidOpen = true;
    }

    private async Task VoidAsync()
    {
        if (SelectedTransaction is null || string.IsNullOrWhiteSpace(VoidReason) || !VoidConfirmed) return;
        try { await api.VoidAsync(SelectedTransaction.Id, VoidReason.Trim()); IsVoidOpen = false; await RefreshAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { ErrorMessage = "İşlem iptal edilemedi."; }
    }

    private void NewType() { SelectedManagedType = null; TypeName = ""; TypeIsActive = true; }
    private void EditType()
    {
        if (SelectedManagedType is null) return;
        TypeName = SelectedManagedType.Name; TypeIsActive = SelectedManagedType.IsActive;
    }
    private async Task SaveTypeAsync()
    {
        if (TypeName.Trim().Length is < 2 or > 100) { ErrorMessage = "Gelir türü adı 2-100 karakter olmalıdır."; return; }
        await api.SaveTypeAsync(SelectedManagedType?.Id, new SaveIncomeTypeRequest(TypeName.Trim(), TypeIsActive));
        await ReloadTypesAsync(); NewType();
    }
    private async Task DeactivateTypeAsync()
    {
        if (SelectedManagedType is null) return;
        await api.DeactivateTypeAsync(SelectedManagedType.Id); await ReloadTypesAsync(); NewType();
    }
    private async Task ReloadTypesAsync() => ReplaceTypes(await api.TypesAsync(true));
    private void ReplaceTypes(IReadOnlyList<IncomeTypeDetails> values)
    {
        IncomeTypes.Clear(); ManagedTypes.Clear();
        foreach (var value in values) { ManagedTypes.Add(value); if (value.IsActive) IncomeTypes.Add(value); }
        SelectedAddType ??= IncomeTypes.FirstOrDefault();
    }
    private void RefreshCommands()
    {
        (AddCommand as AsyncCommand)?.Refresh(); (VoidCommand as AsyncCommand)?.Refresh();
        (OpenVoidCommand as RelayCommand)?.Refresh(); (EditTypeCommand as RelayCommand)?.Refresh();
        (DeactivateTypeCommand as AsyncCommand)?.Refresh();
    }
    private DateTime IstanbulNow() => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), IstanbulZone()).DateTime;
    private static DateTimeOffset ToIstanbulOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), IstanbulZone().GetUtcOffset(value));
    private static TimeZoneInfo IstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record VoidStatusOption(string Name, bool? Value);
