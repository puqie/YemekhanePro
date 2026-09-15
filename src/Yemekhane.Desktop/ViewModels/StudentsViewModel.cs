using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Parents;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class StudentDetailTabViewModel(string key, Func<Task<IReadOnlyList<object>>> loader) : ObservableObject
{
    private readonly DataTable table = CreateTable(key);
    private bool isLoaded, isLoading;
    private string? error;
    private StudentBalanceHeadline? balanceHeadline;

    /// <summary>API'ye giden sabit kimlik; ekranda Title gösterilir.</summary>
    public string Key { get; } = key;
    public string Title { get; } = StudentTabFormatter.TabTitle(key);
    public bool IsGeneral => Key == "General";

    /// <summary>
    /// Items eski canlı testler ve durum denetimleri için korunur. Ekran ise aynı kayıtların
    /// hücrelere ayrılmış DataView karşılığını kullanır; böylece gerçek sütun başlıkları,
    /// yatay/dikey kaydırma ve satır seçimi çalışır.
    /// </summary>
    public ObservableCollection<object> Items { get; } = [];
    public DataView TableRows => table.DefaultView;
    public StudentBalanceHeadline? BalanceHeadline
    {
        get => balanceHeadline;
        private set { if (Set(ref balanceHeadline, value)) Raise(nameof(HasBalanceHeadline)); }
    }
    public bool HasBalanceHeadline => BalanceHeadline is not null;
    public bool IsLoaded { get => isLoaded; private set { if (Set(ref isLoaded, value)) Raise(nameof(IsEmpty)); } }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(IsEmpty)); } }
    public bool IsEmpty => IsLoaded && Error is null && Items.Count == 0;
#pragma warning disable CA1822
    public string EmptyText => StudentTabFormatter.EmptyText;
#pragma warning restore CA1822

    public async Task LoadAsync()
    {
        if (IsLoaded || IsLoading) return;
        IsLoading = true; Error = null;
        try
        {
            foreach (var item in await loader())
            {
                Items.Add(item);
                if (item is StudentBalanceHeadline headline) BalanceHeadline = headline;
                else if (item is StudentDetailRow row) AddTableRow(row);
            }
            IsLoaded = true;
        }
        catch (LoginRequiredException) { Error = "Bu sekme için yetkiniz yok veya oturum sona erdi."; }
        catch (Exception ex) { Error = "Sekme verisi alınamadı: " + ex.Message; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    public Task ReloadAsync()
    {
        Items.Clear(); table.Rows.Clear(); BalanceHeadline = null; IsLoaded = false; Error = null;
        return LoadAsync();
    }

    private static DataTable CreateTable(string tab)
    {
        var result = new DataTable(StudentTabFormatter.TabTitle(tab))
        {
            Locale = System.Globalization.CultureInfo.GetCultureInfo("tr-TR"),
            CaseSensitive = false
        };
        foreach (var label in StudentTabFormatter.ColumnLabels(tab)) result.Columns.Add(label, typeof(string));
        return result;
    }

    private void AddTableRow(StudentDetailRow row)
    {
        var cells = row.Cells is { Count: > 0 }
            ? row.Cells
            : [new StudentDetailCell("Açıklama", row.Summary)];
        foreach (var cell in cells)
            if (!table.Columns.Contains(cell.Label)) table.Columns.Add(cell.Label, typeof(string));

        var data = table.NewRow();
        foreach (var cell in cells) data[cell.Label] = cell.Value;
        table.Rows.Add(data);
    }
}

/// <summary>Izin sirasinda hakedise ne olacagi; Value sunucuya giden koddur.</summary>
public sealed record LeaveBehaviorOption(string Name, string Value);

public sealed class StudentsViewModel : ObservableObject, IDisposable
{
    private readonly IStudentApiClient api;
    private readonly ISettingsApiClient? settingsApi;
    private readonly IShellNavigationService navigation;
    private readonly HashSet<string> permissions;
    private readonly ICardReadEventSource cardReadSource;
    private readonly IFileDialogService fileDialog;
    private readonly ITuitionApiClient? statementApi;
    private readonly IStatementFileDialog? statementDialogs;
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
    // Varsayilan "Tümü": once "Aktif" ile aciliyordu ve pasif ogrenci listede HIC
    // gorunmuyordu. Kasada adi gecen bir ogrenciyi burada bulamamak (saha: "Yiğithan
    // Eker kasada var, ogrencilerde yok") tam olarak bundan kaynaklaniyordu.
    private bool? isActive;
    private bool isLoading, isOffline, isQuickDetailOpen, isDetailOpen, isFormOpen, isCardWorkflowOpen;
    private string? cardWorkflowMessage, infoMessage;
    private bool isDeleteArmed;
    private CancellationTokenSource? cardReadOperation;
    // Sayfa boyutu sunucunun izin verdigi ust sinir (200): 50 iken tipik bir okulun
    // sicili ikinci/ucuncu sayfaya tasiyor, kullanici listede olmayan ogrenciyi "kayit
    // yok" saniyordu (saha: "kasada var, ogrencilerde yok").
    private int page = 1, pageSize = 200, totalCount, detailRequestVersion;
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
    private StudentFormSettings studentFormSettings = new();
    private ImageSource? photoImage;
    private DateTime? formBirthDate;
    private string formStudentNo = "", formFirstName = "", formLastName = "";
    private string? formNationalId, formAddress, formNotes, formFingerprintId, formPid;
    private string? formCardNumber, formPrintedNumber;
    // Veli: sicil kartindan girilir. Ogrenciyle birlikte kaydedilir (bkz. CommitParentAsync).
    private string? formParentName, formParentPhone;
    private Guid? parentId;
    private string? savedParentName, savedParentPhone;

    public StudentsViewModel(IStudentApiClient api, IShellNavigationService navigation, IEnumerable<string> permissions,
        bool task43Available = false, ICardReadEventSource? cardReadSource = null, IFileDialogService? fileDialog = null,
        ITuitionApiClient? statementApi = null, IStatementFileDialog? statementDialogs = null,
        ISettingsApiClient? settingsApi = null)
    {
        this.api = api; this.settingsApi = settingsApi; this.navigation = navigation; this.permissions = permissions.ToHashSet(StringComparer.Ordinal);
        this.fileDialog = fileDialog ?? new FileDialogService();
        this.statementApi = statementApi;
        this.statementDialogs = statementDialogs;
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
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanDeactivate && Details is { IsDeleted: false });
        RestoreCommand = new AsyncCommand(RestoreAsync, () => CanWrite && Details?.IsDeleted == true);
        CancelDeleteCommand = new RelayCommand(() => IsDeleteArmed = false, () => IsDeleteArmed);
        // "İzin Ver" artik dogrudan kaydetmez, FORMU ACAR: tarih ve davranis
        // kullaniciya sorulur. Once hicbir sey sorulmadan bugun icin kayit aciliyordu.
        GiveLeaveCommand = new RelayCommand(OpenLeave, () => CanWrite && Details is not null);
        SaveLeaveCommand = new AsyncCommand(GiveLeaveAsync, () => CanWrite && Details is not null);
        CloseLeaveCommand = new RelayCommand(() => IsLeaveOpen = false);
        ReplaceCardCommand = new AsyncCommand(ReplaceCardAsync, () => CanManageCards && Details is not null);
        ReactivateCardCommand = new AsyncCommand(ReactivateCardAsync, () => ShowReactivateCard);
        ReadCardCommand = new AsyncCommand(ReadCardAsync, () => CanManageCards && this.cardReadSource.IsAvailable);
        OpenCardWorkflowCommand = new AsyncCommand(OpenCardWorkflowAsync, () => CanManageCards);
        CloseCardWorkflowCommand = new RelayCommand(CloseCardWorkflow);
        SearchByReadCardCommand = new AsyncCommand(SearchByReadCardAsync, () => !string.IsNullOrWhiteSpace(CardNumber));
        GrantEntitlementCommand = new RelayCommand(GrantEntitlement, () => CanGrantEntitlement && (SelectedStudent is not null || Details is not null));
        OpenStudentDetailCommand = new AsyncCommand(() => SelectedStudent is null ? Task.CompletedTask : OpenDetailAsync(SelectedStudent));
        // Hazir araliklar: kullanici "gecen yil" icin iki tarih yazmak zorunda kalmasin.
        HistoryThisYearCommand = new RelayCommand(() => SetSchoolYear(0));
        HistoryLastYearCommand = new RelayCommand(() => SetSchoolYear(-1));
        HistoryAllCommand = new RelayCommand(SetAllTime);
        OpenSmsCommand = new RelayCommand(OpenSms, () => CanSendSms && (SelectedStudent is not null || Details is not null));
        // Sicil listesinin tamamini Raporlar ekranina acar; secili ogrencinin veliye verilecek
        // ekstresi ise alt detay panelindeki ayri PDF dugmesinden dogrudan kaydedilir.
        ExportCommand = new RelayCommand(() => navigation.Navigate($"{ShellRoutes.Reports}/{Yemekhane.Application.Reports.ReportType.StudentList}"), () => CanExport);
        ExportStatementPdfCommand = new AsyncCommand(ExportStatementPdfAsync,
            () => CanExportStatement && Details is not null && !IsLoading);
    }

    public ObservableCollection<StudentListItem> Students { get; } = [];
    public ObservableCollection<StudentDetailTabViewModel> Tabs { get; } = [];

    /// <summary>
    /// Gecmis sekmelerinin (Hakedisler, Gecis Gecmisi) PAYLASTIGI tarih araligi.
    /// Tek aralik kullanilir cunku kullanici "Ceylin'in gecen yiline bakayim" derken
    /// hem yuklemelerine hem girislerine ayni donem icin bakar; iki ayri kutu olsaydi
    /// biri 2025'te obur 2026'da kalip sessizce yanlis karsilastirma yapilabilirdi.
    /// </summary>
    public static IReadOnlyList<string> HistoryTabKeys { get; } = ["Entitlements", "Access History"];

    /// <summary>Secili sekme gecmis sekmesi mi (tarih kutulari yalnizca o zaman gorunur).</summary>
    public bool IsHistoryTabSelected => SelectedTab is not null && HistoryTabKeys.Contains(SelectedTab.Key);

    public DateTime? HistoryFrom
    {
        get => historyFrom;
        set { if (Set(ref historyFrom, value)) _ = ReloadHistoryTabsAsync(); }
    }

    public DateTime? HistoryTo
    {
        get => historyTo;
        set { if (Set(ref historyTo, value)) _ = ReloadHistoryTabsAsync(); }
    }

    private DateTime? historyFrom = DateTime.Today.AddMonths(-1);
    private DateTime? historyTo = DateTime.Today.AddMonths(1);

    /// <summary>
    /// Hazir araliklar: kullanici "gecen yil" demek icin iki tarih girmek zorunda kalmasin.
    /// Okul yili Eylul'de baslar; "bu ogretim yili" ve "gecen ogretim yili" ona gore hesaplanir.
    /// </summary>
    public ICommand HistoryThisYearCommand { get; private set; } = null!;
    public ICommand HistoryLastYearCommand { get; private set; } = null!;
    public ICommand HistoryAllCommand { get; private set; } = null!;

    /// <summary>
    /// Ogrencinin ogun bazinda ACIK hakedis donemleri: kalan ogun, bitis gunu, yenileme
    /// gunu. Veli telefonda "kac ogun kaldi, ne zaman bitiyor" diye sordugunda bakilacak
    /// yer burasi; once bu bilgi hicbir ekranda yoktu.
    /// </summary>
    public ObservableCollection<EntitlementPeriodViewModel> Periods { get; } = [];
    public bool HasPeriods => Periods.Count > 0;

    /// <summary>
    /// Donem ozeti YUKLENEMEDIYSE gosterilecek metin. Bos kutu ile "hakki yok" ayni
    /// gorunur; kullanici farki bilmelidir.
    /// </summary>
    public string? PeriodsError
    {
        get => periodsError;
        private set { if (Set(ref periodsError, value)) Raise(nameof(HasPeriodsError)); }
    }

    public bool HasPeriodsError => !string.IsNullOrEmpty(PeriodsError);

    private string? periodsError;
    public IReadOnlyList<StudentStatusOption> Statuses { get; } =
        [new("Tümü", null), new("Aktif", true), new("Pasif", false)];

    /// <summary>
    /// Silinen öğrenciler ayrı bir görünümdedir: normal listede görünmezler, buradan
    /// bulunup Geri Al ile kartsız olarak geri alınırlar.
    /// </summary>
    public bool ShowDeleted
    {
        get => showDeleted;
        set
        {
            if (!Set(ref showDeleted, value)) return;
            Raise(nameof(ShowRestore));
            _ = LoadAsync(1);
        }
    }

    private bool showDeleted;

    /// <summary>Geri Al yalnızca silinmiş öğrenci seçiliyken görünür.</summary>
    public bool ShowRestore => CanWrite && Details?.IsDeleted == true;
    public string? Search { get => search; set { if (Set(ref search, value)) DebounceSearch(); } }
    public string? StudentNo { get => studentNo; set => Set(ref studentNo, value); }
    public string? CardNumber { get => cardNumber; set { if (Set(ref cardNumber, value)) (SearchByReadCardCommand as AsyncCommand)?.Refresh(); } }

    /// <summary>Masa tipi okuyucu bagli mi. Cogu okulda yoktur; kart numarasi elle yazilir.</summary>
    public bool HasCardReader => cardReadSource.IsAvailable;
    public string? FirstName { get => firstName; set => Set(ref firstName, value); }
    public string? LastName { get => lastName; set => Set(ref lastName, value); }
    public string? ClassId { get => classId; set => Set(ref classId, value); }
    public string? SectionId { get => sectionId; set => Set(ref sectionId, value); }
    public string? DepartmentId { get => departmentId; set => Set(ref departmentId, value); }
    public bool? IsActive { get => isActive; set => Set(ref isActive, value); }
    // AsyncCommand CommandManager.RequerySuggested'e baglanmaz: CanExecuteChanged yalnizca
    // Refresh() ile tetiklenir. Bu cagrilar eksikti; "Önceki"/"Sonraki" ilk cizildikleri
    // durumda (acilista TotalCount=0) donup kaliyor, 2. sayfaya gecilince "Önceki" gri
    // kaldigi icin kullanici 1. sayfaya donemiyordu.
    public int Page { get => page; private set { if (Set(ref page, value)) { Raise(nameof(PageText)); RefreshPaging(); } } }
    public int PageSize { get => pageSize; set => Set(ref pageSize, value); }
    public int TotalCount { get => totalCount; private set { if (Set(ref totalCount, value)) { Raise(nameof(PageText)); Raise(nameof(IsEmpty)); RefreshPaging(); } } }

    private void RefreshPaging()
    {
        (PreviousPageCommand as AsyncCommand)?.Refresh();
        (NextPageCommand as AsyncCommand)?.Refresh();
    }
    /// <summary>
    /// Sayfa bilgisi. Birden fazla sayfa varsa bu ACIKCA yazilir: kullanici aradigi
    /// ogrenciyi ilk sayfada bulamayinca kaydin hic olmadigini saniyordu.
    /// </summary>
    public string PageText
    {
        get
        {
            var pages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
            var text = $"Sayfa {Page} / {pages} • {TotalCount:N0} kayıt";
            return pages > 1 ? text + " · diğer sayfalar için Sonraki" : text;
        }
    }
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
            Raise(nameof(CardActionText)); Raise(nameof(ShowReactivateCard)); Raise(nameof(DetailCardNumber)); Raise(nameof(DetailDepartmentName));
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
            Raise(nameof(CardActionText)); Raise(nameof(ShowReactivateCard)); Raise(nameof(ShowDeactivate)); Raise(nameof(ShowActivate));
            Raise(nameof(FormSubtitle)); Raise(nameof(DetailDepartmentName)); Raise(nameof(DetailJobName)); Raise(nameof(PhotoPath)); Raise(nameof(DetailCardNumber));
            Raise(nameof(DetailPanelTitle));
            (ExportStatementPdfCommand as AsyncCommand)?.Refresh();
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
    public StudentDetailTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (!Set(ref selectedTab, value)) return;
            Raise(nameof(IsHistoryTabSelected));
            if (value is not null) _ = value.LoadAsync();
        }
    }
    public bool CanWrite => permissions.Contains("students.write");
    public bool CanDeactivate => permissions.Contains("students.deactivate");
    public bool CanManageCards => permissions.Contains("cards.manage");
    public bool CanReadSensitive => permissions.Contains("students.sensitive.read");
    public bool CanGrantEntitlement => task43Available && permissions.Contains("entitlements.bulk");
    public bool ShowDepartmentField => studentFormSettings.ShowDepartment;
    public bool ShowJobField => studentFormSettings.ShowJob;
    public bool ShowAddressField => studentFormSettings.ShowAddress;
    public bool ShowFingerprintIdField => studentFormSettings.ShowFingerprintId;
    public bool ShowPidField => studentFormSettings.ShowPid;
    public bool CanSendSms => permissions.Contains("sms.send") && navigation.IsAvailable(ShellRoutes.Sms);
    /// <summary>Raporlar rotasi yalnizca reports.read ile acilir (App.xaml.cs); dugme de ona bagli.</summary>
    public bool CanExport => navigation.IsAvailable(ShellRoutes.Reports);
    public bool CanExportStatement => permissions.Contains("reports.export") && statementApi is not null && statementDialogs is not null;
    public string DetailPanelTitle => Details is null
        ? "Öğrenci kayıtları"
        : $"{Details.FirstName} {Details.LastName} · Kayıtlar";
    public string GrantEntitlementReason => CanGrantEntitlement ? string.Empty : "Toplu hakediş yetkisi gerekiyor.";

    /// <summary>
    /// Secili ogrencinin aktif karti yoksa dugme "Kart Ata" der; aksi halde "Kart Degistir".
    /// Ikisi ayni dugmedir cunku kullanici icin is aynidir: "bu ogrenci artik bu karti kullansin".
    /// Sunucuda ise iki ayri uc nokta vardir (atama / degistirme); ayrimi ReplaceCardAsync yapar.
    /// </summary>
    public string CardActionText => HasActiveCard ? "Kart Değiştir" : "Kart Ata";
    /// <summary>
    /// "Eski Kartı Geri Aç": secili ogrencinin AKTIF karti yokken gorunur. Kart yanlislikla
    /// degistirilip/pasiflestirilip turnike "Kart pasif" deyince, numarayi yeniden yazmaya
    /// gerek kalmadan son pasif kart geri acilir. Once bunun icin hicbir yol yoktu.
    /// </summary>
    public bool ShowReactivateCard => CanManageCards && Details is not null && !HasActiveCard;
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

    /// <summary>
    /// Ogrenci Karti cekmecesindeki kart numarasi.
    ///
    /// <para>
    /// Once cekmecede kart alani HIC YOKTU: kart yalnizca sag panelden, ogrenci
    /// KAYDEDILDIKTEN sonra verilebiliyordu. Yeni ogrenci eklerken kart vermek iki
    /// ayri adim gerektiriyordu ve kullanici cekmecedeki salt okunur kutuyu gorup
    /// "kapali" saniyordu. Kart artik kayitla AYNI ANDA atanir.
    /// </para>
    /// </summary>
    public string? FormCardNumber { get => formCardNumber; set => Set(ref formCardNumber, value); }

    /// <summary>Kartin ON yuzundeki basili numara (orn. 6296); kayip kart bulununca sahibini bulmak icin.</summary>
    public string? FormPrintedNumber { get => formPrintedNumber; set => Set(ref formPrintedNumber, value); }
    /// <summary>Sicil karti alanlari (eski programdaki form): dogum tarihi, parmak izi, PI ID ve dort tanim.</summary>
    public DateTime? FormBirthDate { get => formBirthDate; set { if (Set(ref formBirthDate, value)) ClearValidationError(); } }
    public string? FormFingerprintId { get => formFingerprintId; set => Set(ref formFingerprintId, value); }
    public string? FormPid { get => formPid; set => Set(ref formPid, value); }
    /// <summary>
    /// Veli adi ve telefonu. Eski programin Sicil Karti'nda vardi; bizde veli YALNIZCA CSV ice
    /// aktarimindan ("Veli" sabit adiyla) girebiliyordu. Otomatik SMS veli telefonuna dayandigi
    /// icin elle acilan ogrenciye hicbir zaman SMS gonderilemiyordu.
    /// </summary>
    public string? FormParentName { get => formParentName; set { if (Set(ref formParentName, value)) ClearValidationError(); } }
    public string? FormParentPhone { get => formParentPhone; set { if (Set(ref formParentPhone, value)) ClearValidationError(); } }
    public LookupPickerViewModel FormClass { get; }
    public LookupPickerViewModel FormSection { get; }
    public LookupPickerViewModel FormDepartment { get; }
    public LookupPickerViewModel FormJob { get; }
    /// <summary>Cekmece alt basligi: yeni kayit mi, hangi ogrenci duzenleniyor.</summary>
    public string FormSubtitle => Details is null ? "Yeni öğrenci kaydı" : (string.IsNullOrWhiteSpace(Details.StudentNo)
            ? $"{Details.FirstName} {Details.LastName}"
            : $"No {Details.StudentNo} · {Details.FirstName} {Details.LastName}");
    /// <summary>Sag panel ozeti: Bolum ve Gorev adlari tanim listelerinden cozulur (Details yalnizca Id tasir).</summary>
    public string? DetailDepartmentName => FormDepartment.NameOf(Details?.DepartmentId) ?? SelectedStudent?.DepartmentName;
    public string? DetailJobName => FormJob.NameOf(Details?.JobId);
    /// <summary>Cekmecedeki salt okunur Kart No: yalnizca secim ve detay AYNI ogrenciyken (bkz. SameStudent).</summary>
    public string? DetailCardNumber => Details is not null && SelectedStudent?.Id == Details.Id ? SelectedStudent.CardNumber : null;
    public string? DetailPrintedNumber => Details is not null && SelectedStudent?.Id == Details.Id ? SelectedStudent.PrintedNumber : null;
    /// <summary>Cekmecedeki 96px onizleme; fotograf yoksa null ve gri siluet gorunur.</summary>
    public ImageSource? PhotoImage { get => photoImage; private set { if (Set(ref photoImage, value)) { Raise(nameof(HasPhoto)); (RemovePhotoCommand as RelayCommand)?.Refresh(); } } }
    public bool HasPhoto => PhotoImage is not null;
    public string? PhotoError { get => photoError; private set { if (Set(ref photoError, value)) Raise(nameof(HasPhotoError)); } }
    public bool HasPhotoError => !string.IsNullOrWhiteSpace(PhotoError);
    /// <summary>Kaydet'te yuklenecek dosya adi (test/gozlem icin); yoksa null.</summary>
    public string? PendingPhotoName => pendingPhotoName;
    /// <summary>Kaydedilmis fotografin sunucudaki goreli yolu.</summary>
    public string? PhotoPath => Details?.PhotoPath;
    /// <summary>
    /// IZIN FORMU. Bu dort alan EKRANA BAGLI DEGILDI: "İzin Ver" dugmesi kullaniciya
    /// hicbir sey sormadan HEP BUGUN icin, turu "Mazeret", davranisi "Keep" bir kayit
    /// aciyordu. Bir haftalik rapor izni girmek isteyen memur tek gunluk izin yaziyor,
    /// hicbir hak iptal edilmiyor ya da aktarilmiyordu. Sunucunun destekledigi
    /// "Cancel" ve "NextBusinessDay" davranislari masaustunden ERISILEMEZDI.
    /// </summary>
    public string LeaveType { get => leaveType; set => Set(ref leaveType, value); }
    public DateTime LeaveStartsOn
    {
        get => leaveStartsOn;
        // Bitis baslangictan once kalmasin: kullanici ileri bir baslangic secince
        // bitis de birlikte kayar, yoksa sunucu dogrulamasi reddederdi.
        set { if (Set(ref leaveStartsOn, value) && leaveEndsOn < value) LeaveEndsOn = value; }
    }
    public DateTime LeaveEndsOn { get => leaveEndsOn; set => Set(ref leaveEndsOn, value); }
    public string LeaveBehavior { get => leaveBehavior; set => Set(ref leaveBehavior, value); }

    private string leaveType = "Mazeret";
    private DateTime leaveStartsOn = DateTime.Today;
    private DateTime leaveEndsOn = DateTime.Today;
    private string leaveBehavior = "Keep";
    private bool isLeaveOpen;

    /// <summary>Izin cekmecesi acik mi.</summary>
    public bool IsLeaveOpen { get => isLeaveOpen; private set => Set(ref isLeaveOpen, value); }

    /// <summary>Izin turleri; sunucu serbest metin kabul eder, liste yaygin secenekleri verir.</summary>
    public IReadOnlyList<string> LeaveTypes { get; } = ["Mazeret", "Rapor", "Gezi", "Diğer"];

    /// <summary>
    /// Hakedis davranisi. Sunucudaki liste ile AYNI olmalidir
    /// (LeaveService.Behaviors = Keep, Cancel, NextBusinessDay).
    /// </summary>
    public IReadOnlyList<LeaveBehaviorOption> LeaveBehaviors { get; } =
    [
        new("Hakları koru (yemek hakkı durur)", "Keep"),
        new("Hakları iptal et (tahsilat iade edilir)", "Cancel"),
        new("Sonraki iş gününe aktar", "NextBusinessDay"),

    ];
    public string NewCardNumber { get; set; } = "";
    public string NewPrintedNumber { get; set; } = "";
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
    public ICommand RestoreCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand GiveLeaveCommand { get; }
    public ICommand SaveLeaveCommand { get; }
    public ICommand CloseLeaveCommand { get; }
    public ICommand ReplaceCardCommand { get; }
    public ICommand ReactivateCardCommand { get; }
    public ICommand ReadCardCommand { get; }
    public ICommand OpenCardWorkflowCommand { get; }
    public ICommand CloseCardWorkflowCommand { get; }
    public ICommand SearchByReadCardCommand { get; }
    public ICommand GrantEntitlementCommand { get; }
    public ICommand OpenStudentDetailCommand { get; }
    public ICommand OpenSmsCommand { get; }
    public ICommand SelectPhotoCommand { get; }
    public ICommand RemovePhotoCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportStatementPdfCommand { get; }

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
        (ReactivateCardCommand as AsyncCommand)?.Refresh();
        (GrantEntitlementCommand as RelayCommand)?.Refresh();
        (OpenSmsCommand as RelayCommand)?.Refresh();
        (SelectPhotoCommand as RelayCommand)?.Refresh();
        (RemovePhotoCommand as RelayCommand)?.Refresh();
    }

    public async Task InitializeAsync()
    {
        await RefreshStudentFormSettingsAsync();
        await LoadAsync(1);
    }

    private async Task RefreshStudentFormSettingsAsync()
    {
        if (settingsApi is null) return;
        try
        {
            studentFormSettings = await settingsApi.GetStudentFormAsync();
            Raise(nameof(ShowDepartmentField)); Raise(nameof(ShowJobField)); Raise(nameof(ShowAddressField));
            Raise(nameof(ShowFingerprintIdField)); Raise(nameof(ShowPidField));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException or ApiRequestException)
        {
            // Tercihler yüklenemezse geriye uyumlu varsayılan kullanılır: tüm alanlar görünür.
        }
    }

    public void HandleRoute(string route)
    {
        _ = RefreshStudentFormSettingsAsync();
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
        // Tek karakterde sessizce donmek "Filtrele" dugmesini olu gosteriyordu; kullanici
        // neden hicbir sey olmadigini anlamiyordu. Kural artik ekranda yaziyor.
        if (!string.IsNullOrWhiteSpace(Search) && Search.Trim().Length < 2)
        {
            ErrorMessage = "Aramak için en az 2 karakter yazın (ad, soyad, no, kart, sınıf, şube, bölüm, görev ya da veli).";
            return;
        }
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        // Liste yenilenirken DataGrid secimi null'a ceker (Clear); ayni ogrenci yeni sayfada
        // da varsa secim GERI VERILIR. Aksi halde "Yenile"ye her basista form bosaliyordu.
        var keepId = SelectedStudent?.Id;
        // Ekranda TEK arama kutusu vardir. Sinif/sube/bolum dahil metin eslesmeleri
        // repository'deki Search kapsamindadir; rota ile gelen kimlik filtreleri korunur.
        try
        {
            var result = await api.SearchAsync(new StudentQuery(Search: Empty(Search), StudentNo: Empty(StudentNo),
                CardNumber: Empty(CardNumber), FirstName: Empty(FirstName), LastName: Empty(LastName),
                IsActive: ShowDeleted ? null : IsActive,
                Page: targetPage, PageSize: PageSize, ClassId: routeClassId, GroupId: routeGroupId,
                DeletedOnly: ShowDeleted));
            Students.Clear(); foreach (var item in result.Items) Students.Add(item);
            Page = result.Page; TotalCount = result.TotalCount;
            if (keepId.HasValue) SelectedStudent = Students.FirstOrDefault(x => x.Id == keepId.Value);
        }
        catch (LoginRequiredException) { ErrorMessage = "Öğrencileri görüntülemek için students.read izni olan bir oturum gerekiyor."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        { IsOffline = true; ErrorMessage = "Öğrenci verileri alınamadı. API bağlantısını kontrol edin."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    private async Task ExportStatementPdfAsync()
    {
        if (Details is null || statementApi is null || statementDialogs is null) return;
        var from = AsDate(HistoryFrom) ?? new DateOnly(2000, 1, 1);
        var to = AsDate(HistoryTo) ?? DateOnly.FromDateTime(DateTime.Today);
        if (to < from) { ErrorMessage = "PDF için bitiş tarihi başlangıçtan önce olamaz."; return; }

        var identity = string.IsNullOrWhiteSpace(Details.StudentNo) ? Details.Id.ToString("N") : Details.StudentNo;
        var safeIdentity = new string(identity.Where(char.IsLetterOrDigit).ToArray());
        var path = statementDialogs.ChoosePdfPath($"ekstre-{safeIdentity}-{from:yyyyMMdd}");
        if (string.IsNullOrWhiteSpace(path)) return;

        ErrorMessage = InfoMessage = null;
        try
        {
            await statementApi.DownloadStatementPdfAsync(Details.Id, from, to, path);
            InfoMessage = "Öğrenci ekstresi PDF olarak kaydedildi: " + path;
        }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or LoginRequiredException)
        { ErrorMessage = "PDF kaydedilemedi. Bağlantıyı ve dosya yolunu kontrol edin."; }
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
        var version = ++detailRequestVersion;
        var loaded = await api.GetAsync(id);
        // Yalnızca son detay isteği ekrana yazabilir. Bu, hem liste tıklamalarında hem
        // art arda gelen doğrudan rotalarda geç dönen eski öğrenciyi engeller.
        if (version != detailRequestVersion) return;
        if (SelectedStudent is not null && SelectedStudent.Id != id) return;
        Details = loaded; IsQuickDetailOpen = false; IsDetailOpen = true;
        // Rota ile (orn. Panel'den) acilan detay listede secili olmayabilir; form yine de
        // bu ogrenciyi gostermeli, onceki secimin adini degil.
        if (SelectedStudent?.Id != id) FillFormFromDetails(Details);
        // Fotograf ve tanim adlari (Bolum/Gorev) detayla birlikte gelir; hata olursa panel
        // bos kalir ama ogrenci detayi acilmaya devam eder.
        _ = LoadPhotoAsync(Details);
        // Dönem özeti Genel tablonun tarih/hak sütunlarını da besler; önce yüklenir.
        // Hata olsa bile LoadPeriodsAsync bunu kullanıcıya bildirir ve detay açılmaya devam eder.
        await LoadPeriodsAsync(id);
        if (version != detailRequestVersion) return;
        if (!lookupsLoaded) _ = EnsureLookupsAsync();
        Tabs.Clear();
        Tabs.Add(new StudentDetailTabViewModel("General", () => Task.FromResult<IReadOnlyList<object>>(
            CreateGeneralRows(Details, Periods))));
        foreach (var name in new[] { "Cards", "Parents", "Entitlements", "Access History", "Leaves", "Holiday/Transfer", "Payments", "Balance", "SMS History", "Audit" })
            Tabs.Add(new StudentDetailTabViewModel(name, () => LoadTabAsync(name, id)));
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
        // Ad/soyad/numara değişikliği formda ve listede ANINDA görünür: taze kayıt
        // seçili satırın üzerine yazılır, aksi halde kullanıcı "düzenlenmedi" sanıyordu.
        FillFormFromDetails(fresh);
        FormNotes = fresh.Notes; Raise(nameof(FormNotes));
        Raise(nameof(ShowRestore));
        await LoadPhotoAsync(fresh);
    }

    private static object[] CreateGeneralRows(StudentDetails? details,
        IEnumerable<EntitlementPeriodViewModel> periods)
    {
        var no = details?.StudentNo ?? "";
        var name = details is null ? "" : $"{details.FirstName} {details.LastName}".Trim();
        var status = details?.IsActive == true ? "Aktif" : "Pasif";
        var items = periods.ToArray();
        if (items.Length == 0)
        {
            return
            [
                new StudentDetailRow($"No: {no} | Ad Soyad: {name} | Durum: {status} | Aktif hakediş yok",
                [
                    new StudentDetailCell("No", no),
                    new StudentDetailCell("Ad Soyad", name),
                    new StudentDetailCell("Durum", status),
                    new StudentDetailCell("Öğün", "Aktif hakediş yok"),
                    new StudentDetailCell("Başlangıç", "-"),
                    new StudentDetailCell("Bitiş", "-"),
                    new StudentDetailCell("Toplam", "0"),
                    new StudentDetailCell("Kullanılan", "0"),
                    new StudentDetailCell("Kalan", "0"),
                    new StudentDetailCell("Yenileme", "-"),
                    new StudentDetailCell("Hak Durumu", "Hakediş bekleniyor"),
                ])
            ];
        }

        return items.Select(period => (object)new StudentDetailRow(
            $"{period.MealName}: {period.FirstDate:dd.MM.yyyy} - {period.LastDate:dd.MM.yyyy} | Kalan: {period.RemainingQuantity:N0}",
            [
                new StudentDetailCell("No", no),
                new StudentDetailCell("Ad Soyad", name),
                new StudentDetailCell("Durum", status),
                new StudentDetailCell("Öğün", period.MealName),
                new StudentDetailCell("Başlangıç", period.FirstDate.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)),
                new StudentDetailCell("Bitiş", period.LastDate.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)),
                new StudentDetailCell("Toplam", period.TotalQuantity.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
                new StudentDetailCell("Kullanılan", period.ConsumedQuantity.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
                new StudentDetailCell("Kalan", period.RemainingQuantity.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
                new StudentDetailCell("Yenileme", period.RenewFrom.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)),
                new StudentDetailCell("Hak Durumu", period.DaysLeftText),
            ])).ToArray();
    }

    /// <summary>
    /// Sekmeyi yukler; GECMIS sekmelerine secili tarih araligini gecirir. Diger sekmeler
    /// araligi gormez -- kartlar, veliler ve denetim kaydi tarih suzgeciyle daralmamalidir.
    /// </summary>
    private Task<IReadOnlyList<object>> LoadTabAsync(string key, Guid studentId) =>
        HistoryTabKeys.Contains(key)
            ? api.LoadTabAsync(key, studentId, AsDate(HistoryFrom), AsDate(HistoryTo))
            : api.LoadTabAsync(key, studentId);

    private static DateOnly? AsDate(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    /// <summary>
    /// Ogretim yili araligi: 1 Eylul - 31 Agustos. <paramref name="offset"/> 0 ise icinde
    /// bulunulan yil, -1 ise bir onceki. Takvim yili degil OGRETIM yili kullanilir:
    /// Eylul'de acilan kayit Haziran'da hala ayni yila aittir.
    /// </summary>
    private void SetSchoolYear(int offset)
    {
        var today = DateTime.Today;
        // Eylul'den once isek icinde bulundugumuz ogretim yili GECEN yil basladi.
        var startYear = (today.Month >= 9 ? today.Year : today.Year - 1) + offset;
        SetRange(new DateTime(startYear, 9, 1), new DateTime(startYear + 1, 8, 31));
    }

    /// <summary>Tum kayitlar: uygulamanin makul en eski tarihinden bugunun bir yil sonrasina.</summary>
    private void SetAllTime() => SetRange(new DateTime(2000, 1, 1), DateTime.Today.AddYears(1));

    /// <summary>
    /// Iki tarihi TEK seferde yazar ve sekmeleri BIR KEZ tazeler. Ayri ayri atansaydi
    /// her atama kendi yenilemesini baslatir, ilk yenileme eski ikinci tarihle giderdi.
    /// </summary>
    private void SetRange(DateTime from, DateTime to)
    {
        historyFrom = from;
        historyTo = to;
        Raise(nameof(HistoryFrom));
        Raise(nameof(HistoryTo));
        _ = ReloadHistoryTabsAsync();
    }

    /// <summary>Tarih araligi degisince YUKLENMIS gecmis sekmeleri tazelenir.</summary>
    private async Task ReloadHistoryTabsAsync()
    {
        foreach (var key in HistoryTabKeys)
        {
            var tab = Tabs.FirstOrDefault(x => x.Key == key);
            // Hic acilmamis sekme tazelenmez: kullanici oraya gectiginde zaten yuklenir.
            if (tab is not null && tab.IsLoaded) await tab.ReloadAsync();
        }
    }

    private async Task ReloadTabAsync(string key)
    {
        var index = Tabs.ToList().FindIndex(x => x.Key == key);
        if (index < 0) return;
        var id = Details?.Id ?? Guid.Empty;
        var fresh = key == "General"
            ? new StudentDetailTabViewModel("General", () => Task.FromResult<IReadOnlyList<object>>(
                CreateGeneralRows(Details, Periods)))
            : new StudentDetailTabViewModel(key, () => LoadTabAsync(key, id));
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
        detailRequestVersion++;
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
        await LoadParentAsync(source?.Id);
    }

    /// <summary>
    /// Formu ogrencinin BIRINCIL velisiyle doldurur. Veli yoksa alanlar bos kalir ve kaydetmede
    /// yeni veli acilir. Veli okunamazsa form yine acilir: yalnizca veli alanlari bos kalir,
    /// ogrenci duzenlemesi engellenmez.
    /// </summary>
    private async Task LoadParentAsync(Guid? studentId)
    {
        parentId = null; savedParentName = savedParentPhone = null;
        FormParentName = FormParentPhone = null;
        if (studentId is not { } id) { RaiseParentForm(); return; }
        try
        {
            var parents = await api.GetParentsAsync(id);
            var primary = parents.FirstOrDefault(x => x.IsActive && x.IsPrimary)
                ?? parents.FirstOrDefault(x => x.IsActive);
            if (primary is not null)
            {
                parentId = primary.Id;
                savedParentName = FormParentName = primary.Name;
                savedParentPhone = FormParentPhone = primary.Phone;
            }
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { }
        RaiseParentForm();
    }

    private void RaiseParentForm() { Raise(nameof(FormParentName)); Raise(nameof(FormParentPhone)); }

    /// <summary>
    /// Veliyi ogrenciden SONRA kaydeder (yeni ogrencide kimlik ancak simdi vardir). Alanlar
    /// bosaltildiysa mevcut veli pasiflestirilir; degismediyse hic istek gonderilmez.
    /// </summary>
    private async Task CommitParentAsync(Guid id)
    {
        var name = FormParentName?.Trim() ?? "";
        var phone = FormParentPhone?.Trim() ?? "";
        if (name.Length == 0 && phone.Length == 0)
        {
            if (parentId is { } existing) { await api.RemoveParentAsync(existing); parentId = null; }
            savedParentName = savedParentPhone = null;
            return;
        }
        if (string.Equals(name, savedParentName, StringComparison.Ordinal) &&
            string.Equals(phone, savedParentPhone, StringComparison.Ordinal)) return;
        await api.SaveParentAsync(id, parentId, new SaveParentRequest(name, phone));
        savedParentName = name; savedParentPhone = phone;
    }

    /// <summary>
    /// Cekmecedeki kart numarasini kaydeder.
    ///
    /// <para>
    /// Fotograf ve veli gibi KAYITTAN SONRA gonderilir: yeni ogrencide kimlik ancak
    /// o an vardir. Aktif kart varsa DEGISTIRME, yoksa ILK ATAMA ucu cagrilir --
    /// masaustu onceden yalnizca "degistir" ucunu kullaniyordu ve yeni ogrenciye
    /// kart verilemiyordu.
    /// </para>
    /// </summary>
    private async Task CommitCardAsync(Guid id)
    {
        var number = FormCardNumber?.Trim() ?? "";
        var printed = string.IsNullOrWhiteSpace(FormPrintedNumber) ? null : FormPrintedNumber.Trim();
        // Bos birakmak "karti kaldir" demek DEGILDIR: kart kaldirma ayri bir eylemdir
        // (Kartlar sekmesi). Sessizce pasiflestirmek, adini duzelten kullanicinin
        // kartini yok ederdi.
        if (number.Length == 0) return;
        // Zaten ayni numara atanmissa bos yere istek gonderilmez; sunucu bunu
        // "bu kart zaten kullaniliyor" diye reddederdi.
        if (string.Equals(number, DetailCardNumber?.Trim(), StringComparison.Ordinal))
        {
            // Ayni kart; yalnizca baski numarasi degismis olabilir (kart degistirmeden guncellenir).
            var current = string.IsNullOrWhiteSpace(DetailPrintedNumber) ? null : DetailPrintedNumber.Trim();
            if (!string.Equals(printed, current, StringComparison.Ordinal))
                await api.SetPrintedNumberAsync(id, new SetPrintedNumberRequest(printed));
            return;
        }

        if (HasActiveCard)
        {
            try { await api.ReplaceCardAsync(id, new ReplaceCardRequest(number, CardReplacementReason.Trim(), printed)); }
            // Liste eski kalmis olabilir (kart baska yerden pasiflestirilmis): atama ile devam.
            catch (ApiRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            { await api.AssignCardAsync(id, new AssignCardRequest(number, printed)); }
        }
        else await api.AssignCardAsync(id, new AssignCardRequest(number, printed));
        FormCardNumber = null; FormPrintedNumber = null;
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
        // Mevcut kart numarasi forma tasinir: bos gelseydi kullanici karti olan bir
        // ogrenciyi duzenlerken alani bos gorup "kart yok" sanirdi.
        FormCardNumber = SelectedStudent?.Id == d.Id ? SelectedStudent.CardNumber : null;
        FormPrintedNumber = SelectedStudent?.Id == d.Id ? SelectedStudent.PrintedNumber : null;
        RaiseForm();
    }

    /// <summary>
    /// Ogrencinin hakedis donemlerini yukler. Yetki yoksa ya da uc erisilemezse kutu
    /// sessizce bos kalir: ogrenci detayinin acilmasi buna bagli degildir.
    /// </summary>
    private async Task LoadPeriodsAsync(Guid studentId)
    {
        Periods.Clear();
        PeriodsError = null;
        Raise(nameof(HasPeriods));
        try
        {
            var periods = await api.PeriodsAsync(studentId);
            // Yanit gecikirken kullanici baska ogrenciye tiklamis olabilir; o zaman bu
            // sonuc ARTIK YANLIS OGRENCIYE aittir ve yazilmamalidir.
            if (Details?.Id != studentId) return;
            foreach (var period in periods) Periods.Add(new EntitlementPeriodViewModel(period));
        }
        catch (Exception exception)
        {
            // Detay ekranini ENGELLEMEZ ama SESSIZ de kalmaz: bos kutu ile "hakki yok"
            // gorsel olarak AYNIDIR ve kullanici hakki oldugu halde ikinci kez yukleme
            // yapabilir -- bu, daha once yasanan hatanin ta kendisidir.
            if (Details?.Id == studentId) PeriodsError = "Hakediş özeti yüklenemedi: " + exception.Message;
        }
        Raise(nameof(HasPeriods));
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
            var isNew = Details is null;
            var saved = await api.SaveAsync(Details?.Id, BuildSaveRequest(Details?.IsActive ?? true));
            string? photoFailure = null;
            // Fotograf kayittan SONRA gider (yeni ogrencide kimlik ancak simdi var). Yukleme
            // duserse ogrenci yine kaydedilmistir: form kapanir, hata ayrica soylenir.
            try { await CommitPhotoAsync(saved.Id); }
            catch (Exception ex) when (IsWriteFailure(ex)) { photoFailure = Describe(ex, "Fotoğraf yüklenemedi."); }
            // Veli de kayittan SONRA gider (yeni ogrencide kimlik ancak simdi var). Duserse
            // ogrenci yine kaydedilmistir; hata ayrica soylenir ki kullanici SMS'in neden
            // gitmeyecegini bilsin.
            string? parentFailure = null;
            try { await CommitParentAsync(saved.Id); }
            catch (Exception ex) when (IsWriteFailure(ex)) { parentFailure = Describe(ex, "Veli kaydedilemedi."); }
            // Kart da kayittan SONRA gider: yeni ogrencide kimlik ancak simdi vardir.
            // Duserse ogrenci yine kaydedilmistir; hata ayrica soylenir ki kullanici
            // kartin neden atanmadigini bilsin ve sag panelden yeniden deneyebilsin.
            string? cardFailure = null;
            try { await CommitCardAsync(saved.Id); }
            catch (Exception ex) when (IsWriteFailure(ex)) { cardFailure = Describe(ex, "Kart atanamadı."); }
            await RefreshAfterWriteAsync(saved.Id);
            InfoMessage = isNew
                ? $"{saved.FirstName} {saved.LastName} kaydedildi."
                : $"{saved.FirstName} {saved.LastName} bilgileri güncellendi.";
            if (photoFailure is not null) ErrorMessage = "Öğrenci kaydedildi ancak fotoğraf işlenemedi: " + photoFailure;
            if (parentFailure is not null)
                ErrorMessage = (ErrorMessage is null ? "" : ErrorMessage + " ") + "Öğrenci kaydedildi ancak veli işlenemedi: " + parentFailure;
            if (cardFailure is not null)
                ErrorMessage = (ErrorMessage is null ? "" : ErrorMessage + " ") + "Öğrenci kaydedildi ancak kart atanamadı: " + cardFailure;
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
            InfoMessage = $"{deleted.FirstName} {deleted.LastName} silindi; kart zimmeti kaldırıldı ve kart numarası yeniden kullanılabilir. "
                + "Geri almak için \"Silinenleri göster\" kutusunu işaretleyin.";
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { IsDeleteArmed = false; ErrorMessage = Describe(ex, "Öğrenci silinemedi."); }
    }
    /// <summary>
    /// Silinen öğrenciyi geri alır. Öğrenci AKTİF ama KARTSIZ döner: eski kart zimmeti
    /// silme sırasında serbest bırakıldığı için numara başka öğrenciye verilmiş olabilir.
    /// </summary>
    private async Task RestoreAsync()
    {
        if (Details is null) return;
        try
        {
            var id = Details.Id;
            var restored = await api.RestoreAsync(id);
            ErrorMessage = null;
            ShowDeleted = false;
            await RefreshAfterWriteAsync(id);
            InfoMessage = $"{restored.FirstName} {restored.LastName} geri alındı. Kartsız olarak aktif; gerekirse yeni kart atayın.";
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Öğrenci geri alınamadı."); }
    }

    /// <summary>Izin formunu bugunun tarihiyle acar.</summary>
    private void OpenLeave()
    {
        LeaveStartsOn = DateTime.Today;
        LeaveEndsOn = DateTime.Today;
        LeaveType = "Mazeret";
        LeaveBehavior = "Keep";
        ErrorMessage = null;
        IsLeaveOpen = true;
    }

    private async Task GiveLeaveAsync()
    {
        if (Details is null) return;
        try
        {
            await api.GiveLeaveAsync(new CreateLeaveRequest(Details.Id, DateOnly.FromDateTime(LeaveStartsOn), DateOnly.FromDateTime(LeaveEndsOn),
                LeaveType, null, LeaveBehavior, Guid.Empty));
            ErrorMessage = null;
            IsLeaveOpen = false;
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
            var printed = string.IsNullOrWhiteSpace(NewPrintedNumber) ? null : NewPrintedNumber.Trim();
            if (HasActiveCard)
            {
                try { await api.ReplaceCardAsync(id, new ReplaceCardRequest(number, CardReplacementReason.Trim(), printed)); }
                // Liste eski kalmis olabilir (kart baska yerden pasiflestirilmis): atama ile devam.
                catch (ApiRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { await api.AssignCardAsync(id, new AssignCardRequest(number, printed)); }
            }
            else await api.AssignCardAsync(id, new AssignCardRequest(number, printed));
            NewCardNumber = ""; Raise(nameof(NewCardNumber)); NewPrintedNumber = ""; Raise(nameof(NewPrintedNumber)); ErrorMessage = null;
            await RefreshAfterWriteAsync(id);
            await ReloadTabAsync("Cards");
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Kart değiştirilemedi."); }
    }

    /// <summary>
    /// Son pasif karti geri acar; sunucu acilan karti doner, numarasi mesajda soylenir.
    /// Mesaj TAZELEMEDEN SONRA yazilir: liste yenilenirken secim degisir ve InfoMessage silinir.
    /// </summary>
    private async Task ReactivateCardAsync()
    {
        if (Details is null) return;
        try
        {
            var id = Details.Id;
            var card = await api.ReactivateCardAsync(id);
            ErrorMessage = null;
            await RefreshAfterWriteAsync(id);
            await ReloadTabAsync("Cards");
            InfoMessage = $"{card.CardNumber} numaralı kart yeniden aktif; turnike bu kartı yine tanır.";
        }
        catch (Exception ex) when (IsWriteFailure(ex)) { ErrorMessage = Describe(ex, "Kart geri açılamadı."); }
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
            // Okuyucusu olmayan okul icin bu bir hata DEGIL: numara elle yazilir. Once "okuyucu
            // bulunamadi, cihaz baglantisini kontrol edin" deniyordu; okul "okuyucumuz yok ki" dedi.
            CardWorkflowMessage = "Kart numarasını yazıp Ara'ya basın.";
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
        // Kullanıcının en son tıkladığı satır tek kaynaktır. Detay isteği hâlâ eski
        // öğrenci için dönüyor olsa bile yanlış kişiye hakediş ekranı açılmaz.
        var id = SelectedStudent?.Id ?? Details?.Id;
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
        // Ogrenci numarasi ISTEGE BAGLI: okul bazi ogrenciye numara vermiyor (anasinifi,
        // misafir ogrenci). Kimlik kart numarasiyla saglanir; numara yalnizca kolaylik.
        if (FormStudentNo?.Trim().Length > 32) return "Öğrenci NO alanı en fazla 32 karakter olabilir.";
        if (string.IsNullOrWhiteSpace(FormFirstName) || FormFirstName.Trim().Length > 100) return "Ad alanı zorunludur.";
        if (string.IsNullOrWhiteSpace(FormLastName) || FormLastName.Trim().Length > 100) return "Soyad alanı zorunludur.";
        if (!string.IsNullOrWhiteSpace(FormNationalId) && (FormNationalId.Trim().Length != 11 || !FormNationalId.Trim().All(char.IsDigit))) return "TC Kimlik No 11 rakam olmalıdır.";
        if (FormBirthDate.HasValue && FormBirthDate.Value.Date > DateTime.Today) return "Doğum tarihi gelecekte olamaz.";
        if (FormFingerprintId?.Trim().Length > 64) return "Parmak izi ID en fazla 64 karakter olabilir.";
        if (FormPid?.Trim().Length > 64) return "PI ID en fazla 64 karakter olabilir.";
        if (FormAddress?.Trim().Length > 500) return "Adres en fazla 500 karakter olabilir.";
        // Veli ADI istege baglidir; TELEFON zorunludur. SMS ve kayit telefonla calisir,
        // ad yalnizca gorunumdur. Yalnizca ad girilip telefon bos birakilirsa kayit
        // hicbir ise yaramaz, o yuzden telefon istenir.
        var parentName = FormParentName?.Trim() ?? "";
        var parentPhone = FormParentPhone?.Trim() ?? "";
        if (parentName.Length > 200) return "Veli adı en fazla 200 karakter olabilir.";
        if (parentName.Length > 0 && parentPhone.Length == 0)
            return "Veli telefonu zorunludur (örn. 5321234567).";
        return null;
    }
    private void ClearForm()
    {
        FormStudentNo = FormFirstName = FormLastName = ""; FormNationalId = FormAddress = FormNotes = FormFingerprintId = FormPid = null;
        FormParentName = FormParentPhone = null; parentId = null; savedParentName = savedParentPhone = null;
        FormCardNumber = null; FormPrintedNumber = null;
        FormBirthDate = null; FormClass.Select(null); FormSection.Select(null); FormDepartment.Select(null); FormJob.Select(null);
        RaiseForm();
    }
    private void RaiseForm()
    {
        Raise(nameof(FormStudentNo)); Raise(nameof(FormFirstName)); Raise(nameof(FormLastName)); Raise(nameof(FormNationalId)); Raise(nameof(FormAddress)); Raise(nameof(FormNotes));
        Raise(nameof(FormFingerprintId)); Raise(nameof(FormPid)); Raise(nameof(FormBirthDate)); Raise(nameof(FormSubtitle));
        // Kart numarasi da bildirilmeli: unutulursa form doldurulur ama kutu ekranda
        // ESKI degeri gosterir ve kullanici yanlis karti kaydeder.
        Raise(nameof(FormCardNumber)); Raise(nameof(FormPrintedNumber));
        RaiseParentForm();
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

