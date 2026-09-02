using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Organization;
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

    /// <summary>
    /// Sekmeyi "hic yuklenmemis" durumuna dondurup yeniden yukler. Izin verildikten ya da
    /// kart degistirildikten sonra cagrilir: aksi halde daha once acilmis sekme eski
    /// listeyi gostermeye devam eder ve kullanici islemin yapilmadigini sanir.
    /// </summary>
    public Task ReloadAsync()
    {
        Items.Clear(); IsLoaded = false; Error = null;
        return LoadAsync();
    }
}

public sealed class StudentsViewModel : ObservableObject, IDisposable
{
    private readonly IStudentApiClient api;
    private readonly IShellNavigationService navigation;
    private readonly HashSet<string> permissions;
    private readonly ICardReadEventSource cardReadSource;
    private readonly bool task43Available;
    /// <summary>
    /// Arayuz is parcaciginin baglami. Gecikmeli arama (DebounceSearch) bir havuz is
    /// parcaciginda uyanir; listeyi ORADAN degistirmek WPF'te NotSupportedException
    /// atar ("CollectionView ... farkli bir is parcacigindan degisiklikleri desteklemez")
    /// ve bu hata Task.Run icinde kaybolur: kullanici arama kutusuna yazar, hicbir sey
    /// olmaz. Yukleme bu baglama geri gonderilir. Baglam yoksa (birim testi) dogrudan calisir.
    /// </summary>
    private readonly SynchronizationContext? uiContext;
    private CancellationTokenSource? searchDelay;
    private string? search, studentNo, cardNumber, firstName, lastName, classId, sectionId, departmentId, errorMessage;
    private bool? isActive = true;
    private bool isLoading, isOffline, isQuickDetailOpen, isDetailOpen, isFormOpen, isCardWorkflowOpen;
    private string? cardWorkflowMessage, infoMessage;
    private bool isDeleteArmed;
    private CancellationTokenSource? cardReadOperation;
    private int page = 1, pageSize = 50, totalCount;
    private StudentListItem? selectedStudent;
    private StudentDetails? details;
    private StudentDetailTabViewModel? selectedTab;
    private Guid? routeClassId, routeGroupId;
    /// <summary>
    /// Fotograf durumu: <c>photoBytes</c> sunucudaki (Details.PhotoPath) dosyanin icerigi;
    /// <c>pendingPhoto</c> kullanicinin "Resim Sec" ile sectigi ama HENUZ kaydedilmemis dosya;
    /// <c>photoRemoved</c> "Kaldir"a basildigini soyler. Yukleme/silme Kaydet'te yapilir: yeni
    /// ogrencide kimlik daha yokken dosya gonderilemez, duzenlemede de Iptal ile geri alinabilmeli.
    /// </summary>
    private byte[]? photoBytes, pendingPhoto;
    private string? pendingPhotoName, photoError;
    private bool photoRemoved, lookupsLoaded;
    private ImageSource? photoImage;
    private DateTime? formBirthDate;
    private string formStudentNo = "", formFirstName = "", formLastName = "";
    private string? formNationalId, formAddress, formNotes, formFingerprintId, formPid;
    private readonly IFileDialogService fileDialog;

    public StudentsViewModel(IStudentApiClient api, IShellNavigationService navigation, IEnumerable<string> permissions,
        bool task43Available = false, ICardReadEventSource? cardReadSource = null, IFileDialogService? fileDialog = null)
    {
        this.api = api; this.navigation = navigation; this.permissions = permissions.ToHashSet(StringComparer.Ordinal);
        // Varsayilan gercek diyalog: App.xaml.cs'e dokunmadan uretimde calisir; testler kendi sahtesini verir.
        this.fileDialog = fileDialog ?? new FileDialogService();
        FormClass = new LookupPickerViewModel(LookupKind.Class, api);
        FormSection = new LookupPickerViewModel(LookupKind.Section, api);
        FormDepartment = new LookupPickerViewModel(LookupKind.Department, api);
        FormJob = new LookupPickerViewModel(LookupKind.Job, api);
        SelectPhotoCommand = new RelayCommand(SelectPhoto, () => IsFormOpen);
        RemovePhotoCommand = new RelayCommand(RemovePhoto, () => IsFormOpen && HasPhoto);
        this.task43Available = task43Available || (navigation.IsAvailable(ShellRoutes.Entitlements) && this.permissions.Contains("entitlements.bulk"));
        this.cardReadSource = cardReadSource ?? new DeviceCardReadEventSource(null);
        uiContext = SynchronizationContext.Current;
        SearchCommand = new AsyncCommand(() => LoadAsync(1)); NextPageCommand = new AsyncCommand(() => LoadAsync(Page + 1), () => Page * PageSize < TotalCount);
        PreviousPageCommand = new AsyncCommand(() => LoadAsync(Page - 1), () => Page > 1);
        OpenQuickDetailCommand = new ParameterCommand<StudentListItem>(OpenQuickDetail);
        OpenFullDetailCommand = new ParameterCommand<StudentListItem>(item => _ = OpenDetailAsync(item));
        CloseDrawersCommand = new RelayCommand(CloseDrawers);
        NewStudentCommand = new RelayCommand(OpenCreate, () => CanWrite);
        EditStudentCommand = new RelayCommand(OpenEdit, () => CanWrite && Details is not null && !IsFormOpen);
        CancelEditCommand = new RelayCommand(CancelEdit, () => IsFormOpen);
        SaveStudentCommand = new AsyncCommand(SaveAsync, () => CanWrite && IsFormOpen);
        DeactivateCommand = new AsyncCommand(() => SetActiveAsync(false, "Öğrenci pasife alınamadı."), () => CanWrite && Details?.IsActive == true);
        ActivateCommand = new AsyncCommand(() => SetActiveAsync(true, "Öğrenci aktifleştirilemedi."), () => CanWrite && Details?.IsActive == false);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanDeactivate && Details is not null);
        CancelDeleteCommand = new RelayCommand(() => IsDeleteArmed = false, () => IsDeleteArmed);
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
    /// <summary>Basarili ama ekranda izi kalmayan islemin (silme) geri bildirimi.</summary>
    public string? InfoMessage { get => infoMessage; private set { if (Set(ref infoMessage, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => !string.IsNullOrWhiteSpace(InfoMessage);
    /// <summary>
    /// Silme iki adimlidir: ilk tiklama dugmeyi "Silmeyi Onayla"ya cevirir, ikincisi siler.
    /// Modal onay kutusu yerine bu yol secildi: tek tiklamayla geri donusu olmayan bir
    /// silme (kayit tum listelerden kaybolur) kabul edilemez, ama modal da bu ekranda yok.
    /// </summary>
    public bool IsDeleteArmed
    {
        get => isDeleteArmed;
        private set { if (Set(ref isDeleteArmed, value)) { Raise(nameof(DeleteButtonText)); (CancelDeleteCommand as RelayCommand)?.Refresh(); } }
    }
    public string DeleteButtonText => IsDeleteArmed ? "Silmeyi Onayla" : "Sil";
    public bool IsEmpty => !IsLoading && TotalCount == 0 && !HasError;
    public bool ShowGrid => !IsLoading;
    public bool IsQuickDetailOpen { get => isQuickDetailOpen; private set => Set(ref isQuickDetailOpen, value); }
    public bool IsDetailOpen { get => isDetailOpen; private set => Set(ref isDetailOpen, value); }
    public bool IsFormOpen { get => isFormOpen; private set { if (Set(ref isFormOpen, value)) RefreshCommands(); } }
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
            FillFormFromSelection(value);
            IsDeleteArmed = false; InfoMessage = null;
            Raise(nameof(CardActionText)); Raise(nameof(DetailCardNumber)); Raise(nameof(DetailDepartmentName));
            RefreshCommands();
        }
    }

    /// <summary>
    /// Secili ogrencinin kimlik alanlarini forma yazar. Not/TC/Adres liste ogesinde YOKTUR;
    /// onlar ancak api.GetAsync donunce (Details) bilinir, o yuzden burada TEMIZLENIR --
    /// yoksa onceki ogrencinin notu yeni secimin yaninda durur.
    ///
    /// "Yeni Ogrenci" akisi BOZULMAZ: OpenCreate() SelectedStudent'a hic dokunmaz, kendi
    /// ClearForm() cagrisini bu metottan SONRA yapar; form bos baslar.
    ///
    /// Form ACIKKEN (kullanici yazarken) secim degisirse yazilanlar SILINMEZ: LoadAsync
    /// listeyi yenilerken DataGrid secimi bir an icin null'a ceker; bu null yuzunden
    /// kullanicinin yarim formu ucup gitmemeli.
    /// </summary>
    private void FillFormFromSelection(StudentListItem? item)
    {
        if (IsFormOpen) return;
        FormStudentNo = item?.StudentNo ?? "";
        FormFirstName = item?.FirstName ?? "";
        FormLastName = item?.LastName ?? "";
        FormNationalId = FormAddress = FormNotes = null;
        RaiseForm();
    }
    public StudentDetails? Details
    {
        get => details;
        private set
        {
            if (!Set(ref details, value)) return;
            // Detay gelince (ya da Yeni Ogrenci ile temizlenince) NOT alani da gelir/gider:
            // liste ogesinde not yoktur, kullanici secili ogrencinin notunu Duzenle'ye
            // basmadan gorebilmelidir.
            if (!IsFormOpen) { FormNotes = value?.Notes; Raise(nameof(FormNotes)); }
            IsDeleteArmed = false;
            Raise(nameof(CardActionText)); Raise(nameof(ShowDeactivate)); Raise(nameof(ShowActivate));
            Raise(nameof(FormSubtitle)); Raise(nameof(DetailDepartmentName)); Raise(nameof(DetailJobName)); Raise(nameof(PhotoPath)); Raise(nameof(DetailCardNumber));
            RefreshCommands();
        }
    }
    /// <summary>
    /// Pasiflestir/Aktiflestir dugmelerinden yalnizca uygun olan GORUNUR (ikisi birden
    /// pasif halde durmaz): aktif ogrencide Pasiflestir, pasif ogrencide Aktiflestir.
    ///
    /// PASIFLESTIRME ile SILME AYRILDI. Onceden "Pasiflestir" DELETE /students/{id}
    /// cagiriyordu; sunucu bunu IsDeleted=true ile yapar ve kayit TUM sorgulardan
    /// (global filtre) kaybolur -- "Pasif" filtresinde bile gorunmez, geri alinamaz.
    /// Oysa ekranin "Pasif" filtresi ve rozeti IsActive=false kaydi anlatir (tohumda 25 tane).
    /// Simdi Pasiflestir/Aktiflestir kaydi IsActive ile yeniden yazar (geri alinabilir),
    /// DELETE ise ayri ve onayli "Sil" dugmesindedir.
    /// </summary>
    public bool ShowDeactivate => CanWrite && Details?.IsActive == true;
    public bool ShowActivate => CanWrite && Details?.IsActive == false;
    public StudentDetailTabViewModel? SelectedTab { get => selectedTab; set { if (Set(ref selectedTab, value) && value is not null) _ = value.LoadAsync(); } }
    public bool CanWrite => permissions.Contains("students.write");
    public bool CanDeactivate => permissions.Contains("students.deactivate");
    public bool CanManageCards => permissions.Contains("cards.manage");
    public bool CanReadSensitive => permissions.Contains("students.sensitive.read");
    public bool CanGrantEntitlement => task43Available && permissions.Contains("entitlements.bulk");
    public bool CanSendSms => permissions.Contains("sms.send") && navigation.IsAvailable(ShellRoutes.Sms);
    public string GrantEntitlementReason => CanGrantEntitlement ? string.Empty : "Toplu hakediş yetkisi gerekiyor.";

    /// <summary>
    /// Secili ogrencinin aktif karti yoksa dugme "Kart Ata" der; aksi halde "Kart Degistir".
    /// Ikisi ayni dugmedir cunku kullanici icin is aynidir: "bu ogrenci artik bu karti kullansin".
    /// Sunucuda ise iki ayri uc nokta vardir (atama / degistirme); ayrimi ReplaceCardAsync yapar.
    /// </summary>
    public string CardActionText => HasActiveCard ? "Kart Değiştir" : "Kart Ata";
    private bool HasActiveCard => SelectedStudent is not null && SelectedStudent.Id == Details?.Id
        ? !string.IsNullOrWhiteSpace(SelectedStudent.CardNumber)
        : true;

    // Bu alanlar auto-property DEGIL: her atama PropertyChanged tetiklemeli. Once yalnizca
    // RaiseForm() cagrildiginda bildirim gidiyordu; forma disaridan tek bir alan yazan her
    // yol (ve programatik atama) ekranda GORUNMUYORDU. Set(...) ile atama ve bildirim tek yerde.
    public string FormStudentNo { get => formStudentNo; set { if (Set(ref formStudentNo, value ?? "")) { Raise(nameof(FormSubtitle)); ClearValidationError(); } } }
    public string FormFirstName { get => formFirstName; set { if (Set(ref formFirstName, value ?? "")) ClearValidationError(); } }
    public string FormLastName { get => formLastName; set { if (Set(ref formLastName, value ?? "")) ClearValidationError(); } }
    public string? FormNationalId { get => formNationalId; set { if (Set(ref formNationalId, value)) ClearValidationError(); } }
    public string? FormAddress { get => formAddress; set => Set(ref formAddress, value); }
    public string? FormNotes { get => formNotes; set => Set(ref formNotes, value); }
    /// <summary>Sicil karti alanlari (eski programdaki form): dogum tarihi, parmak izi, PI ID ve dort tanim.</summary>
    public DateTime? FormBirthDate { get => formBirthDate; set { if (Set(ref formBirthDate, value)) ClearValidationError(); } }
    public string? FormFingerprintId { get => formFingerprintId; set => Set(ref formFingerprintId, value); }
    public string? FormPid { get => formPid; set => Set(ref formPid, value); }
    public LookupPickerViewModel FormClass { get; }
    public LookupPickerViewModel FormSection { get; }
    public LookupPickerViewModel FormDepartment { get; }
    public LookupPickerViewModel FormJob { get; }
    /// <summary>Cekmece alt basligi: yeni kayit mi, hangi ogrenci duzenleniyor.</summary>
    public string FormSubtitle => Details is null ? "Yeni öğrenci kaydı" : $"No {Details.StudentNo} · {Details.FirstName} {Details.LastName}";
    /// <summary>Sag panel ozeti: Bolum ve Gorev adlari tanim listelerinden cozulur (Details yalnizca Id tasir).</summary>
    public string? DetailDepartmentName => FormDepartment.NameOf(Details?.DepartmentId) ?? SelectedStudent?.DepartmentName;
    public string? DetailJobName => FormJob.NameOf(Details?.JobId);
    /// <summary>Cekmecedeki salt okunur Kart No: yalnizca secim ve detay AYNI ogrenciyken (bkz. SameStudent).</summary>
    public string? DetailCardNumber => Details is not null && SelectedStudent?.Id == Details.Id ? SelectedStudent.CardNumber : null;
    /// <summary>Cekmecedeki 96px onizleme; fotograf yoksa null ve gri siluet gorunur.</summary>
    public ImageSource? PhotoImage { get => photoImage; private set { if (Set(ref photoImage, value)) { Raise(nameof(HasPhoto)); (RemovePhotoCommand as RelayCommand)?.Refresh(); } } }
    public bool HasPhoto => PhotoImage is not null;
    public string? PhotoError { get => photoError; private set { if (Set(ref photoError, value)) Raise(nameof(HasPhotoError)); } }
    public bool HasPhotoError => !string.IsNullOrWhiteSpace(PhotoError);
    /// <summary>Kaydet'te yuklenecek dosya adi (test/gozlem icin); yoksa null.</summary>
    public string? PendingPhotoName => pendingPhotoName;
    /// <summary>Kaydedilmis fotografin sunucudaki goreli yolu.</summary>
    public string? PhotoPath => Details?.PhotoPath;
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
    public ICommand CancelEditCommand { get; }
    public ICommand SaveStudentCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand GiveLeaveCommand { get; }
    public ICommand ReplaceCardCommand { get; }
    public ICommand ReadCardCommand { get; }
    public ICommand OpenCardWorkflowCommand { get; }
    public ICommand CloseCardWorkflowCommand { get; }
    public ICommand SearchByReadCardCommand { get; }
    public ICommand GrantEntitlementCommand { get; }
    public ICommand OpenStudentDetailCommand { get; }
    public ICommand OpenSmsCommand { get; }
    public ICommand SelectPhotoCommand { get; }
    public ICommand RemovePhotoCommand { get; }

    /// <summary>
    /// Dugmelerin etkin/pasif durumu Details, SelectedStudent ve IsFormOpen'a baglidir; WPF
    /// bir ICommand'i YALNIZCA CanExecuteChanged tetiklenince yeniden sorar. Onceden bu
    /// olay hic tetiklenmiyordu: ogrenci secilince "Duzenle"/"Pasiflestir"/"Kart Degistir"
    /// ilk degerlendirmedeki (Details=null) pasif halinde kaliyordu.
    /// </summary>
    private void RefreshCommands()
    {
        (EditStudentCommand as RelayCommand)?.Refresh();
        (CancelEditCommand as RelayCommand)?.Refresh();
        (SaveStudentCommand as AsyncCommand)?.Refresh();
        (DeactivateCommand as AsyncCommand)?.Refresh();
        (ActivateCommand as AsyncCommand)?.Refresh();
        (DeleteCommand as AsyncCommand)?.Refresh();
        (GiveLeaveCommand as AsyncCommand)?.Refresh();
        (ReplaceCardCommand as AsyncCommand)?.Refresh();
        (GrantEntitlementCommand as RelayCommand)?.Refresh();
        (OpenSmsCommand as RelayCommand)?.Refresh();
        (SelectPhotoCommand as RelayCommand)?.Refresh();
        (RemovePhotoCommand as RelayCommand)?.Refresh();
    }

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
        // Liste yenilenirken DataGrid secimi null'a ceker (Clear); ayni ogrenci yeni sayfada
        // da varsa secim GERI VERILIR. Aksi halde "Yenile"ye her basista form bosaliyordu.
        var keepId = SelectedStudent?.Id;
        try
        {
            var result = await api.SearchAsync(new StudentQuery(Search: Empty(Search), StudentNo: Empty(StudentNo), CardNumber: Empty(CardNumber),
                FirstName: Empty(FirstName), LastName: Empty(LastName), IsActive: IsActive, Page: targetPage, PageSize: PageSize,
                ClassId: routeClassId, ClassName: Empty(ClassId), SectionName: Empty(SectionId), DepartmentName: Empty(DepartmentId), GroupId: routeGroupId));
            Students.Clear(); foreach (var item in result.Items) Students.Add(item);
            Page = result.Page; TotalCount = result.TotalCount;
            if (keepId.HasValue) SelectedStudent = Students.FirstOrDefault(x => x.Id == keepId.Value);
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
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;
                // Listeyi arayuz is parcaciginda degistir (bkz. uiContext aciklamasi).
                if (uiContext is null) await LoadAsync(1);
                else uiContext.Post(_ => { _ = LoadAsync(1); }, null);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void OpenQuickDetail(StudentListItem item) { SelectedStudent = item; IsQuickDetailOpen = true; IsDetailOpen = false; }
    // Form once KAPATILIR: acik formla (IsFormOpen) secim degisirse FillFormFromSelection
    // yazilanlari korumak icin doldurmayi atlar; kullanici baska ogrenciye tikladiysa
    // niyeti bellidir, form o ogrenciye gecmelidir.
    private async Task OpenDetailAsync(StudentListItem item) { IsFormOpen = false; SelectedStudent = item; await OpenDetailByIdAsync(item.Id); }
    private async Task OpenDetailByIdAsync(Guid id)
    {
        IsFormOpen = false;
        Details = await api.GetAsync(id); IsQuickDetailOpen = false; IsDetailOpen = true;
        // Rota ile (orn. Panel'den) acilan detay listede secili olmayabilir; form yine de
        // bu ogrenciyi gostermeli, onceki secimin adini degil.
        if (SelectedStudent?.Id != id) FillFormFromDetails(Details);
        // Fotograf ve tanim adlari (Bolum/Gorev) detayla birlikte gelir; hata olursa panel
        // bos kalir ama ogrenci detayi acilmaya devam eder.
        _ = LoadPhotoAsync(Details);
        if (!lookupsLoaded) _ = EnsureLookupsAsync();
        Tabs.Clear();
        Tabs.Add(new StudentDetailTabViewModel("General", () => Task.FromResult<IReadOnlyList<object>>
            ([new StudentDetailRow($"No: {Details.StudentNo}  |  Ad Soyad: {Details.FirstName} {Details.LastName}  |  Durum: {(Details.IsActive ? "Aktif" : "Pasif")}")])));
        foreach (var name in new[] { "Cards", "Parents", "Entitlements", "Access History", "Leaves", "Holiday/Transfer", "Payments", "SMS History", "Audit" })
            Tabs.Add(new StudentDetailTabViewModel(name, () => api.LoadTabAsync(name, id)));
        SelectedTab = Tabs[0];
    }

    /// <summary>
    /// Yazma isleminden sonra detayi sunucudan taze ceker ve listeyi yeniler. Ogrenci yeni
    /// sayfada yoksa (yeni kayit son sayfaya duser; pasiflestirilen kayit "Aktif" filtresinden
    /// cikar) sunucudan numarasiyla cekilip listenin BASINA eklenir ve secili birakilir:
    /// kullanici az once uzerinde calistigi kaydi ekranda gormeli, "kayit kayboldu" sanmamali.
    /// Sekme listesi yeniden kurulmaz; degisen sekmeler ayrica ReloadTab ile tazelenir.
    /// </summary>
    private async Task RefreshAfterWriteAsync(Guid id)
    {
        var fresh = await api.GetAsync(id);
        await LoadAsync(Page);
        var listed = Students.FirstOrDefault(x => x.Id == id);
        if (listed is null)
        {
            var exact = await api.SearchAsync(new StudentQuery(StudentNo: fresh.StudentNo, IsActive: null, PageSize: 5));
            listed = exact.Items.FirstOrDefault(x => x.Id == id);
            if (listed is not null) Students.Insert(0, listed);
        }
        IsFormOpen = false;
        if (Details?.Id != id || Tabs.Count == 0) await OpenDetailByIdAsync(id);
        else
        {
            Details = fresh;
            // "Genel" sekmesi Details'ten uretilir; Ad/Durum degistiyse yeniden yazilmali.
            await ReloadTabAsync("General");
        }
        SelectedStudent = listed;
        FillFormFromSelection(listed);
        FormNotes = fresh.Notes; Raise(nameof(FormNotes));
        await LoadPhotoAsync(fresh);
    }

    private async Task ReloadTabAsync(string key)
    {
        var index = Tabs.ToList().FindIndex(x => x.Key == key);
        if (index < 0) return;
        var id = Details?.Id ?? Guid.Empty;
        var fresh = key == "General"
            ? new StudentDetailTabViewModel("General", () => Task.FromResult<IReadOnlyList<object>>
                ([new StudentDetailRow($"No: {Details?.StudentNo}  |  Ad Soyad: {Details?.FirstName} {Details?.LastName}  |  Durum: {(Details?.IsActive == true ? "Aktif" : "Pasif")}")]))
            : new StudentDetailTabViewModel(key, () => api.LoadTabAsync(key, id));
        var old = Tabs[index];
        var wasSelected = ReferenceEquals(SelectedTab, old);
        Tabs[index] = fresh;
        // Secili sekme hemen yuklenir; daha once acilmis ama su an secili olmayan sekme de
        // yeniden yuklenir ki kullanici geri dondugunde eski listeyi gormesin.
        if (wasSelected) SelectedTab = fresh; else if (old.IsLoaded) await fresh.LoadAsync();
    }

    /// <summary>
    /// "Yeni Ogrenci" / "Duzenle" Ogrenci Karti cekmecesini acar. Tanim listeleri her
    /// acilista sunucudan yenilenir (Tanimlar ekraninda eklenen bir sube burada da gorunsun);
    /// yukleme bitince secimler Details'e gore yeniden kurulur.
    /// </summary>
    private void OpenCreate()
    {
        IsFormOpen = false; Details = null; ClearForm(); ResetPhotoState(null);
        IsFormOpen = true; IsDetailOpen = true; IsQuickDetailOpen = false;
        _ = PrepareFormAsync(null);
    }
    private void OpenEdit()
    {
        if (Details is null) return;
        FillFormFromDetails(Details); ResetPhotoState(photoBytes); IsFormOpen = true;
        _ = PrepareFormAsync(Details);
    }

    /// <summary>Dort tanim listesini yukler, sonra secimleri verilen kayda gore kurar.</summary>
    private async Task PrepareFormAsync(StudentDetails? source)
    {
        await EnsureLookupsAsync(refresh: true);
        FormClass.Select(source?.ClassId); FormSection.Select(source?.SectionId);
        FormDepartment.Select(source?.DepartmentId); FormJob.Select(source?.JobId);
    }

    private async Task EnsureLookupsAsync(bool refresh = false)
    {
        if (lookupsLoaded && !refresh) return;
        await Task.WhenAll(FormClass.LoadAsync(), FormSection.LoadAsync(), FormDepartment.LoadAsync(), FormJob.LoadAsync());
        lookupsLoaded = true;
        Raise(nameof(DetailDepartmentName)); Raise(nameof(DetailJobName));
    }

    private void FillFormFromDetails(StudentDetails d)
    {
        FormStudentNo = d.StudentNo; FormFirstName = d.FirstName; FormLastName = d.LastName;
        FormNationalId = d.NationalId; FormAddress = d.Address; FormNotes = d.Notes;
        FormBirthDate = d.BirthDate?.ToDateTime(TimeOnly.MinValue); FormFingerprintId = d.FingerprintId; FormPid = d.Pid;
        FormClass.Select(d.ClassId); FormSection.Select(d.SectionId); FormDepartment.Select(d.DepartmentId); FormJob.Select(d.JobId);
        RaiseForm();
    }

    /// <summary>
    /// Sunucudaki fotografi indirir ve onizlemeyi kurar. Fotografsiz kayitta indirme YAPILMAZ
    /// (404 beklemek yerine PhotoPath'e bakilir). Baska bir ogrenciye gecildiyse gec gelen
    /// yanit yok sayilir; aksi halde A'nin fotografi B'nin kartinda gorunurdu.
    /// </summary>
    private async Task LoadPhotoAsync(StudentDetails? d)
    {
        photoBytes = null;
        if (d is null || string.IsNullOrWhiteSpace(d.PhotoPath)) { if (!IsFormOpen) ResetPhotoState(null); return; }
        try
        {
            var bytes = await api.DownloadPhotoAsync(d.Id);
            if (Details?.Id != d.Id) return;
            photoBytes = bytes;
            if (!IsFormOpen) ResetPhotoState(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException or ApiRequestException)
        { PhotoError = "Fotoğraf alınamadı."; }
    }

    /// <summary>Bekleyen secim/kaldirma atilir; onizleme verilen (sunucudaki) icerige doner.</summary>
    private void ResetPhotoState(byte[]? serverBytes)
    {
        pendingPhoto = null; pendingPhotoName = null; photoRemoved = false; PhotoError = null;
        PhotoImage = StudentPhotoImage.Create(serverBytes);
        Raise(nameof(PendingPhotoName));
    }

    /// <summary>
    /// "Resim Sec": dosya diyalogundan JPG/PNG alinir, 2 MB siniri istemcide de denetlenir
    /// (sunucu ayrica denetler). Dosya belleğe okunur; disk kilidi birakilmaz.
    /// </summary>
    private void SelectPhoto()
    {
        var path = fileDialog.OpenFile("Fotoğraf Seç", "Resim dosyaları (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png");
        if (string.IsNullOrWhiteSpace(path)) return;
        StagePhoto(path);
    }

    /// <summary>Diyalogsuz yol (test ve surukle-birak icin): dosyayi dogrulayip bekleyen fotograf yapar.</summary>
    public void StagePhoto(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png")) { PhotoError = "Yalnızca JPG ve PNG dosyaları seçilebilir."; return; }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PhotoError = "Resim dosyası okunamadı."; return; }
        if (bytes.LongLength > StudentPhotoService.MaximumBytes) { PhotoError = "Fotoğraf en fazla 2 MB olabilir."; return; }
        var image = StudentPhotoImage.Create(bytes);
        if (image is null) { PhotoError = "Resim dosyası okunamadı."; return; }
        pendingPhoto = bytes; pendingPhotoName = Path.GetFileName(path); photoRemoved = false; PhotoError = null;
        PhotoImage = image; Raise(nameof(PendingPhotoName));
    }

    private void RemovePhoto()
    {
        pendingPhoto = null; pendingPhotoName = null; PhotoError = null;
        photoRemoved = Details?.PhotoPath is not null;
        PhotoImage = null; Raise(nameof(PendingPhotoName));
    }

    /// <summary>Kaydedilen ogrenciye bekleyen fotografi yukler ya da kaldirilan fotografi siler.</summary>
    private async Task CommitPhotoAsync(Guid id)
    {
        if (pendingPhoto is not null)
        {
            await api.UploadPhotoAsync(id, pendingPhotoName ?? "photo.png", pendingPhoto);
            pendingPhoto = null; pendingPhotoName = null;
        }
        else if (photoRemoved) { await api.DeletePhotoAsync(id); photoRemoved = false; }
    }

    /// <summary>
    /// Duzenlemeyi ya da yeni kayit formunu KAYDETMEDEN kapatir; alanlar secili ogrencinin
    /// sunucudaki degerlerine geri doner. Onceden "Iptal" yoktu: yanlis bir seyler yazan
    /// kullanici ya kaydetmek ya da baska bir ogrenciye tiklayip geri gelmek zorundaydi.
    /// </summary>
    private void CancelEdit()
    {
        IsFormOpen = false; ErrorMessage = null;
        // Secilen ama kaydedilmeyen fotograf atilir; onizleme sunucudaki haline doner.
        ResetPhotoState(photoBytes);
        if (Details is not null) FillFormFromDetails(Details);
        else FillFormFromSelection(SelectedStudent);
    }

    private async Task SaveAsync()
    {
        ErrorMessage = ValidateForm(); if (ErrorMessage is not null) return;
        try
        {
            var saved = await api.SaveAsync(Details?.Id, BuildSaveRequest(Details?.IsActive ?? true));
            string? photoFailure = null;
            // Fotograf kayittan SONRA gider (yeni ogrencide kimlik ancak simdi var). Yukleme
            // duserse ogrenci yine kaydedilmistir: form kapanir, hata ayrica soylenir.
            try { await CommitPhotoAsync(saved.Id); }
            catch (Exception ex) when (IsWriteFailure(ex)) { photoFailure = Describe(ex, "Fotoğraf yüklenemedi."); }
            await RefreshAfterWriteAsync(saved.Id);
            if (photoFailure is not null) ErrorMessage = "Öğrenci kaydedildi ancak fotoğraf işlenemedi: " + photoFailure;
        }
        // Form ACIK BIRAKILIR: kullanici numarayi duzeltip yeniden deneyebilmelidir.
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Öğrenci kaydedilemedi."); }
    }

    /// <summary>
    /// PUT /api/students/{id} TAM kaydi bekler: gonderilmeyen alanlar sunucuda null'a
    /// yazilir. Sicil karti artik her alani tasir; HEPSI formdan gider. Tek istisna
    /// fotograf yolu: onu yalnizca fotograf uclari (yukle/sil) degistirir, burada Details'ten
    /// aynen tasinir -- aksi halde adini duzeltmek fotografi SILERDI.
    /// </summary>
    private SaveStudentRequest BuildSaveRequest(bool isActive) => new(
        FormStudentNo, FormFirstName, FormLastName, Empty(FormNationalId),
        BirthDate: FormBirthDate.HasValue ? DateOnly.FromDateTime(FormBirthDate.Value) : null,
        ClassId: FormClass.SelectedId, SectionId: FormSection.SelectedId, DepartmentId: FormDepartment.SelectedId,
        JobId: FormJob.SelectedId, FingerprintId: Empty(FormFingerprintId), Pid: Empty(FormPid),
        Address: Empty(FormAddress), PhotoPath: Details?.PhotoPath, Notes: Empty(FormNotes), IsActive: isActive);

    /// <summary>Pasiflestir/Aktiflestir: form acik degildir, kayit Details'ten AYNEN yeniden yazilir.</summary>
    private static SaveStudentRequest RequestFromDetails(StudentDetails d, bool isActive) => new(
        d.StudentNo, d.FirstName, d.LastName, d.NationalId, d.BirthDate, d.ClassId, d.SectionId, d.DepartmentId,
        d.JobId, d.FingerprintId, d.Pid, d.Address, d.PhotoPath, d.Notes, isActive);

    /// <summary>
    /// Ogrenciyi pasife alir ya da yeniden aktif eder (bkz. ShowDeactivate aciklamasi).
    /// Sunucuda ayri bir uc nokta yoktur; kayit IsActive ile (diger alanlar aynen)
    /// yeniden yazilir. Onceden geri donus yolu hic yoktu.
    /// </summary>
    private async Task SetActiveAsync(bool active, string failure)
    {
        if (Details is null) return;
        try
        {
            var saved = await api.SaveAsync(Details.Id, RequestFromDetails(Details, active));
            await RefreshAfterWriteAsync(saved.Id);
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, failure); }
    }

    /// <summary>
    /// Kaydi SILER (sunucuda IsDeleted; tum listelerden kaybolur, ancak Sicil Aktar ile
    /// yeniden ice aktarilirsa geri gelir). Ilk cagri yalnizca onay ister.
    /// </summary>
    private async Task DeleteAsync()
    {
        if (Details is null) return;
        if (!IsDeleteArmed) { IsDeleteArmed = true; return; }
        try
        {
            var deleted = Details;
            await api.DeactivateAsync(deleted.Id);
            IsDeleteArmed = false; IsFormOpen = false; Details = null; Tabs.Clear(); SelectedTab = null;
            await LoadAsync(Page);
            SelectedStudent = null; ClearForm(); ResetPhotoState(null); photoBytes = null; ErrorMessage = null;
            InfoMessage = $"{deleted.StudentNo} numaralı öğrenci ({deleted.FirstName} {deleted.LastName}) silindi.";
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { IsDeleteArmed = false; ErrorMessage = Describe(ex, "Öğrenci silinemedi."); }
    }
    private async Task GiveLeaveAsync()
    {
        if (Details is null) return;
        try
        {
            await api.GiveLeaveAsync(new CreateLeaveRequest(Details.Id, DateOnly.FromDateTime(LeaveStartsOn), DateOnly.FromDateTime(LeaveEndsOn),
                LeaveType, null, LeaveBehavior, Guid.Empty));
            ErrorMessage = null;
            // Key: API kimligi (Ingilizce); Title artik Turkce oldugu icin arama Key uzerinden.
            // Sekme daha once acilmis olsa bile YENIDEN yuklenir; eski liste yeni izni gostermez.
            await ReloadTabAsync("Leaves");
            SelectedTab = Tabs.FirstOrDefault(x => x.Key == "Leaves");
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "İzin kaydedilemedi."); }
    }
    /// <summary>
    /// Aktif karti olmayan ogrenciye kart ATAR, olana kartini DEGISTIRIR (eski kart pasife
    /// duser). Onceden yalnizca "degistir" ucu cagriliyordu; kartsiz ogrenciye ilk kart
    /// verilemiyor, sunucu "degistirilecek aktif kart bulunamadi" diyordu.
    /// </summary>
    private async Task ReplaceCardAsync()
    {
        if (Details is null || string.IsNullOrWhiteSpace(NewCardNumber)) { ErrorMessage = "Yeni kart numarası zorunludur."; return; }
        try
        {
            var id = Details.Id; var number = NewCardNumber.Trim();
            if (HasActiveCard)
            {
                try { await api.ReplaceCardAsync(id, new ReplaceCardRequest(number, CardReplacementReason.Trim())); }
                // Liste eski kalmis olabilir (kart baska yerden pasiflestirilmis): atama ile devam.
                catch (ApiRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { await api.AssignCardAsync(id, new AssignCardRequest(number)); }
            }
            else await api.AssignCardAsync(id, new AssignCardRequest(number));
            NewCardNumber = ""; Raise(nameof(NewCardNumber)); ErrorMessage = null;
            await RefreshAfterWriteAsync(id);
            await ReloadTabAsync("Cards");
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
    /// <summary>
    /// Kullanici sorunlu alani duzeltince BAYAT dogrulama mesajini siler. Aksi halde
    /// "TC Kimlik No 11 rakam olmalidir." gecerli bir TC girildikten sonra da ekranda
    /// kalir ve kullanici neyin yanlis oldugunu aramaya devam eder.
    /// Yalnizca form ACIKKEN ve mesaj bu formun kendi dogrulamasindan geldiyse temizlenir;
    /// sunucudan gelen yazma hatasi (orn. "numara zaten kayitli") kaybolmamali... o da
    /// kullanici numarayi degistirince anlamsizlasir, bu yuzden ayni yol kullanilir.
    /// </summary>
    private void ClearValidationError() { if (IsFormOpen && HasError) ErrorMessage = null; }

    private string? ValidateForm()
    {
        if (string.IsNullOrWhiteSpace(FormStudentNo) || FormStudentNo.Trim().Length > 32) return "Öğrenci NO alanı 1-32 karakter olmalıdır.";
        if (string.IsNullOrWhiteSpace(FormFirstName) || FormFirstName.Trim().Length > 100) return "Ad alanı zorunludur.";
        if (string.IsNullOrWhiteSpace(FormLastName) || FormLastName.Trim().Length > 100) return "Soyad alanı zorunludur.";
        if (!string.IsNullOrWhiteSpace(FormNationalId) && (FormNationalId.Trim().Length != 11 || !FormNationalId.Trim().All(char.IsDigit))) return "TC Kimlik No 11 rakam olmalıdır.";
        if (FormBirthDate.HasValue && FormBirthDate.Value.Date > DateTime.Today) return "Doğum tarihi gelecekte olamaz.";
        if (FormFingerprintId?.Trim().Length > 64) return "Parmak izi ID en fazla 64 karakter olabilir.";
        if (FormPid?.Trim().Length > 64) return "PI ID en fazla 64 karakter olabilir.";
        if (FormAddress?.Trim().Length > 500) return "Adres en fazla 500 karakter olabilir.";
        return null;
    }
    private void ClearForm()
    {
        FormStudentNo = FormFirstName = FormLastName = ""; FormNationalId = FormAddress = FormNotes = FormFingerprintId = FormPid = null;
        FormBirthDate = null; FormClass.Select(null); FormSection.Select(null); FormDepartment.Select(null); FormJob.Select(null);
        RaiseForm();
    }
    private void RaiseForm()
    {
        Raise(nameof(FormStudentNo)); Raise(nameof(FormFirstName)); Raise(nameof(FormLastName)); Raise(nameof(FormNationalId)); Raise(nameof(FormAddress)); Raise(nameof(FormNotes));
        Raise(nameof(FormFingerprintId)); Raise(nameof(FormPid)); Raise(nameof(FormBirthDate)); Raise(nameof(FormSubtitle));
    }
    private void CloseDrawers() { IsQuickDetailOpen = IsDetailOpen = IsFormOpen = false; }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public void Dispose() { searchDelay?.Cancel(); searchDelay?.Dispose(); cardReadOperation?.Cancel(); cardReadOperation?.Dispose(); GC.SuppressFinalize(this); }
}

public sealed record StudentStatusOption(string Name, bool? Value);

/// <summary>
/// Bayt dizisinden DONMUS (Freeze) bir BitmapImage uretir. Dosya yolundan URI ile
/// yuklemek dosyayi kilitler ve "Kaldir" sonrasi silinemezdi; bellek akisi + OnLoad
/// ile disk aninda serbest kalir. Freeze: goruntu arka plan is parcaciginda uretilse
/// bile arayuz is parcacigindan kullanilabilir.
/// </summary>
public static class StudentPhotoImage
{
    public static ImageSource? Create(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 192;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        { return null; }
    }
}
