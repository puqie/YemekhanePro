using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class StudentDetailTabViewModel(string key, Func<Task<IReadOnlyList<object>>> loader) : ObservableObject
{
    private bool isLoaded, isLoading;
    private string? error;

    /// <summary>
    /// API'ye giden KIMLIK. Ingilizce kalir cunku LoadTabAsync bu degeri switch'liyor;
    /// ekranda gorunen metinle karistirilirsa sunucu sekmeyi tanimaz.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    /// Ekranda gorunen Turkce baslik; bilinmeyen anahtar oldugu gibi gosterilir.
    /// Baslik sozlugu StudentTabFormatter'da, alan tanimlariyla ayni yerde tutulur:
    /// bir sekme eklendiginde baslik ve alan listesi birlikte yazilsin diye.
    /// </summary>
    public string Title { get; } = StudentTabFormatter.TabTitle(key);

    public ObservableCollection<object> Items { get; } = [];
    public bool IsLoaded { get => isLoaded; private set { if (Set(ref isLoaded, value)) Raise(nameof(IsEmpty)); } }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(IsEmpty)); } }

    /// <summary>
    /// "Kayit yok" YALNIZCA yukleme basariyla bitip hic satir gelmediyse dogrudur.
    /// Yuklenmeden once (henuz bilinmiyor) ya da hata varsa (alinamadi) false kalir:
    /// aksi halde kullanici gercek kaydini kaybettigini sanir.
    /// </summary>
    public bool IsEmpty => IsLoaded && Error is null && Items.Count == 0;
    public string EmptyText => StudentTabFormatter.EmptyText;

    public async Task LoadAsync()
    {
        if (IsLoaded || IsLoading) return;
        IsLoading = true; Error = null;
        try { foreach (var item in await loader()) Items.Add(item); IsLoaded = true; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LoginRequiredException) { Error = "Sekme verisi alınamadı."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }
}

public sealed class StudentsViewModel : ObservableObject, IDisposable
{
    private readonly IStudentApiClient api;
    private readonly IShellNavigationService navigation;
    private readonly HashSet<string> permissions;
    private readonly ICardReadEventSource cardReadSource;
    private readonly bool task43Available;
    private CancellationTokenSource? searchDelay;
    private string? search, studentNo, cardNumber, firstName, lastName, classId, sectionId, departmentId, errorMessage;
    private bool? isActive = true;
    private bool isLoading, isOffline, isQuickDetailOpen, isDetailOpen, isFormOpen, isCardWorkflowOpen;
    private string? cardWorkflowMessage;
    private CancellationTokenSource? cardReadOperation;
    private int page = 1, pageSize = 50, totalCount;
    private StudentListItem? selectedStudent;
    private StudentDetails? details;
    private StudentDetailTabViewModel? selectedTab;
    private Guid? routeClassId, routeGroupId;

    public StudentsViewModel(IStudentApiClient api, IShellNavigationService navigation, IEnumerable<string> permissions,
        bool task43Available = false, ICardReadEventSource? cardReadSource = null)
    {
        this.api = api; this.navigation = navigation; this.permissions = permissions.ToHashSet(StringComparer.Ordinal);
        this.task43Available = task43Available || (navigation.IsAvailable(ShellRoutes.Entitlements) && this.permissions.Contains("entitlements.bulk"));
        this.cardReadSource = cardReadSource ?? new DeviceCardReadEventSource(null);
        SearchCommand = new AsyncCommand(() => LoadAsync(1)); NextPageCommand = new AsyncCommand(() => LoadAsync(Page + 1), () => Page * PageSize < TotalCount);
        PreviousPageCommand = new AsyncCommand(() => LoadAsync(Page - 1), () => Page > 1);
        OpenQuickDetailCommand = new ParameterCommand<StudentListItem>(OpenQuickDetail);
        OpenFullDetailCommand = new ParameterCommand<StudentListItem>(item => _ = OpenDetailAsync(item));
        CloseDrawersCommand = new RelayCommand(CloseDrawers);
        NewStudentCommand = new RelayCommand(OpenCreate, () => CanWrite);
        EditStudentCommand = new RelayCommand(OpenEdit, () => CanWrite && Details is not null);
        SaveStudentCommand = new AsyncCommand(SaveAsync, () => CanWrite && IsFormOpen);
        DeactivateCommand = new AsyncCommand(DeactivateAsync, () => CanDeactivate && Details?.IsActive == true);
        GiveLeaveCommand = new AsyncCommand(GiveLeaveAsync, () => CanWrite && Details is not null);
        ReplaceCardCommand = new AsyncCommand(ReplaceCardAsync, () => CanManageCards && Details is not null);
        ReadCardCommand = new AsyncCommand(ReadCardAsync, () => CanManageCards && this.cardReadSource.IsAvailable);
        OpenCardWorkflowCommand = new AsyncCommand(OpenCardWorkflowAsync, () => CanManageCards);
        CloseCardWorkflowCommand = new RelayCommand(CloseCardWorkflow);
        SearchByReadCardCommand = new AsyncCommand(SearchByReadCardAsync, () => !string.IsNullOrWhiteSpace(CardNumber));
        GrantEntitlementCommand = new RelayCommand(GrantEntitlement, () => CanGrantEntitlement && (SelectedStudent is not null || Details is not null));
        OpenStudentDetailCommand = new AsyncCommand(() => SelectedStudent is null ? Task.CompletedTask : OpenDetailAsync(SelectedStudent));
        OpenSmsCommand = new RelayCommand(OpenSms, () => CanSendSms && (SelectedStudent is not null || Details is not null));
    }

    public ObservableCollection<StudentListItem> Students { get; } = [];
    public ObservableCollection<StudentDetailTabViewModel> Tabs { get; } = [];
    public IReadOnlyList<StudentStatusOption> Statuses { get; } =
        [new("Tümü", null), new("Aktif", true), new("Pasif", false)];
    public string? Search { get => search; set { if (Set(ref search, value)) DebounceSearch(); } }
    public string? StudentNo { get => studentNo; set => Set(ref studentNo, value); }
    public string? CardNumber { get => cardNumber; set => Set(ref cardNumber, value); }
    public string? FirstName { get => firstName; set => Set(ref firstName, value); }
    public string? LastName { get => lastName; set => Set(ref lastName, value); }
    public string? ClassId { get => classId; set => Set(ref classId, value); }
    public string? SectionId { get => sectionId; set => Set(ref sectionId, value); }
    public string? DepartmentId { get => departmentId; set => Set(ref departmentId, value); }
    public bool? IsActive { get => isActive; set => Set(ref isActive, value); }
    public int Page { get => page; private set { if (Set(ref page, value)) Raise(nameof(PageText)); } }
    public int PageSize { get => pageSize; set => Set(ref pageSize, value); }
    public int TotalCount { get => totalCount; private set { if (Set(ref totalCount, value)) { Raise(nameof(PageText)); Raise(nameof(IsEmpty)); } } }
    public string PageText => $"Sayfa {Page} / {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize))} • {TotalCount:N0} kayıt";
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) Raise(nameof(ShowGrid)); } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && TotalCount == 0 && !HasError;
    public bool ShowGrid => !IsLoading;
    public bool IsQuickDetailOpen { get => isQuickDetailOpen; private set => Set(ref isQuickDetailOpen, value); }
    public bool IsDetailOpen { get => isDetailOpen; private set => Set(ref isDetailOpen, value); }
    public bool IsFormOpen { get => isFormOpen; private set => Set(ref isFormOpen, value); }
    public bool IsCardWorkflowOpen { get => isCardWorkflowOpen; private set => Set(ref isCardWorkflowOpen, value); }
    public string? CardWorkflowMessage { get => cardWorkflowMessage; private set => Set(ref cardWorkflowMessage, value); }
    public bool IsCardReaderAvailable => cardReadSource.IsAvailable;
    /// <summary>
    /// Listeden bir ogrenci secilir secilmez form alanlari (NO / Ad / Soyad) O ogrencinin
    /// degerleriyle DOLDURULUR.
    ///
    /// Onceden bu ucu YALNIZCA OpenEdit() dolduruyordu, yani "Duzenle" dugmesine basilana
    /// kadar. Kullanici listeden ELİF ÇETİN'e tiklayinca sagdaki "Ogrenci Formu" panelinin
    /// ust kutulari BOS kaliyor, secilen ogrencinin kim oldugu formda hic gorunmuyordu.
    ///
    /// Form varsayilan olarak SALT OKUNUR kalir: IsFormOpen'a burada DOKUNULMAZ, dolayisiyla
    /// kutular yalnizca "Duzenle" (OpenEdit) sonrasinda yazilabilir hale gelir. Kaydet komutu
    /// da IsFormOpen'a bagli oldugundan salt okunur haldeki bu degerler kazara gonderilemez.
    ///
    /// null atamasi da doldurma sayilir ve formu TEMIZLER: aksi halde secim kalkinca onceki
    /// ogrencinin NO/Ad/Soyad'i ekranda kalir ve SameStudent korumasiyla gizlenen salt okunur
    /// blogun aksine yanlis ogrenciyi gostermeye devam ederdi.
    /// </summary>
    public StudentListItem? SelectedStudent
    {
        get => selectedStudent;
        set
        {
            if (!Set(ref selectedStudent, value)) return;
            (GrantEntitlementCommand as RelayCommand)?.Refresh();
            FillFormFromSelection(value);
        }
    }

    /// <summary>
    /// Secili ogrencinin kimlik alanlarini forma yazar. Not/TC/Adres liste ogesinde YOKTUR;
    /// onlar ancak api.GetAsync donunce (Details) bilinir, o yuzden burada TEMIZLENIR --
    /// yoksa onceki ogrencinin notu yeni secimin yaninda durur.
    ///
    /// "Yeni Ogrenci" akisi BOZULMAZ: OpenCreate() SelectedStudent'a hic dokunmaz, kendi
    /// ClearForm() cagrisini bu metottan SONRA yapar; form bos baslar.
    /// </summary>
    private void FillFormFromSelection(StudentListItem? item)
    {
        FormStudentNo = item?.StudentNo ?? "";
        FormFirstName = item?.FirstName ?? "";
        FormLastName = item?.LastName ?? "";
        FormNationalId = FormAddress = FormNotes = null;
        RaiseForm();
    }
    public StudentDetails? Details { get => details; private set { if (Set(ref details, value)) (GrantEntitlementCommand as RelayCommand)?.Refresh(); } }
    public StudentDetailTabViewModel? SelectedTab { get => selectedTab; set { if (Set(ref selectedTab, value) && value is not null) _ = value.LoadAsync(); } }
    public bool CanWrite => permissions.Contains("students.write");
    public bool CanDeactivate => permissions.Contains("students.deactivate");
    public bool CanManageCards => permissions.Contains("cards.manage");
    public bool CanReadSensitive => permissions.Contains("students.sensitive.read");
    public bool CanGrantEntitlement => task43Available && permissions.Contains("entitlements.bulk");
    public bool CanSendSms => permissions.Contains("sms.send") && navigation.IsAvailable(ShellRoutes.Sms);
    public string GrantEntitlementReason => CanGrantEntitlement ? string.Empty : "Toplu hakediş yetkisi gerekiyor.";

    public string FormStudentNo { get; set; } = "";
    public string FormFirstName { get; set; } = "";
    public string FormLastName { get; set; } = "";
    public string? FormNationalId { get; set; }
    public string? FormAddress { get; set; }
    public string? FormNotes { get; set; }
    public string LeaveType { get; set; } = "Mazeret";
    public DateTime LeaveStartsOn { get; set; } = DateTime.Today;
    public DateTime LeaveEndsOn { get; set; } = DateTime.Today;
    public string LeaveBehavior { get; set; } = "Keep";
    public string NewCardNumber { get; set; } = "";
    public string CardReplacementReason { get; set; } = "Kayıp/hasarlı kart";

    public ICommand SearchCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand OpenQuickDetailCommand { get; }
    public ICommand OpenFullDetailCommand { get; }
    public ICommand CloseDrawersCommand { get; }
    public ICommand NewStudentCommand { get; }
    public ICommand EditStudentCommand { get; }
    public ICommand SaveStudentCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand GiveLeaveCommand { get; }
    public ICommand ReplaceCardCommand { get; }
    public ICommand ReadCardCommand { get; }
    public ICommand OpenCardWorkflowCommand { get; }
    public ICommand CloseCardWorkflowCommand { get; }
    public ICommand SearchByReadCardCommand { get; }
    public ICommand GrantEntitlementCommand { get; }
    public ICommand OpenStudentDetailCommand { get; }
    public ICommand OpenSmsCommand { get; }

    public async Task InitializeAsync() => await LoadAsync(1);
    public void HandleRoute(string route)
    {
        if (route == ShellRoutes.StudentsCreate) OpenCreate();
        else if (route is ShellRoutes.Cards or ShellRoutes.CardReader) _ = OpenCardWorkflowAsync();
        else if (route.StartsWith(ShellRoutes.StudentDetail + "/", StringComparison.Ordinal)
            && Guid.TryParse(route[(route.LastIndexOf('/') + 1)..], out var id)) _ = OpenDetailByIdAsync(id);
        else if (route.StartsWith(ShellRoutes.Students + "/class/", StringComparison.Ordinal)
            && Guid.TryParse(route[(route.LastIndexOf('/') + 1)..], out var classFilter))
        { routeClassId = classFilter; routeGroupId = null; _ = LoadAsync(1); }
        else if (route.StartsWith(ShellRoutes.Students + "/group/", StringComparison.Ordinal)
            && Guid.TryParse(route[(route.LastIndexOf('/') + 1)..], out var groupFilter))
        { routeGroupId = groupFilter; routeClassId = null; _ = LoadAsync(1); }
    }

    public async Task LoadAsync(int targetPage)
    {
        if (!string.IsNullOrWhiteSpace(Search) && Search.Trim().Length < 2) return;
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        try
        {
            var result = await api.SearchAsync(new StudentQuery(Search: Empty(Search), StudentNo: Empty(StudentNo), CardNumber: Empty(CardNumber),
                FirstName: Empty(FirstName), LastName: Empty(LastName), IsActive: IsActive, Page: targetPage, PageSize: PageSize,
                ClassId: routeClassId, ClassName: Empty(ClassId), SectionName: Empty(SectionId), DepartmentName: Empty(DepartmentId), GroupId: routeGroupId));
            Students.Clear(); foreach (var item in result.Items) Students.Add(item);
            Page = result.Page; TotalCount = result.TotalCount;
        }
        catch (LoginRequiredException) { ErrorMessage = "Öğrencileri görüntülemek için students.read izni olan bir oturum gerekiyor."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        { IsOffline = true; ErrorMessage = "Öğrenci verileri alınamadı. API bağlantısını kontrol edin."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    private void DebounceSearch()
    {
        searchDelay?.Cancel(); searchDelay?.Dispose(); searchDelay = new CancellationTokenSource();
        var token = searchDelay.Token;
        _ = Task.Run(async () => { try { await Task.Delay(350, token); if (!token.IsCancellationRequested) await LoadAsync(1); } catch (OperationCanceledException) { } }, token);
    }

    private void OpenQuickDetail(StudentListItem item) { SelectedStudent = item; IsQuickDetailOpen = true; IsDetailOpen = false; }
    private async Task OpenDetailAsync(StudentListItem item) { SelectedStudent = item; await OpenDetailByIdAsync(item.Id); }
    private async Task OpenDetailByIdAsync(Guid id)
    {
        Details = await api.GetAsync(id); IsQuickDetailOpen = false; IsDetailOpen = true; IsFormOpen = false;
        Tabs.Clear();
        Tabs.Add(new StudentDetailTabViewModel("General", () => Task.FromResult<IReadOnlyList<object>>
            ([new StudentDetailRow($"No: {Details.StudentNo}  |  Ad Soyad: {Details.FirstName} {Details.LastName}  |  Durum: {(Details.IsActive ? "Aktif" : "Pasif")}")])));
        foreach (var name in new[] { "Cards", "Parents", "Entitlements", "Access History", "Leaves", "Holiday/Transfer", "Payments", "SMS History", "Audit" })
            Tabs.Add(new StudentDetailTabViewModel(name, () => api.LoadTabAsync(name, id)));
        SelectedTab = Tabs[0];
    }

    private void OpenCreate() { Details = null; ClearForm(); IsFormOpen = true; IsDetailOpen = true; IsQuickDetailOpen = false; }
    private void OpenEdit()
    {
        if (Details is null) return;
        FormStudentNo = Details.StudentNo; FormFirstName = Details.FirstName; FormLastName = Details.LastName;
        FormNationalId = Details.NationalId; FormAddress = Details.Address; FormNotes = Details.Notes; IsFormOpen = true; RaiseForm();
    }
    private async Task SaveAsync()
    {
        ErrorMessage = ValidateForm(); if (ErrorMessage is not null) return;
        try
        {
            var saved = await api.SaveAsync(Details?.Id, new SaveStudentRequest(FormStudentNo, FormFirstName, FormLastName,
                Empty(FormNationalId), Address: Empty(FormAddress), Notes: Empty(FormNotes), IsActive: Details?.IsActive ?? true));
            IsFormOpen = false; await LoadAsync(Page); await OpenDetailByIdAsync(saved.Id);
        }
        // Form ACIK BIRAKILIR: kullanici numarayi duzeltip yeniden deneyebilmelidir.
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Öğrenci kaydedilemedi."); }
    }
    private async Task DeactivateAsync()
    {
        if (Details is null) return;
        try { await api.DeactivateAsync(Details.Id); CloseDrawers(); await LoadAsync(Page); }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Öğrenci pasife alınamadı."); }
    }
    private async Task GiveLeaveAsync()
    {
        if (Details is null) return;
        try
        {
            await api.GiveLeaveAsync(new CreateLeaveRequest(Details.Id, DateOnly.FromDateTime(LeaveStartsOn), DateOnly.FromDateTime(LeaveEndsOn),
                LeaveType, null, LeaveBehavior, Guid.Empty));
            // Key: API kimligi (Ingilizce); Title artik Turkce oldugu icin arama Key uzerinden.
            var tab = Tabs.FirstOrDefault(x => x.Key == "Leaves");
            if (tab is not null && !tab.IsLoaded) SelectedTab = tab;
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "İzin kaydedilemedi."); }
    }
    private async Task ReplaceCardAsync()
    {
        if (Details is null || string.IsNullOrWhiteSpace(NewCardNumber)) { ErrorMessage = "Yeni kart numarası zorunludur."; return; }
        try
        {
            await api.ReplaceCardAsync(Details.Id, new ReplaceCardRequest(NewCardNumber.Trim(), CardReplacementReason.Trim()));
            NewCardNumber = "";
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Kart değiştirilemedi."); }
    }

    /// <summary>Kullaniciya gosterilebilir yazma hatalari; digerleri yukari birakilir.</summary>
    private static bool IsWriteFailure(Exception exception) =>
        exception is ApiRequestException or HttpRequestException or TaskCanceledException
            or InvalidDataException or LoginRequiredException;

    /// <summary>
    /// Sunucunun mesaji varsa AYNEN gosterilir ("Bu ogrenci numarasi zaten
    /// kullaniliyor."); yoksa islem icin yazilmis yedek metin kullanilir.
    /// </summary>
    private static string Describe(Exception exception, string fallback) => exception switch
    {
        ApiRequestException api => api.Message,
        LoginRequiredException => "Bu işlem için yetkiniz yok veya oturumunuz sona erdi.",
        _ => fallback + " Sunucuya ulaşılamadı."
    };
    private async Task ReadCardAsync()
    {
        var value = await cardReadSource.ReadNextAsync();
        if (value is null) return;
        NewCardNumber = value.CardNumber; Raise(nameof(NewCardNumber));
    }
    public async Task OpenCardWorkflowAsync()
    {
        IsCardWorkflowOpen = true;
        CardWorkflowMessage = null;
        if (!CanManageCards) { CardWorkflowMessage = "Kart işlemi için cards.manage izni gerekiyor."; return; }
        if (!cardReadSource.IsAvailable)
        {
            CardWorkflowMessage = "Bağlı ve aktif kart okuyucu bulunamadı. Cihaz bağlantısını kontrol edin.";
            return;
        }
        if (cardReadOperation is not null) return;
        cardReadOperation = new CancellationTokenSource();
        CardWorkflowMessage = "Kart okuyucu bekleniyor...";
        try
        {
            var value = await cardReadSource.ReadNextAsync(cardReadOperation.Token);
            if (value is null) { CardWorkflowMessage = "Kart okuyucudan veri alınamadı."; return; }
            CardNumber = value.CardNumber;
            NewCardNumber = value.CardNumber;
            Raise(nameof(NewCardNumber));
            CardWorkflowMessage = $"Kart okundu: {value.CardNumber}. Eşleşen öğrenci aranıyor.";
            await LoadAsync(1);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { CardWorkflowMessage = $"Kart okunamadı: {ex.Message}"; }
        finally { cardReadOperation?.Dispose(); cardReadOperation = null; }
    }
    private async Task SearchByReadCardAsync()
    {
        if (string.IsNullOrWhiteSpace(CardNumber)) { CardWorkflowMessage = "Önce kart okutun veya kart numarası girin."; return; }
        await LoadAsync(1);
        CardWorkflowMessage = TotalCount == 0 ? "Bu karta atanmış öğrenci bulunamadı. Bir öğrenci açarak kartı atayabilirsiniz." : $"{TotalCount:N0} eşleşen öğrenci bulundu.";
    }
    public void CloseCardWorkflow()
    {
        cardReadOperation?.Cancel();
        IsCardWorkflowOpen = false;
    }
    private void GrantEntitlement()
    {
        var id = Details?.Id ?? SelectedStudent?.Id;
        if (id.HasValue) navigation.Navigate($"{ShellRoutes.Entitlements}/{id.Value:D}");
    }
    private void OpenSms()
    {
        var id = Details?.Id ?? SelectedStudent?.Id;
        if (id.HasValue) navigation.Navigate($"{ShellRoutes.Sms}/{id.Value:D}");
    }
    private string? ValidateForm()
    {
        if (string.IsNullOrWhiteSpace(FormStudentNo) || FormStudentNo.Trim().Length > 32) return "Öğrenci NO alanı 1-32 karakter olmalıdır.";
        if (string.IsNullOrWhiteSpace(FormFirstName) || FormFirstName.Trim().Length > 100) return "Ad alanı zorunludur.";
        if (string.IsNullOrWhiteSpace(FormLastName) || FormLastName.Trim().Length > 100) return "Soyad alanı zorunludur.";
        if (!string.IsNullOrWhiteSpace(FormNationalId) && (FormNationalId.Length != 11 || !FormNationalId.All(char.IsDigit))) return "TC Kimlik No 11 rakam olmalıdır.";
        return null;
    }
    private void ClearForm() { FormStudentNo = FormFirstName = FormLastName = ""; FormNationalId = FormAddress = FormNotes = null; RaiseForm(); }
    private void RaiseForm() { Raise(nameof(FormStudentNo)); Raise(nameof(FormFirstName)); Raise(nameof(FormLastName)); Raise(nameof(FormNationalId)); Raise(nameof(FormAddress)); Raise(nameof(FormNotes)); }
    private void CloseDrawers() { IsQuickDetailOpen = IsDetailOpen = IsFormOpen = false; }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public void Dispose() { searchDelay?.Cancel(); searchDelay?.Dispose(); cardReadOperation?.Cancel(); cardReadOperation?.Dispose(); GC.SuppressFinalize(this); }
}

public sealed record StudentStatusOption(string Name, bool? Value);
