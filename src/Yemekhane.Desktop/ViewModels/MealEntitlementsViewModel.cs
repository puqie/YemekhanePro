using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;
using Yemekhane.Application.Students;

namespace Yemekhane.Desktop.ViewModels;

public sealed record EntitlementStatusOption(string Name, string? Value);
public sealed record EntitlementTargetOption(string Name, string Value);

/// <summary>Grup/ogun filtre kutusu ogesi. <c>Id == null</c> = "Tümü" (filtre yok).</summary>
public sealed record EntitlementFilterOption(string Name, Guid? Id);

/// <summary>
/// Elle girilen ogrenci listesini cozer: her parca ya bir kimlik (GUID; listeden
/// secim ve derin baglanti boyle gelir) ya da bir okul numarasidir (kullanicinin
/// elinde olan tek sey). Numaralar sunucuda cozulur; bilinmeyen numara istegi
/// reddeder, bu yuzden burada yalnizca ayristirma yapilir.
/// </summary>
internal static class ManualStudentInput
{
    private static readonly char[] Separators = [',', ';', ' ', '\r', '\n', '\t'];

    public static (Guid[] Ids, string[] Nos) Parse(string? text)
    {
        var ids = new List<Guid>(); var nos = new List<string>();
        foreach (var token in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(token, out var id)) ids.Add(id); else nos.Add(token);
        }
        return (ids.Distinct().ToArray(), nos.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public sealed class MealEntitlementsViewModel : ObservableObject
{
    private readonly IMealEntitlementApiClient api;
    private readonly bool canManage, canBulk;
    private bool isLoading, isOffline, isGrantOpen, isCancelConfirmationOpen;
    private string? errorMessage, statusMessage, status, previewMessage;
    private string? searchText;
    private string dayCountText = "10";
    private int page = 1, pageSize = 50, totalCount, totalQuantity, consumedQuantity, remainingQuantity;
    private string quantityText = "1";
    private DateTime? startsOn = DateTime.Today.AddDays(-7), endsOn = DateTime.Today.AddDays(7);
    private DateTime grantStartsOn = DateTime.Today, grantEndsOn = DateTime.Today;
    private MealTypeDetails? grantMeal;
    private EntitlementFilterOption? selectedMealFilter, selectedGroupFilter;
    private GroupRecord? grantGroup;
    private ClassRecord? grantClass;
    private string targetType = "Manual", grade = "", manualStudentIds = "";
    private bool includeSaturday, includeSunday;
    private EntitlementPreview? preview;
    private EntitlementGrantRequest? previewRequest;

    public MealEntitlementsViewModel(IMealEntitlementApiClient api, IEnumerable<string> permissions, BulkOperationWizardViewModel? bulkWizard = null)
    {
        this.api = api;
        BulkWizard = bulkWizard;
        var values = permissions.ToHashSet(StringComparer.Ordinal);
        canManage = values.Contains("entitlements.manage"); canBulk = values.Contains("entitlements.bulk");
        SearchCommand = new AsyncCommand(() => LoadAsync(1), () => canManage);
        PreviousPageCommand = new AsyncCommand(() => LoadAsync(Page - 1), () => canManage && Page > 1);
        NextPageCommand = new AsyncCommand(() => LoadAsync(Page + 1), () => canManage && Page * PageSize < TotalCount);
        OpenGrantCommand = new RelayCommand(OpenGrant, () => canBulk);
        CloseGrantCommand = new RelayCommand(CloseGrant);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => canBulk);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => canBulk && Preview is not null);
        RequestCancelCommand = new RelayCommand(RequestCancel, () => canManage);
        ConfirmCancelCommand = new AsyncCommand(CancelAsync, () => canManage && SelectedItems.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedItems.Count > 0);
        CloseCancelCommand = new RelayCommand(() => IsCancelConfirmationOpen = false);
        OpenBulkCommand = new RelayCommand(OpenBulk, () => canBulk && BulkWizard is not null);
        SearchStudentsCommand = new AsyncCommand(SearchStudentsAsync, () => canBulk);
        ClearPickerCommand = new RelayCommand(ClearPicker, () => canBulk);
        selectedMealFilter = MealFilters[0]; selectedGroupFilter = GroupFilters[0];
        // Sihirbaz bir islemi uygulayinca ya da geri alinca arkadaki liste ESKI kalmasin:
        // kullanici "Geri Al" dedikten sonra satirlarin hala "Iptal" gorunmesini hata sanir.
        if (BulkWizard is not null && canManage) BulkWizard.Changed += (_, _) => _ = LoadAsync(Page);
    }

    public ObservableCollection<MealEntitlementRowViewModel> Items { get; } = [];
    public ObservableCollection<MealTypeDetails> MealTypes { get; } = [];
    public ObservableCollection<ClassRecord> Classes { get; } = [];
    public ObservableCollection<GroupRecord> Groups { get; } = [];
    /// <summary>Filtre kutulari icin "Tümü" ile baslayan listeler: bos bir acilir kutu "hicbiri" degil "hepsi" demektir, bu ekranda yazmali.</summary>
    public ObservableCollection<EntitlementFilterOption> MealFilters { get; } = [new("Tümü", null)];
    public ObservableCollection<EntitlementFilterOption> GroupFilters { get; } = [new("Tümü", null)];
    /// <summary>
    /// Isaretli satirlar. Secim tablonun kendi satir secimine DEGIL, satirin kendi
    /// IsSelected ozelligine baglidir: DataGridRow.IsSelected'e baglanan onay kutusu,
    /// ayni tiklamayi hem "satiri sec" hem "tiki degistir" diye isleyen DataGrid
    /// yuzunden hemen geri kapaniyordu (SelectionUnit=FullRow + Extended).
    /// </summary>
    public ObservableCollection<MealEntitlementListItem> SelectedItems { get; } = [];
    public IReadOnlyList<EntitlementStatusOption> Statuses { get; } = [new("Tümü", null), new("Aktif", "Active"), new("İptal", "Cancelled"), new("Aktarıldı", "Transferred")];
    public IReadOnlyList<EntitlementTargetOption> TargetTypes { get; } = [new("Manuel öğrenciler", "Manual"), new("Sınıf", "Class"), new("Kademe", "Grade"), new("Grup", "Group"), new("Tüm aktif öğrenciler", "All")];
    public bool CanManage => canManage;
    public bool CanBulk => canBulk;
    public BulkOperationWizardViewModel? BulkWizard { get; }
    /// <summary>
    /// TEK ARAMA metni: ad, ogrenci no, kart no ve sinif adinda birden aranir.
    ///
    /// <para>
    /// Once dort ayri kutu vardi (Ogrenci no / Kart no / Ad soyad / Sinif). Kullanici
    /// aradigi seyin hangi kutuya ait oldugunu bilmek zorundaydi ve yanlis kutuya
    /// yazinca SESSIZCE bos sonuc aliyordu. Dokuz kutu ayrica %125 olcekte ekrandan
    /// tasiyor, sagdaki alanlar goruntunun disinda kaliyordu.
    /// </para>
    /// </summary>
    public string? SearchText { get => searchText; set => Set(ref searchText, value); }
    public string? Status { get => status; set => Set(ref status, value); }
    public DateTime? StartsOn { get => startsOn; set => Set(ref startsOn, value); }
    public DateTime? EndsOn { get => endsOn; set => Set(ref endsOn, value); }
    public EntitlementFilterOption? SelectedMealFilter { get => selectedMealFilter; set { if (Set(ref selectedMealFilter, value)) Raise(nameof(SelectedMeal)); } }
    public EntitlementFilterOption? SelectedGroupFilter { get => selectedGroupFilter; set { if (Set(ref selectedGroupFilter, value)) Raise(nameof(SelectedGroup)); } }
    /// <summary>Secili ogun filtresi (null = Tümü). Kutu ogeleriyle karsilikli eslenir.</summary>
    public MealTypeDetails? SelectedMeal
    {
        get => MealTypes.FirstOrDefault(x => x.Id == SelectedMealFilter?.Id);
        set => SelectedMealFilter = MealFilters.FirstOrDefault(x => x.Id == value?.Id) ?? MealFilters[0];
    }
    public GroupRecord? SelectedGroup
    {
        get => Groups.FirstOrDefault(x => x.Id == SelectedGroupFilter?.Id);
        set => SelectedGroupFilter = GroupFilters.FirstOrDefault(x => x.Id == value?.Id) ?? GroupFilters[0];
    }
    public int Page { get => page; private set { if (Set(ref page, value)) { Raise(nameof(PageText)); (PreviousPageCommand as AsyncCommand)?.Refresh(); (NextPageCommand as AsyncCommand)?.Refresh(); } } }
    public int PageSize { get => pageSize; set => Set(ref pageSize, value); }
    public int TotalCount { get => totalCount; private set { if (Set(ref totalCount, value)) { Raise(nameof(PageText)); Raise(nameof(IsEmpty)); (NextPageCommand as AsyncCommand)?.Refresh(); } } }
    public int TotalQuantity { get => totalQuantity; private set => Set(ref totalQuantity, value); }
    public int ConsumedQuantity { get => consumedQuantity; private set => Set(ref consumedQuantity, value); }
    public int RemainingQuantity { get => remainingQuantity; private set => Set(ref remainingQuantity, value); }
    public string PageText => $"Sayfa {Page} / {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize))} • {TotalCount:N0} kayıt";
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) Raise(nameof(IsEmpty)); } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    /// <summary>
    /// Basarili islem sonucu ("135 hak olusturuldu"). Onceden bu metin cekmecenin icine
    /// yaziliyor, cekmece de ayni anda kapandigi icin kullanici hicbir geri bildirim
    /// gormuyordu.
    /// </summary>
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsEmpty => !IsLoading && !HasError && TotalCount == 0;
    public bool IsGrantOpen { get => isGrantOpen; private set => Set(ref isGrantOpen, value); }
    public bool IsCancelConfirmationOpen { get => isCancelConfirmationOpen; private set => Set(ref isCancelConfirmationOpen, value); }
    public string CancelConfirmationText => $"Seçili {SelectedItems.Count} kullanılmamış hak iptal edilecek. Bu işlem geri alınamaz.";
    public string TargetType { get => targetType; set { if (Set(ref targetType, value)) { Preview = null; RaiseTargetVisibility(); } } }
    public bool IsManualTarget => TargetType == "Manual";
    public bool IsClassTarget => TargetType == "Class";
    public bool IsGradeTarget => TargetType == "Grade";
    public bool IsGroupTarget => TargetType == "Group";
    /// <summary>Kimlik (GUID) ya da okul numarasi; virgul/bosluk/satir ile ayrilir.</summary>
    public string ManualStudentIds { get => manualStudentIds; set { if (Set(ref manualStudentIds, value)) Preview = null; } }
    public ClassRecord? GrantClass { get => grantClass; set { if (Set(ref grantClass, value)) Preview = null; } }
    public GroupRecord? GrantGroup { get => grantGroup; set { if (Set(ref grantGroup, value)) Preview = null; } }
    public string Grade { get => grade; set { if (Set(ref grade, value)) Preview = null; } }
    public MealTypeDetails? GrantMeal { get => grantMeal; set { if (Set(ref grantMeal, value)) { Preview = null; Raise(nameof(GrantMealPriceText)); Raise(nameof(HasGrantMealPrice)); Raise(nameof(CanCharge)); } } }
    /// <summary>
    /// Secili ogunun bedeli ("Öğün bedeli: ₺250,00"). Ucret sifirsa satir gizlenir: ucretsiz
    /// ogunde "₺0,00" yazmak kullaniciya bir hata varmis gibi gorunur.
    /// </summary>
    public bool HasGrantMealPrice => GrantMeal is { Price: > 0 };
    public string GrantMealPriceText => HasGrantMealPrice ? "Öğün bedeli: " + GrantMeal!.Price.ToString("C2", Turkish) : "";
    /// <summary>
    /// Onizlemenin toplam bedeli = ucret x hak adedi x gunluk adet. Sunucunun RightsCount'u
    /// ogrenci x gun sayisidir, gunluk adedi icermez; ayni gun iki ogun verilirse bedel de iki kat.
    /// </summary>
    /// <summary>
    /// Toplam bedeli SUNUCU hesaplar (EntitlementPreview.Total). Ekran kendi carpimini
    /// yapiyordu ve o rakam hicbir yere gitmiyordu; simdi ayni deger hem burada gorunur
    /// hem kasaya yazilir. Eski surumlerden gelen 0 yanit icin ekran hesabi yedek kalir.
    /// </summary>
    public decimal PreviewTotal => Preview is null ? 0
        : Preview.Total > 0 ? Preview.Total
        : previewRequest is null || GrantMeal is null ? 0 : GrantMeal.Price * Preview.RightsCount * previewRequest.Quantity;
    public bool HasPreviewTotal => HasPreview && PreviewTotal > 0;
    public string PreviewTotalText => HasPreviewTotal ? "Toplam bedel: " + PreviewTotal.ToString("C2", Turkish) : "";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private bool chargeToCash, notifyParents;
    private string? studentPickerSearch;
    private bool isPickerBusy;
    /// <summary>Onizleme basina sabit islem kimligi: tekrar denemede tahsilat ikinci kez yazilmaz.</summary>
    private Guid grantOperationId = Guid.NewGuid();
    public DateTime GrantStartsOn { get => grantStartsOn; set { if (Set(ref grantStartsOn, value)) { Preview = null; Raise(nameof(GrantRangeText)); } } }
    public DateTime GrantEndsOn { get => grantEndsOn; set { if (Set(ref grantEndsOn, value)) Preview = null; } }

    /// <summary>
    /// Hakedisin kac GUN surecegi. Kullanici bitis tarihi degil gun sayisi dusunur
    /// ("10 gunluk yemek hakki"); bitis tarihini elle bulmak icin takvime bakip
    /// hafta sonlarini saymak gerekiyordu ve yanlis sayilan her gun eksik ya da
    /// fazla hak olusturuyordu.
    ///
    /// <para>
    /// Metin olarak baglanir: int baglamada "abc" SESSIZCE reddedilip eski deger
    /// kalirdi (gunluk adet kutusunda ayni tuzak yasandi).
    /// </para>
    /// </summary>
    public string DayCountText
    {
        get => dayCountText;
        set { if (Set(ref dayCountText, value)) { Preview = null; Raise(nameof(GrantRangeText)); } }
    }

    /// <summary>
    /// Girilen gun sayisinin hangi tarihte bitecegini soyler. Kullanici "10 gun"
    /// yazdiginda hangi tarihe kadar hak olusacagini ONCEDEN gormelidir.
    /// </summary>
    public string GrantRangeText
    {
        get
        {
            if (!int.TryParse(DayCountText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                || days < 1)
                return "Gün sayısı 1 veya daha büyük bir tam sayı olmalıdır.";
            var end = WorkingDayRange.EndDateFor(
                DateOnly.FromDateTime(GrantStartsOn), days, IncludeSaturday, IncludeSunday);
            return end is null
                ? "Seçilen günlerle bu süre hesaplanamıyor."
                : $"{GrantStartsOn:dd.MM.yyyy} - {end.Value:dd.MM.yyyy} ({days} gün)";
        }
    }
    /// <summary>
    /// Gunluk adet metin olarak baglanir. int'e dogrudan baglansaydi "abc" gibi bir giris
    /// WPF'te sessizce reddedilir, kutu kirmizi cizilir ama ViewModel ESKI degeri tutar;
    /// kullanici "Etkileri Onizle" deyince fark etmeden eski adetle onizleme alirdi.
    /// </summary>
    public string QuantityText { get => quantityText; set { if (Set(ref quantityText, value ?? "")) { Preview = null; Raise(nameof(Quantity)); } } }
    public int Quantity
    {
        get => int.TryParse(quantityText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        set => QuantityText = value.ToString(CultureInfo.InvariantCulture);
    }
    public bool IncludeSaturday { get => includeSaturday; set { if (Set(ref includeSaturday, value)) { Preview = null; Raise(nameof(GrantRangeText)); } } }
    public bool IncludeSunday { get => includeSunday; set { if (Set(ref includeSunday, value)) { Preview = null; Raise(nameof(GrantRangeText)); } } }
    public EntitlementPreview? Preview { get => preview; private set { if (Set(ref preview, value)) { Raise(nameof(HasPreview)); Raise(nameof(PreviewText)); Raise(nameof(PreviewTotal)); Raise(nameof(HasPreviewTotal)); Raise(nameof(PreviewTotalText)); (ApplyCommand as AsyncCommand)?.Refresh(); } } }
    public bool HasPreview => Preview is not null;
    public string PreviewText => Preview is null ? "" : $"{Preview.StudentCount:N0} öğrenci • {Preview.DayCount:N0} gün • {Preview.RightsCount:N0} hak ({Preview.CreatedCount:N0} yeni, {Preview.UpdatedCount:N0} güncelleme)";
    public string? PreviewMessage { get => previewMessage; private set => Set(ref previewMessage, value); }
    public ICommand SearchCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand OpenGrantCommand { get; }
    public ICommand CloseGrantCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RequestCancelCommand { get; }
    public ICommand ConfirmCancelCommand { get; }
    public ICommand CloseCancelCommand { get; }
    public ICommand OpenBulkCommand { get; }

    public async Task InitializeAsync()
    {
        if (!canManage && !canBulk) return;
        try
        {
            var mealTask = api.MealTypesAsync(); var classTask = api.ClassesAsync(); var groupTask = api.GroupsAsync();
            await Task.WhenAll(mealTask, classTask, groupTask);
            foreach (var item in mealTask.Result) { MealTypes.Add(item); MealFilters.Add(new(item.Name, item.Id)); }
            foreach (var item in classTask.Result) Classes.Add(item);
            foreach (var item in groupTask.Result) { Groups.Add(item); GroupFilters.Add(new(item.Name, item.Id)); }
            GrantMeal = MealTypes.FirstOrDefault();
            if (canManage) await LoadAsync(1);
        }
        catch (Exception ex) { HandleError(ex, "Filtre seçenekleri alınamadı."); }
    }

    public void HandleRoute(string route)
    {
        if (!route.StartsWith(ShellRoutes.Entitlements, StringComparison.Ordinal)) return;
        var suffix = route[(ShellRoutes.Entitlements.Length)..].Trim('/');
        if (Guid.TryParse(suffix, out var studentId)) { ManualStudentIds = studentId.ToString("D"); OpenGrant(); }
    }

    /// <summary>Satir onay kutusu her degistiginde secili kume yeniden kurulur.</summary>
    private void RebuildSelection()
    {
        SelectedItems.Clear();
        foreach (var row in Items.Where(x => x.IsSelected)) SelectedItems.Add(row.Item);
        Raise(nameof(CancelConfirmationText)); Raise(nameof(HasSelection));
        (ConfirmCancelCommand as AsyncCommand)?.Refresh();
        (ClearSelectionCommand as RelayCommand)?.Refresh();
    }

    public bool HasSelection => SelectedItems.Count > 0;

    /// <summary>Satir onay kutusu disaridan kurulduysa (testler) secimi yeniden hesaplar.</summary>
    public void RebuildSelectionForTests() => RebuildSelection();
    public ICommand ClearSelectionCommand { get; }

    /// <summary>Tum secimi kaldirir ("Seçimi temizle").</summary>
    public void ClearSelection()
    {
        foreach (var row in Items) row.SetSelectedQuietly(false);
        RebuildSelection();
    }

    public async Task LoadAsync(int targetPage)
    {
        IsLoading = true; IsOffline = false; ErrorMessage = null; StatusMessage = null;
        try
        {
            var result = await api.SearchAsync(new MealEntitlementQuery(ToDate(StartsOn), ToDate(EndsOn),
                StudentNo: null, CardNumber: null, Name: null, ClassName: null,
                SelectedGroupFilter?.Id, SelectedMealFilter?.Id, Status,
                targetPage, PageSize, Search: Empty(SearchText)));
            Items.Clear(); foreach (var item in result.Items) Items.Add(new MealEntitlementRowViewModel(item, RebuildSelection));
            Page = result.Page; TotalCount = result.TotalCount; TotalQuantity = result.Summary.TotalQuantity;
            ConsumedQuantity = result.Summary.ConsumedQuantity; RemainingQuantity = result.Summary.RemainingQuantity;
        }
        catch (Exception ex) { HandleError(ex, "Hakediş listesi alınamadı."); }
        finally
        {
            // Secim HER durumda temizlenir (hata dahil): aksi halde filtreden sonra artik
            // gorunmeyen satirlar secili kalir ve toplu islem onlari da kapsardi.
            RebuildSelection();
            IsLoading = false; Raise(nameof(IsEmpty));
        }
    }

    private void OpenGrant()
    {
        if (SelectedItems.Count > 0) ManualStudentIds = string.Join(", ", SelectedItems.Select(x => x.StudentId).Distinct());
        IsGrantOpen = true; Preview = null; PreviewMessage = null; StatusMessage = null;
        // Ogun ucreti Tanimlar ekraninda degistirilmis olabilir; acilista yuklenen liste eski
        // bedeli gosterirdi. Cekmece hemen acilir, liste arkada tazelenir (beklemek gerekmez).
        _ = RefreshMealTypesAsync();
    }

    /// <summary>
    /// Ogun listesini sunucudan tazeler; yalnizca DEGISEN kayitlar yerine konur (record deger
    /// esitligi). Listeyi bosaltip yeniden doldurmak ComboBox secimini dusurur ve surmekte olan
    /// onizlemeyi sifirlardi; degismeyen kayitlara dokunulmaz. Hata olursa eski liste kalir.
    /// </summary>
    public async Task RefreshMealTypesAsync()
    {
        try
        {
            var fresh = await api.MealTypesAsync();
            foreach (var item in fresh)
            {
                var index = MealTypes.ToList().FindIndex(x => x.Id == item.Id);
                if (index < 0) { MealTypes.Add(item); MealFilters.Add(new(item.Name, item.Id)); continue; }
                if (MealTypes[index] == item) continue;
                var wasSelected = GrantMeal?.Id == item.Id;
                MealTypes[index] = item;
                if (wasSelected) GrantMeal = item;
            }
            for (var i = MealTypes.Count - 1; i >= 0; i--)
                if (fresh.All(x => x.Id != MealTypes[i].Id)) MealTypes.RemoveAt(i);
            if (GrantMeal is null) GrantMeal = MealTypes.FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException or ApiRequestException) { }
    }
    private void CloseGrant() { IsGrantOpen = false; Preview = null; PreviewMessage = null; }
    private void OpenBulk()
    {
        BulkWizard?.Preset(studentIds: SelectedItems.Select(x => x.StudentId).Distinct().ToArray());
        BulkWizard?.OpenCommand.Execute(null);
    }

    private async Task PreviewAsync()
    {
        ErrorMessage = null; PreviewMessage = null;
        try { previewRequest = BuildGrant(); Preview = await api.PreviewAsync(previewRequest); }
        catch (Exception ex) { Preview = null; PreviewMessage = Friendly(ex, "Önizleme oluşturulamadı."); }
    }

    private async Task ApplyAsync()
    {
        if (Preview is null || previewRequest is null) return;
        try
        {
            var result = await api.ApplyAsync(new ApplyEntitlementGrantRequest(previewRequest, Preview.PreviewToken));
            Preview = null; IsGrantOpen = false;
            // Yeni satirlar listede GORUNSUN: filtre araligi verilen araligi kapsamiyorsa
            // (varsayilan +-7 gun) kullanici "uyguladim ama liste degismedi" sanir.
            // Aralik yalnizca genisletilir, daraltilmaz.
            var grantStart = previewRequest.StartsOn.ToDateTime(TimeOnly.MinValue);
            var grantEnd = previewRequest.EndsOn.ToDateTime(TimeOnly.MinValue);
            if (StartsOn is null || StartsOn > grantStart) StartsOn = grantStart;
            if (EndsOn is null || EndsOn < grantEnd) EndsOn = grantEnd;
            if (canManage) await LoadAsync(1);
            // Kasaya yazilan tutar ve SMS sayisi da bildirilir: memur "gelir yansidi mi,
            // veliye gitti mi" diye ayrica kontrol etmek zorunda kalmasin.
            var message = $"{result.CreatedCount:N0} hak oluşturuldu, {result.UpdatedCount:N0} hak güncellendi.";
            if (result.ChargedStudents > 0)
                message += $" Kasaya {result.ChargedStudents:N0} öğrenci için {result.ChargedTotal.ToString("C2", Turkish)} işlendi.";
            if (result.NotifiedParents > 0) message += $" {result.NotifiedParents:N0} veliye SMS kuyruğa alındı.";
            StatusMessage = message;
            // Sonraki hakedis yeni bir islem: ayni kimlikle tekrar tahsilat yazilmaz.
            grantOperationId = Guid.NewGuid();
        }
        catch (Exception ex) { PreviewMessage = Friendly(ex, "Hakedişler uygulanamadı. Yeniden önizleyin."); Preview = null; }
    }

    private void RequestCancel()
    {
        ErrorMessage = null; StatusMessage = null;
        if (SelectedItems.Count == 0) { ErrorMessage = "İptal edilecek hakları seçin."; return; }
        if (SelectedItems.Any(x => x.ConsumedQuantity > 0 || x.Status != "Active"))
        { ErrorMessage = "Kullanılmış veya aktif olmayan haklar iptal edilemez."; return; }
        IsCancelConfirmationOpen = true; Raise(nameof(CancelConfirmationText));
    }

    private async Task CancelAsync()
    {
        try
        {
            var ids = SelectedItems.Select(x => x.Id).Distinct().ToArray();
            var result = await api.CancelAsync(new CancelEntitlementsRequest(ids, ids.Length));
            IsCancelConfirmationOpen = false; await LoadAsync(Page);
            StatusMessage = $"{result.CancelledCount:N0} hak iptal edildi.";
        }
        catch (Exception ex) { IsCancelConfirmationOpen = false; ErrorMessage = Friendly(ex, "Haklar iptal edilemedi."); }
    }

    /// <summary>
    /// Ogun bedeli kasaya OGRENCI BASINA gelir olarak islensin mi. Ucretsiz ogunde
    /// (bedel 0) kutu gorunmez; yazacak tutar yoktur.
    /// </summary>
    public bool ChargeToCash { get => chargeToCash; set { if (Set(ref chargeToCash, value)) Preview = null; } }
    /// <summary>Veliye "hakkiniz tanimlandi" SMS'i gonderilsin mi.</summary>
    public bool NotifyParents { get => notifyParents; set { if (Set(ref notifyParents, value)) Preview = null; } }
    /// <summary>Bedeli olan ogunde ucretlendirme secenekleri gorunur.</summary>
    public bool CanCharge => HasGrantMealPrice;

    /// <summary>
    /// Hakedis listesi ogrenci-GUN satiridir: ayni ogrenci 300 gun icin 300 kez gorunur ve
    /// oradan ogrenci secilemez. Hizli Hakedis kendi ogrenci listesini kullanir; her ogrenci
    /// bir kez, ad/sinif/numara ile aranarak.
    /// </summary>
    public ObservableCollection<StudentPickerRowViewModel> StudentPicker { get; } = [];
    public string? StudentPickerSearch { get => studentPickerSearch; set => Set(ref studentPickerSearch, value); }
    public bool IsPickerBusy { get => isPickerBusy; private set => Set(ref isPickerBusy, value); }
    public bool HasPickerRows => StudentPicker.Count > 0;
    public string PickerSummary => StudentPicker.Count(x => x.IsSelected) is var n && n > 0
        ? $"Seçili: {n} öğrenci"
        : "Ad, sınıf ya da numara yazıp Ara'ya basın.";

    public ICommand SearchStudentsCommand { get; private set; } = null!;
    public ICommand ClearPickerCommand { get; private set; } = null!;

    /// <summary>Ad, soyad, numara, kart ya da SINIF adiyla arar; her ogrenci tek satir.</summary>
    private async Task SearchStudentsAsync()
    {
        var term = StudentPickerSearch?.Trim();
        PreviewMessage = null;
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
        { PreviewMessage = "Aramak için en az 2 karakter yazın (ad, soyad, sınıf, öğrenci no ya da kart no)."; return; }
        IsPickerBusy = true;
        try
        {
            var chosen = StudentPicker.Where(x => x.IsSelected).ToDictionary(x => x.Id, x => x);
            var result = await api.SearchStudentsAsync(term);
            StudentPicker.Clear();
            // Onceki secim KORUNUR: kullanici once 5/A arayip secip sonra 5/B arayabilir.
            foreach (var row in chosen.Values) StudentPicker.Add(row);
            foreach (var item in result.Items.Where(x => !chosen.ContainsKey(x.Id)))
                StudentPicker.Add(new StudentPickerRowViewModel(item, OnPickerChanged));
            if (StudentPicker.Count == chosen.Count) PreviewMessage = "Bu aramayla eşleşen aktif öğrenci bulunamadı.";
            OnPickerChanged();
        }
        catch (Exception ex) { PreviewMessage = Friendly(ex, "Öğrenci aranamadı."); }
        finally { IsPickerBusy = false; }
    }

    private void ClearPicker()
    {
        StudentPicker.Clear();
        StudentPickerSearch = null;
        OnPickerChanged();
    }

    /// <summary>Secim degisince manuel numara kutusu da guncellenir; ikisi tek kaynak olur.</summary>
    private void OnPickerChanged()
    {
        Raise(nameof(HasPickerRows)); Raise(nameof(PickerSummary));
        // Yalnizca NUMARASI OLAN secimler kutuya yazilir; numarasiz ogrenci kimligiyle
        // gonderilir (bkz. BuildGrant). Bos numara yazmak listeyi bozardi.
        var selected = StudentPicker.Where(x => x.IsSelected && !string.IsNullOrWhiteSpace(x.StudentNo))
            .Select(x => x.StudentNo).ToArray();
        if (selected.Length > 0) ManualStudentIds = string.Join(", ", selected);
        Preview = null;
    }

    private EntitlementGrantRequest BuildGrant()
    {
        if (GrantMeal is null) throw new InvalidOperationException("Öğün seçilmelidir.");
        if (!int.TryParse(QuantityText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity is < 1 or > 10)
            throw new InvalidOperationException("Günlük adet 1-10 arasında bir tam sayı olmalıdır.");
        // Bitis tarihi GUN SAYISINDAN hesaplanir: kullanici "10 gun" der, sistem
        // hafta sonlarini atlayarak 10 dolu gun bulur. Boylece "10 gun" her zaman
        // 10 hak demektir; takvim gunu sayilsaydi hafta sonuna denk gelen istekte
        // hak sayisi degisirdi.
        if (!int.TryParse(DayCountText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayCount)
            || dayCount < 1)
            throw new InvalidOperationException("Gün sayısı 1 veya daha büyük bir tam sayı olmalıdır.");
        var computedEnd = WorkingDayRange.EndDateFor(
            DateOnly.FromDateTime(GrantStartsOn), dayCount, IncludeSaturday, IncludeSunday)
            ?? throw new InvalidOperationException("Seçilen günlerle bu süre hesaplanamıyor. Cumartesi/Pazar seçimini gözden geçirin.");
        var (ids, nos) = ManualStudentInput.Parse(ManualStudentIds);
        // Listeden secilen ogrencilerin KIMLIGI kullanilir: numarasi olmayan ogrenci
        // (anasinifi, misafir) numara kutusuna hicbir sey yazamaz ve eskiden bu ekrandan
        // hakedis alamiyordu.
        var picked = StudentPicker.Where(x => x.IsSelected).Select(x => x.Id).ToArray();
        if (picked.Length > 0) ids = [.. ids.Concat(picked).Distinct()];
        if (IsManualTarget && ids.Length == 0 && nos.Length == 0)
            throw new InvalidOperationException("Listeden öğrenci seçin ya da öğrenci numaralarını yazın (örn. 5012, 5013).");
        if (IsClassTarget && GrantClass is null) throw new InvalidOperationException("Sınıf seçilmelidir.");
        if (IsGroupTarget && GrantGroup is null) throw new InvalidOperationException("Grup seçilmelidir.");
        if (IsGradeTarget && string.IsNullOrWhiteSpace(Grade)) throw new InvalidOperationException("Kademe / sınıf seviyesi girilmelidir.");
        var target = new EntitlementTarget(TargetType, IsManualTarget ? ids : [], GrantClass?.Id, Empty(Grade), GrantGroup?.Id,
            IsManualTarget && nos.Length > 0 ? nos : null);
        return new EntitlementGrantRequest(target, GrantMeal.Id, DateOnly.FromDateTime(GrantStartsOn),
            computedEnd, quantity, IncludeSaturday, IncludeSunday, "WPF Quick Grant",
            // Ucretsiz ogunde kasaya yazacak tutar yoktur; secenek isaretli olsa da gonderilmez.
            ChargeToCash && HasGrantMealPrice, NotifyParents, grantOperationId);
    }

    private void HandleError(Exception ex, string fallback)
    {
        IsOffline = ex is HttpRequestException or TaskCanceledException or InvalidDataException;
        ErrorMessage = ex is LoginRequiredException ? "Bu ekran için hakediş yetkisi olan bir oturum gerekiyor." : Friendly(ex, fallback);
    }
    // ApiRequestException sunucunun Turkce ProblemDetails basligini tasir; oldugu gibi gosterilir.
    private static string Friendly(Exception ex, string fallback) => ex is InvalidOperationException or ApiRequestException ? ex.Message : fallback;
    private void RaiseTargetVisibility() { Raise(nameof(IsManualTarget)); Raise(nameof(IsClassTarget)); Raise(nameof(IsGradeTarget)); Raise(nameof(IsGroupTarget)); }
    private static DateOnly? ToDate(DateTime? value) => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Listedeki bir hakedis satiri. Secim durumu SATIRIN kendisinde durur; onceden onay
/// kutusu DataGridRow.IsSelected'e bagliydi ve tablo ayni tiklamayla satir secimini
/// yeniden hesapladigi icin tik aninda geri kapaniyordu. Ayrica sanallastirilmis
/// (recycling) satir kaplari kaydirmada tiki baska satira tasiyordu.
/// </summary>
public sealed class MealEntitlementRowViewModel(MealEntitlementListItem item, Action? changed = null) : ObservableObject
{
    private bool isSelected;

    public MealEntitlementListItem Item { get; } = item;
    public bool IsSelected { get => isSelected; set { if (Set(ref isSelected, value)) changed?.Invoke(); } }

    /// <summary>Toplu temizlemede tek tek bildirim yapmadan degeri sifirlar.</summary>
    public void SetSelectedQuietly(bool value)
    {
        if (isSelected == value) return;
        isSelected = value;
        Raise(nameof(IsSelected));
    }

    public Guid Id => Item.Id;
    public Guid StudentId => Item.StudentId;
    public DateOnly Date => Item.Date;
    public string StudentNo => Item.StudentNo;
    public string? CardNumber => Item.CardNumber;
    public string MealName => Item.MealName;
    public string StudentName => Item.StudentName;
    public string? ClassName => Item.ClassName;
    public int Quantity => Item.Quantity;
    public int ConsumedQuantity => Item.ConsumedQuantity;
    public int RemainingQuantity => Item.RemainingQuantity;
    public string Status => Item.Status;
    public string? Source => Item.Source;
}

/// <summary>
/// Hizli Hakedis ogrenci secim satiri. Hakedis listesinden ayridir: orada ayni ogrenci her
/// gun icin bir kez gorunur, burada her ogrenci TEK satirdir.
/// </summary>
public sealed class StudentPickerRowViewModel(StudentListItem item, Action? changed = null) : ObservableObject
{
    private bool isSelected;

    public Guid Id => item.Id;
    public string StudentNo => item.StudentNo;
    public string Name => (item.FirstName + " " + item.LastName).Trim();
    public string ClassName => string.IsNullOrWhiteSpace(item.ClassName) ? "" : item.ClassName!;
    public string SectionName => item.SectionName ?? "";
    public bool IsSelected { get => isSelected; set { if (Set(ref isSelected, value)) changed?.Invoke(); } }
}
