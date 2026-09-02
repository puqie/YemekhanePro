using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class CashViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private const string LookupHint = "Öğrenci no veya kart no girip Doğrula'ya basın.";
    private readonly ICashApiClient api;
    private readonly TimeProvider clock;
    private readonly HashSet<string> permissions;
    private CashSummary? daily, weekly, monthly, custom, dailyReport;
    private IncomeTransactionDetails? selectedTransaction;
    private IncomeTypeOption? selectedFilterType;
    private IncomeTypeDetails? selectedAddType, selectedManagedType;
    private StudentListItem? lookupStudent, filterStudent;
    private string? studentNumber, lookupCardNumber, filterStudentNumber, filterCardNumber, errorMessage, addError, voidReason, typeName;
    private string? topUpNote, topUpError, statusMessage;
    private string amountText = "";
    private string topUpAmountText = "";
    private string transactionTime = "";
    private bool? filterIsVoided;
    private DateTime filterFrom, filterTo, dailyDate, customFrom, customTo, addDate;
    private DateTime? topUpExpiresOn;
    private bool isLoading, isOffline, isAddOpen, isVoidOpen, isTopUpOpen, addConfirmed, voidConfirmed, topUpConfirmed, typeIsActive = true;
    private int page = 1, pageSize = 50, totalCount;
    private Guid operationId = Guid.NewGuid();
    // Bakiye yuklemesinin kendi islem kimligi: cekmece her acilista yenilenir, basarisiz denemede korunur.
    private Guid topUpOperationId = Guid.NewGuid();
    // Yalnizca "Secileni Duzenle" ile dolar: listede bir satirin SECILI olmasi, kullanicinin onu
    // duzenlemek istedigi anlamina gelmez. Onceden Kaydet, secili satir varsa onu sessizce yeniden
    // adlandiriyordu; kullanici "Yeni" demeyi unutunca mevcut tur kayboluyordu.
    private Guid? editingTypeId;

    public CashViewModel(ICashApiClient api, IEnumerable<string> permissions, TimeProvider? clock = null,
        bool reportCenterAvailable = false, IShellNavigationService? navigation = null)
    {
        this.api = api; this.permissions = permissions.ToHashSet(StringComparer.Ordinal); this.clock = clock ?? TimeProvider.System;
        IsExportAvailable = reportCenterAvailable || navigation?.IsAvailable(ShellRoutes.Reports) == true;
        OpenReportsCommand = new RelayCommand(() => navigation?.Navigate(ShellRoutes.Reports), () => IsExportAvailable);
        var now = IstanbulNow();
        FilterFrom = FilterTo = AddDate = DailyDate = CustomFrom = CustomTo = now.Date;
        TransactionTime = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        FilterTypeOptions.Add(IncomeTypeOption.All);
        selectedFilterType = IncomeTypeOption.All;
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
        OpenTopUpCommand = new RelayCommand(OpenTopUp, () => CanWrite);
        CloseTopUpCommand = new RelayCommand(() => IsTopUpOpen = false);
        TopUpCommand = new AsyncCommand(TopUpAsync, () => CanWrite && TopUpConfirmed);
        NewTypeCommand = new RelayCommand(NewType, () => CanManage);
        EditTypeCommand = new RelayCommand(EditType, () => CanManage && SelectedManagedType is not null);
        SaveTypeCommand = new AsyncCommand(SaveTypeAsync, () => CanManage);
        DeactivateTypeCommand = new AsyncCommand(DeactivateTypeAsync, () => CanManage && SelectedManagedType?.IsActive == true);
    }

    public ObservableCollection<IncomeTransactionDetails> Transactions { get; } = [];
    /// <summary>Gelir Ekle cekmecesindeki secenekler: yalnizca AKTIF turler.</summary>
    public ObservableCollection<IncomeTypeDetails> IncomeTypes { get; } = [];
    public ObservableCollection<IncomeTypeDetails> ManagedTypes { get; } = [];
    /// <summary>
    /// Islem filtresi secenekleri: basta "Tümü", ardindan tum turler (pasifler "(pasif)" ekiyle).
    /// Filtre kutusu IncomeTypes'a bagliyken hicbir sey secili degilken BOS gorunuyordu ve
    /// pasif bir turun eski islemleri filtrelenemiyordu.
    /// </summary>
    public ObservableCollection<IncomeTypeOption> FilterTypeOptions { get; } = [];
    public IReadOnlyList<VoidStatusOption> VoidStatuses { get; } = [new("Tümü", null), new("Aktif", false), new("İptal", true)];
    public CashSummary? Daily { get => daily; private set { if (Set(ref daily, value)) Raise(nameof(DailyTotal)); } }
    public CashSummary? Weekly { get => weekly; private set { if (Set(ref weekly, value)) Raise(nameof(WeeklyTotal)); } }
    public CashSummary? Monthly { get => monthly; private set { if (Set(ref monthly, value)) Raise(nameof(MonthlyTotal)); } }
    public CashSummary? Custom { get => custom; private set => Set(ref custom, value); }
    /// <summary>
    /// "Günlük Kasa" sekmesinde secilen gunun ozeti. BUGÜN karti (<see cref="Daily"/>) ile AYRI tutulur:
    /// onceden ayni ozellik paylasiliyordu ve dunun kasasina bakmak ust karttaki bugunku tutari eziyordu.
    /// </summary>
    public CashSummary? DailyReport { get => dailyReport; private set => Set(ref dailyReport, value); }
    public decimal DailyTotal => Daily?.TotalAmount ?? 0;
    public decimal WeeklyTotal => Weekly?.TotalAmount ?? 0;
    public decimal MonthlyTotal => Monthly?.TotalAmount ?? 0;
    // Tarih alanlari bildirimli (INPC): kod tarafindan atanan deger (acilista bugun, formu sifirlama)
    // DatePicker'a yansimali; duz otomatik ozellikte kutu eski tarihi gosterirken sorgu yeni tarihle gidiyordu.
    public DateTime FilterFrom { get => filterFrom; set => Set(ref filterFrom, value); }
    public DateTime FilterTo { get => filterTo; set => Set(ref filterTo, value); }
    public DateTime DailyDate { get => dailyDate; set => Set(ref dailyDate, value); }
    public DateTime CustomFrom { get => customFrom; set => Set(ref customFrom, value); }
    public DateTime CustomTo { get => customTo; set => Set(ref customTo, value); }
    public DateTime AddDate { get => addDate; set => Set(ref addDate, value); }
    public string TransactionTime { get => transactionTime; set => Set(ref transactionTime, value); }
    public string AmountText { get => amountText; set => Set(ref amountText, value); }
    public string? Description { get; set; }
    public string? StudentNumber { get => studentNumber; set { if (Set(ref studentNumber, value)) LookupStudent = null; } }
    public string? LookupCardNumber { get => lookupCardNumber; set { if (Set(ref lookupCardNumber, value)) LookupStudent = null; } }
    public string? FilterCardNumber { get => filterCardNumber; set => Set(ref filterCardNumber, value); }
    public string? FilterStudentNumber { get => filterStudentNumber; set { if (Set(ref filterStudentNumber, value)) FilterStudent = null; } }
    public StudentListItem? FilterStudent { get => filterStudent; private set { if (Set(ref filterStudent, value)) Raise(nameof(FilterStudentText)); } }
    public string FilterStudentText => FilterStudent is null ? "Öğrenci filtresi yok" : "Öğrenci filtresi: " + Identity(FilterStudent);
    public bool? FilterIsVoided { get => filterIsVoided; set => Set(ref filterIsVoided, value); }
    public IncomeTypeOption? SelectedFilterType { get => selectedFilterType; set => Set(ref selectedFilterType, value); }
    public IncomeTypeDetails? SelectedAddType { get => selectedAddType; set => Set(ref selectedAddType, value); }
    public IncomeTypeDetails? SelectedManagedType { get => selectedManagedType; set { if (Set(ref selectedManagedType, value)) RefreshCommands(); } }
    public IncomeTransactionDetails? SelectedTransaction { get => selectedTransaction; set { if (Set(ref selectedTransaction, value)) RefreshCommands(); } }
    public StudentListItem? LookupStudent { get => lookupStudent; private set { if (Set(ref lookupStudent, value)) { Raise(nameof(LookupStudentText)); Raise(nameof(HasLookupStudent)); } } }
    public bool HasLookupStudent => LookupStudent is not null;
    /// <summary>
    /// Dogrulanmis ogrencinin AYIRT EDICI kimligi (ad, no, sinif/sube, kart). Ayni ad-soyadli
    /// ogrenciler oldugu icin yalnizca ad yetmez. Dogrulama yapilmamisken hata degil yonlendirme
    /// metni gosterilir: bos formda "dogrulanmadi" uyarisi kullaniciyi hata yaptigina inandiriyordu.
    /// </summary>
    public string LookupStudentText => LookupStudent is null ? LookupHint : Identity(LookupStudent);
    public string VoidConfirmationText => SelectedTransaction is null ? "" : $"{SelectedTransaction.Amount.ToString("C2", Turkish)} • {SelectedTransaction.StudentName ?? "Öğrencisiz işlem"}";
    public string? VoidReason { get => voidReason; set { if (Set(ref voidReason, value)) RefreshCommands(); } }
    public string TypeName { get => typeName ?? ""; set => Set(ref typeName, value); }
    public bool TypeIsActive { get => typeIsActive; set => Set(ref typeIsActive, value); }
    public string TypeFormTitle => editingTypeId is null ? "Yeni gelir türü" : "Gelir türünü düzenle";
    public bool AddConfirmed { get => addConfirmed; set { if (Set(ref addConfirmed, value)) RefreshCommands(); } }
    public bool VoidConfirmed { get => voidConfirmed; set { if (Set(ref voidConfirmed, value)) RefreshCommands(); } }
    public bool IsAddOpen { get => isAddOpen; private set => Set(ref isAddOpen, value); }
    public bool IsVoidOpen { get => isVoidOpen; private set => Set(ref isVoidOpen, value); }
    // Bakiye Yukle cekmecesi (eski programdaki "TL Bakiye Yukleme"): ogrenci dogrulama alanlari
    // Gelir Ekle ile paylasilir (ayni anda yalnizca bir cekmece acik olabilir).
    public bool IsTopUpOpen { get => isTopUpOpen; private set => Set(ref isTopUpOpen, value); }
    public string TopUpAmountText { get => topUpAmountText; set => Set(ref topUpAmountText, value); }
    public string? TopUpNote { get => topUpNote; set => Set(ref topUpNote, value); }
    /// <summary>Bos = suresiz. Doluysa o tarihten sonra yuklemenin harcanmamis kalani gecis kararinda kullanilmaz.</summary>
    public DateTime? TopUpExpiresOn { get => topUpExpiresOn; set => Set(ref topUpExpiresOn, value); }
    public bool TopUpConfirmed { get => topUpConfirmed; set { if (Set(ref topUpConfirmed, value)) RefreshCommands(); } }
    public string? TopUpError { get => topUpError; private set => Set(ref topUpError, value); }
    /// <summary>Basarili yukleme ve iptal uyarisi gibi hata olmayan sonuc bildirimleri (alt bilgi satiri).</summary>
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
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
    public ICommand OpenTopUpCommand { get; }
    public ICommand CloseTopUpCommand { get; }
    public ICommand TopUpCommand { get; }
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
            // Gunluk Kasa sekmesi bugunu gosteriyorsa ayni veriyi paylasir; baska bir gun seciliyse
            // ekleme/iptal sonrasi o gunun rakamlari da tazelenir.
            DailyReport = DateOnly.FromDateTime(DailyDate) == today ? Daily : await api.SummaryAsync(CashSummaryPeriod.Daily, DateOnly.FromDateTime(DailyDate));
            ReplaceTypes(await typesTask);
            await LoadTransactionsCoreAsync(1);
        }
        catch (LoginRequiredException) { ErrorMessage = "Kasa verileri için yetkili oturum gerekiyor."; }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        { IsOffline = true; ErrorMessage = "Kasa verileri alınamadı. API bağlantısını kontrol edin."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    public async Task LoadTransactionsAsync(int targetPage)
    {
        // Tarih araligi kullanici hatasidir, ag hatasi degil. Once burada yakalanir: asagidaki
        // catch InvalidDataException'i cevrimdisi sayip "Çevrimdışı" rozetini yakiyor ve gercek
        // nedeni "Kasa işlemleri alınamadı." ile eziyordu (LoadCustomAsync bunu zaten dogru yapiyor).
        if (FilterFrom.Date > FilterTo.Date) { ErrorMessage = "Filtre başlangıcı bitişten sonra olamaz."; return; }
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        try { await LoadTransactionsCoreAsync(targetPage); }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
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
        var selectedId = SelectedTransaction?.Id;
        Transactions.Clear(); foreach (var item in result.Items) Transactions.Add(item);
        // Secim, listedeki GUNCEL kayda tasinir. Eski nesne kalsaydi iptal edilmis bir islem
        // hala IsVoided=false gorunur ve "Secili Islemi Iptal Et" ikinci kez acilabilirdi.
        SelectedTransaction = selectedId is null ? null : Transactions.FirstOrDefault(t => t.Id == selectedId);
        Page = result.Page; TotalCount = result.TotalCount;
    }

    private async Task LoadDailyAsync()
    {
        ErrorMessage = null;
        try { DailyReport = await api.SummaryAsync(CashSummaryPeriod.Daily, DateOnly.FromDateTime(DailyDate)); }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = Describe(ex, "Günlük kasa alınamadı."); }
    }

    private async Task LoadCustomAsync()
    {
        if (CustomFrom.Date > CustomTo.Date) { ErrorMessage = "Özel aralık başlangıcı bitişten sonra olamaz."; return; }
        ErrorMessage = null;
        try { Custom = await api.SummaryAsync(CashSummaryPeriod.Custom, startDate: DateOnly.FromDateTime(CustomFrom), endDate: DateOnly.FromDateTime(CustomTo)); }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = Describe(ex, "Özel aralık özeti alınamadı."); }
    }

    private void OpenAdd()
    {
        ResetAddForm();
        // Iki cekmece ayni anda acik kalamaz: ekleme, iptal ve bakiye yukleme birbirini disliyor.
        IsVoidOpen = false; IsTopUpOpen = false; IsAddOpen = true;
    }

    private void OpenTopUp()
    {
        ResetTopUpForm();
        IsVoidOpen = false; IsAddOpen = false; IsTopUpOpen = true;
    }

    private void ResetTopUpForm()
    {
        StudentNumber = LookupCardNumber = null; LookupStudent = null;
        TopUpAmountText = ""; TopUpNote = null; TopUpExpiresOn = null; TopUpConfirmed = false; TopUpError = null;
        topUpOperationId = Guid.NewGuid();
    }

    public string? ValidateTopUp()
    {
        if (LookupStudent is null) return "Öğrenci veya kart doğrulaması zorunludur.";
        if (!TryParseAmount(TopUpAmountText, out var amount))
            return "Tutar sıfırdan büyük ve en fazla iki ondalıklı olmalıdır (örn. 500 veya 1.250,50).";
        if (amount > StudentBalanceService.MaxTopUpAmount) return $"Tek seferde en fazla {StudentBalanceService.MaxTopUpAmount:N0} ₺ yüklenebilir.";
        if (TopUpNote?.Trim().Length > 500) return "Açıklama en fazla 500 karakter olmalıdır.";
        if (TopUpExpiresOn is { } expires && expires.Date < IstanbulNow().Date) return "Bitiş tarihi bugünden önce olamaz.";
        if (!TopUpConfirmed) return "Yükleme bilgilerini onaylayın.";
        return null;
    }

    private async Task TopUpAsync()
    {
        TopUpError = ValidateTopUp(); if (TopUpError is not null) return;
        TryParseAmount(TopUpAmountText, out var amount);
        var student = LookupStudent!;
        try
        {
            var result = await api.TopUpBalanceAsync(new BalanceTopUpRequest(student.Id, null, amount, Empty(TopUpNote),
                TopUpExpiresOn is { } expires ? DateOnly.FromDateTime(expires) : null, topUpOperationId));
            StatusMessage = $"{amount.ToString("C2", Turkish)} yüklendi · {Identity(student)} · yeni bakiye {result.Balance.ToString("C2", Turkish)}";
            IsTopUpOpen = false; ResetTopUpForm(); await RefreshAsync();
        }
        catch (ApiRequestException ex) { TopUpError = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { TopUpError = "Bakiye yüklenemedi. Aynı işlem kimliğiyle güvenle tekrar deneyebilirsiniz."; }
    }

    private void ResetAddForm()
    {
        var now = IstanbulNow(); AddDate = now.Date; TransactionTime = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        AmountText = ""; Description = null; Raise(nameof(Description)); StudentNumber = LookupCardNumber = null;
        LookupStudent = null; SelectedAddType = IncomeTypes.FirstOrDefault(); AddConfirmed = false; AddError = null; operationId = Guid.NewGuid();
    }

    private async Task LookupStudentAsync()
    {
        SetLookupError(null); LookupStudent = null;
        if (string.IsNullOrWhiteSpace(StudentNumber) == string.IsNullOrWhiteSpace(LookupCardNumber))
        { SetLookupError("Tam öğrenci numarası veya tam kart numarasından yalnızca birini girin."); return; }
        try
        {
            var result = await api.FindStudentAsync(Empty(StudentNumber), Empty(LookupCardNumber));
            LookupStudent = result.Items.Count == 1 ? result.Items[0] : null;
            if (LookupStudent is null) SetLookupError("Girilen tam değerle eşleşen tek bir aktif öğrenci bulunamadı.");
        }
        catch (Exception ex) when (IsApiFailure(ex)) { SetLookupError(Describe(ex, "Öğrenci doğrulanamadı.")); }
    }

    /// <summary>Dogrulama hatasi ACIK olan cekmecede gorunur; Bakiye Yukle acikken Gelir Ekle'nin metnine yazilsa kullanici gormezdi.</summary>
    private void SetLookupError(string? message)
    {
        if (IsTopUpOpen) TopUpError = message; else AddError = message;
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
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = Describe(ex, "Öğrenci filtresi doğrulanamadı."); }
    }

    private async Task AddAsync()
    {
        AddError = ValidateAdd(); if (AddError is not null) return;
        var time = TimeOnly.ParseExact(TransactionTime.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        var local = AddDate.Date.Add(time.ToTimeSpan());
        TryParseAmount(AmountText, out var amount);
        try
        {
            await api.AddAsync(new CreateIncomeTransactionRequest(operationId, LookupStudent!.Id,
                LookupStudent.CardNumber, ToIstanbulOffset(local), SelectedAddType!.Id, amount, Empty(Description)));
            IsAddOpen = false; ResetAddForm(); await RefreshAsync();
        }
        catch (ApiRequestException ex) { AddError = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { AddError = "Gelir kaydedilemedi. Aynı işlem kimliğiyle güvenle tekrar deneyebilirsiniz."; }
    }

    public string? ValidateAdd()
    {
        if (LookupStudent is null) return "Öğrenci veya kart doğrulaması zorunludur.";
        if (SelectedAddType is null || !SelectedAddType.IsActive) return "Aktif gelir türü seçin.";
        if (!TimeOnly.TryParseExact(TransactionTime?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "Saat SS:dd biçiminde olmalıdır.";
        if (!TryParseAmount(AmountText, out _))
            return "Tutar sıfırdan büyük ve en fazla iki ondalıklı olmalıdır (örn. 125,50 veya 1.250,50).";
        if (Description?.Trim().Length > 500) return "Açıklama en fazla 500 karakter olmalıdır.";
        if (!AddConfirmed) return "Kayıt bilgilerini onaylayın.";
        return null;
    }

    /// <summary>
    /// Tutari Turkce yazimla ("1.250,50") okur; "1250.50" gibi noktali ondalik yazimi da kabul eder.
    /// tr-TR ile duz decimal.Parse "1250.50"yi 125.050 olarak okuyordu: nokta Turkcede binlik
    /// ayiracidir ve kullanicinin 1.250,50 niyetiyle yazdigi tutar YUZ KAT fazla kaydediliyordu.
    /// Kural: nokta ancak tam olarak uc basamakli gruplar ayiriyorsa binliktir; aksi halde ondaliktir.
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0;
        var value = text?.Trim().Replace("₺", "", StringComparison.Ordinal).Replace("TL", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(value) || value.Any(c => !char.IsDigit(c) && c != '.' && c != ',')) return false;
        var lastComma = value.LastIndexOf(','); var lastDot = value.LastIndexOf('.');
        string integerPart, fractionPart = "";
        if (lastComma >= 0 && lastDot >= 0)
        {
            // Iki ayirac da var: sondaki ondaliktir ("1.250,50" ya da "1,250.50"), oteki binliktir.
            var decimalIndex = Math.Max(lastComma, lastDot);
            var thousands = decimalIndex == lastComma ? '.' : ',';
            integerPart = value[..decimalIndex]; fractionPart = value[(decimalIndex + 1)..];
            if (!ValidGroups(integerPart, thousands)) return false;
            integerPart = integerPart.Replace(thousands.ToString(), "", StringComparison.Ordinal);
        }
        else if (lastComma >= 0)
        {
            if (value.IndexOf(',') != lastComma) return false;
            integerPart = value[..lastComma]; fractionPart = value[(lastComma + 1)..];
        }
        else if (lastDot >= 0)
        {
            var groups = value.Split('.');
            if (groups.Length > 2 || groups[1].Length == 3)
            {
                if (!ValidGroups(value, '.')) return false;
                integerPart = value.Replace(".", "", StringComparison.Ordinal);
            }
            else { integerPart = groups[0]; fractionPart = groups[1]; }
        }
        else integerPart = value;
        if (integerPart.Length == 0 || fractionPart.Length > 2 || !integerPart.All(char.IsDigit) || !fractionPart.All(char.IsDigit)) return false;
        if (!decimal.TryParse(integerPart + (fractionPart.Length > 0 ? "." + fractionPart : ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)) return false;
        return amount > 0;
    }

    private static bool ValidGroups(string integerPart, char separator)
    {
        var groups = integerPart.Split(separator);
        return groups[0].Length is >= 1 and <= 3 && groups.Skip(1).All(g => g.Length == 3);
    }

    private void OpenVoid()
    {
        if (SelectedTransaction is null) return;
        VoidReason = ""; VoidConfirmed = false; Raise(nameof(VoidConfirmationText));
        IsAddOpen = false; IsTopUpOpen = false; IsVoidOpen = true;
    }

    private async Task VoidAsync()
    {
        if (SelectedTransaction is null || string.IsNullOrWhiteSpace(VoidReason) || !VoidConfirmed) return;
        try
        {
            var voided = await api.VoidAsync(SelectedTransaction.Id, VoidReason.Trim());
            // Bakiye yuklemesi iptalinde sunucu bakiyenin eksiye dustugunu bildirebilir; sessizce yutulmaz.
            StatusMessage = voided.Warning;
            IsVoidOpen = false; await RefreshAsync();
        }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = Describe(ex, "İşlem iptal edilemedi."); }
    }

    private void NewType() { SelectedManagedType = null; editingTypeId = null; TypeName = ""; TypeIsActive = true; Raise(nameof(TypeFormTitle)); }
    private void EditType()
    {
        if (SelectedManagedType is null) return;
        editingTypeId = SelectedManagedType.Id; TypeName = SelectedManagedType.Name; TypeIsActive = SelectedManagedType.IsActive; Raise(nameof(TypeFormTitle));
    }
    private async Task SaveTypeAsync()
    {
        if (TypeName.Trim().Length is < 2 or > 100) { ErrorMessage = "Gelir türü adı 2-100 karakter olmalıdır."; return; }
        ErrorMessage = null;
        try
        {
            await api.SaveTypeAsync(editingTypeId, new SaveIncomeTypeRequest(TypeName.Trim(), TypeIsActive));
            await ReloadTypesAsync(); NewType();
        }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = Describe(ex, "Gelir türü kaydedilemedi."); }
    }
    private async Task DeactivateTypeAsync()
    {
        if (SelectedManagedType is null) return;
        ErrorMessage = null;
        try { await api.DeactivateTypeAsync(SelectedManagedType.Id); await ReloadTypesAsync(); NewType(); }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = Describe(ex, "Gelir türü pasifleştirilemedi."); }
    }
    private async Task ReloadTypesAsync() => ReplaceTypes(await api.TypesAsync(true));
    private void ReplaceTypes(IReadOnlyList<IncomeTypeDetails> values)
    {
        IncomeTypes.Clear(); ManagedTypes.Clear();
        foreach (var value in values) { ManagedTypes.Add(value); if (value.IsActive) IncomeTypes.Add(value); }
        var selectedFilterId = SelectedFilterType?.Id;
        FilterTypeOptions.Clear(); FilterTypeOptions.Add(IncomeTypeOption.All);
        foreach (var value in values) FilterTypeOptions.Add(new IncomeTypeOption(value.Id, value.IsActive ? value.Name : value.Name + " (pasif)"));
        SelectedFilterType = FilterTypeOptions.FirstOrDefault(o => o.Id == selectedFilterId) ?? IncomeTypeOption.All;
        // Pasiflesen tur ekleme kutusunda secili kalmamali; API zaten reddeder ama kullanici nedenini gormezdi.
        if (SelectedAddType is null || !IncomeTypes.Contains(SelectedAddType)) SelectedAddType = IncomeTypes.FirstOrDefault();
    }
    private void RefreshCommands()
    {
        (AddCommand as AsyncCommand)?.Refresh(); (VoidCommand as AsyncCommand)?.Refresh(); (TopUpCommand as AsyncCommand)?.Refresh();
        (OpenVoidCommand as RelayCommand)?.Refresh(); (EditTypeCommand as RelayCommand)?.Refresh();
        (DeactivateTypeCommand as AsyncCommand)?.Refresh();
    }
    /// <summary>Ayirt edici ogrenci kimligi; StudentIdentityConverter ile ayni dizilis: "AD SOYAD · No 5016 · 8B/B · Kart 8350016".</summary>
    private static string Identity(StudentListItem s)
    {
        var parts = new List<string> { $"{s.FirstName} {s.LastName}".Trim(), $"No {s.StudentNo}" };
        var classText = string.Join('/', new[] { s.ClassName, s.SectionName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (classText.Length > 0) parts.Add(classText);
        parts.Add(s.CardNumber is null ? "Kart yok" : $"Kart {s.CardNumber}");
        return string.Join(" · ", parts);
    }
    private static bool IsApiFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException or ApiRequestException;
    /// <summary>Sunucunun ProblemDetails mesaji varsa onu, yoksa yerel yedek metni dondurur.</summary>
    private static string Describe(Exception ex, string fallback) => ex is ApiRequestException api ? api.Message : fallback;
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

/// <summary>Islem filtresindeki gelir turu secenegi; Id null ise "Tümü".</summary>
public sealed record IncomeTypeOption(Guid? Id, string Name)
{
    public static readonly IncomeTypeOption All = new(null, "Tümü");
}
