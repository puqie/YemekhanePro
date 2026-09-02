using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;

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
    private string? errorMessage, statusMessage, studentNo, cardNumber, studentName, className, status, previewMessage;
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
        CloseCancelCommand = new RelayCommand(() => IsCancelConfirmationOpen = false);
        OpenBulkCommand = new RelayCommand(OpenBulk, () => canBulk && BulkWizard is not null);
        selectedMealFilter = MealFilters[0]; selectedGroupFilter = GroupFilters[0];
        // Sihirbaz bir islemi uygulayinca ya da geri alinca arkadaki liste ESKI kalmasin:
        // kullanici "Geri Al" dedikten sonra satirlarin hala "Iptal" gorunmesini hata sanir.
        if (BulkWizard is not null && canManage) BulkWizard.Changed += (_, _) => _ = LoadAsync(Page);
    }

    public ObservableCollection<MealEntitlementListItem> Items { get; } = [];
    public ObservableCollection<MealTypeDetails> MealTypes { get; } = [];
    public ObservableCollection<ClassRecord> Classes { get; } = [];
    public ObservableCollection<GroupRecord> Groups { get; } = [];
    /// <summary>Filtre kutulari icin "Tümü" ile baslayan listeler: bos bir acilir kutu "hicbiri" degil "hepsi" demektir, bu ekranda yazmali.</summary>
    public ObservableCollection<EntitlementFilterOption> MealFilters { get; } = [new("Tümü", null)];
    public ObservableCollection<EntitlementFilterOption> GroupFilters { get; } = [new("Tümü", null)];
    public ObservableCollection<MealEntitlementListItem> SelectedItems { get; } = [];
    public IReadOnlyList<EntitlementStatusOption> Statuses { get; } = [new("Tümü", null), new("Aktif", "Active"), new("İptal", "Cancelled"), new("Aktarıldı", "Transferred")];
    public IReadOnlyList<EntitlementTargetOption> TargetTypes { get; } = [new("Manuel öğrenciler", "Manual"), new("Sınıf", "Class"), new("Kademe", "Grade"), new("Grup", "Group"), new("Tüm aktif öğrenciler", "All")];
    public bool CanManage => canManage;
    public bool CanBulk => canBulk;
    public BulkOperationWizardViewModel? BulkWizard { get; }
    public string? StudentNo { get => studentNo; set => Set(ref studentNo, value); }
    public string? CardNumber { get => cardNumber; set => Set(ref cardNumber, value); }
    public string? StudentName { get => studentName; set => Set(ref studentName, value); }
    public string? ClassName { get => className; set => Set(ref className, value); }
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
    public MealTypeDetails? GrantMeal { get => grantMeal; set { if (Set(ref grantMeal, value)) Preview = null; } }
    public DateTime GrantStartsOn { get => grantStartsOn; set { if (Set(ref grantStartsOn, value)) Preview = null; } }
    public DateTime GrantEndsOn { get => grantEndsOn; set { if (Set(ref grantEndsOn, value)) Preview = null; } }
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
    public bool IncludeSaturday { get => includeSaturday; set { if (Set(ref includeSaturday, value)) Preview = null; } }
    public bool IncludeSunday { get => includeSunday; set { if (Set(ref includeSunday, value)) Preview = null; } }
    public EntitlementPreview? Preview { get => preview; private set { if (Set(ref preview, value)) { Raise(nameof(HasPreview)); Raise(nameof(PreviewText)); (ApplyCommand as AsyncCommand)?.Refresh(); } } }
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

    public void SetSelection(IEnumerable<MealEntitlementListItem> selection)
    {
        SelectedItems.Clear(); foreach (var item in selection) SelectedItems.Add(item);
        Raise(nameof(CancelConfirmationText)); (ConfirmCancelCommand as AsyncCommand)?.Refresh();
    }

    public async Task LoadAsync(int targetPage)
    {
        IsLoading = true; IsOffline = false; ErrorMessage = null; StatusMessage = null;
        try
        {
            var result = await api.SearchAsync(new MealEntitlementQuery(ToDate(StartsOn), ToDate(EndsOn), Empty(StudentNo),
                Empty(CardNumber), Empty(StudentName), Empty(ClassName), SelectedGroupFilter?.Id, SelectedMealFilter?.Id, Status,
                targetPage, PageSize));
            Items.Clear(); foreach (var item in result.Items) Items.Add(item);
            Page = result.Page; TotalCount = result.TotalCount; TotalQuantity = result.Summary.TotalQuantity;
            ConsumedQuantity = result.Summary.ConsumedQuantity; RemainingQuantity = result.Summary.RemainingQuantity;
            SetSelection([]);
        }
        catch (Exception ex) { HandleError(ex, "Hakediş listesi alınamadı."); }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    private void OpenGrant()
    {
        if (SelectedItems.Count > 0) ManualStudentIds = string.Join(", ", SelectedItems.Select(x => x.StudentId).Distinct());
        IsGrantOpen = true; Preview = null; PreviewMessage = null; StatusMessage = null;
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
            StatusMessage = $"{result.CreatedCount:N0} hak oluşturuldu, {result.UpdatedCount:N0} hak güncellendi.";
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

    private EntitlementGrantRequest BuildGrant()
    {
        if (GrantMeal is null) throw new InvalidOperationException("Öğün seçilmelidir.");
        if (!int.TryParse(QuantityText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity is < 1 or > 10)
            throw new InvalidOperationException("Günlük adet 1-10 arasında bir tam sayı olmalıdır.");
        if (GrantEndsOn.Date < GrantStartsOn.Date) throw new InvalidOperationException("Bitiş tarihi başlangıç tarihinden önce olamaz.");
        var (ids, nos) = ManualStudentInput.Parse(ManualStudentIds);
        if (IsManualTarget && ids.Length == 0 && nos.Length == 0)
            throw new InvalidOperationException("Öğrenci numaralarını girin (örn. 5012, 5013) ya da listeden satır seçerek Hızlı Hakediş'i açın.");
        if (IsClassTarget && GrantClass is null) throw new InvalidOperationException("Sınıf seçilmelidir.");
        if (IsGroupTarget && GrantGroup is null) throw new InvalidOperationException("Grup seçilmelidir.");
        if (IsGradeTarget && string.IsNullOrWhiteSpace(Grade)) throw new InvalidOperationException("Kademe / sınıf seviyesi girilmelidir.");
        var target = new EntitlementTarget(TargetType, IsManualTarget ? ids : [], GrantClass?.Id, Empty(Grade), GrantGroup?.Id,
            IsManualTarget && nos.Length > 0 ? nos : null);
        return new EntitlementGrantRequest(target, GrantMeal.Id, DateOnly.FromDateTime(GrantStartsOn),
            DateOnly.FromDateTime(GrantEndsOn), quantity, IncludeSaturday, IncludeSunday, "WPF Quick Grant");
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
