using System.Collections.ObjectModel;
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

public sealed class MealEntitlementsViewModel : ObservableObject
{
    private readonly IMealEntitlementApiClient api;
    private readonly bool canManage, canBulk;
    private bool isLoading, isOffline, isGrantOpen, isCancelConfirmationOpen;
    private string? errorMessage, studentNo, cardNumber, studentName, className, status, previewMessage;
    private int page = 1, pageSize = 50, totalCount, totalQuantity, consumedQuantity, remainingQuantity, quantity = 1;
    private DateTime? startsOn = DateTime.Today.AddDays(-7), endsOn = DateTime.Today.AddDays(7);
    private DateTime grantStartsOn = DateTime.Today, grantEndsOn = DateTime.Today;
    private MealTypeDetails? selectedMeal, grantMeal;
    private GroupRecord? selectedGroup, grantGroup;
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
    }

    public ObservableCollection<MealEntitlementListItem> Items { get; } = [];
    public ObservableCollection<MealTypeDetails> MealTypes { get; } = [];
    public ObservableCollection<ClassRecord> Classes { get; } = [];
    public ObservableCollection<GroupRecord> Groups { get; } = [];
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
    public MealTypeDetails? SelectedMeal { get => selectedMeal; set => Set(ref selectedMeal, value); }
    public GroupRecord? SelectedGroup { get => selectedGroup; set => Set(ref selectedGroup, value); }
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
    public bool IsEmpty => !IsLoading && !HasError && TotalCount == 0;
    public bool IsGrantOpen { get => isGrantOpen; private set => Set(ref isGrantOpen, value); }
    public bool IsCancelConfirmationOpen { get => isCancelConfirmationOpen; private set => Set(ref isCancelConfirmationOpen, value); }
    public string CancelConfirmationText => $"Seçili {SelectedItems.Count} kullanılmamış hak iptal edilecek. Bu işlem geri alınamaz.";
    public string TargetType { get => targetType; set { if (Set(ref targetType, value)) { Preview = null; RaiseTargetVisibility(); } } }
    public bool IsManualTarget => TargetType == "Manual";
    public bool IsClassTarget => TargetType == "Class";
    public bool IsGradeTarget => TargetType == "Grade";
    public bool IsGroupTarget => TargetType == "Group";
    public string ManualStudentIds { get => manualStudentIds; set { if (Set(ref manualStudentIds, value)) Preview = null; } }
    public ClassRecord? GrantClass { get => grantClass; set { if (Set(ref grantClass, value)) Preview = null; } }
    public GroupRecord? GrantGroup { get => grantGroup; set { if (Set(ref grantGroup, value)) Preview = null; } }
    public string Grade { get => grade; set { if (Set(ref grade, value)) Preview = null; } }
    public MealTypeDetails? GrantMeal { get => grantMeal; set { if (Set(ref grantMeal, value)) Preview = null; } }
    public DateTime GrantStartsOn { get => grantStartsOn; set { if (Set(ref grantStartsOn, value)) Preview = null; } }
    public DateTime GrantEndsOn { get => grantEndsOn; set { if (Set(ref grantEndsOn, value)) Preview = null; } }
    public int Quantity { get => quantity; set { if (Set(ref quantity, value)) Preview = null; } }
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
            foreach (var item in mealTask.Result) MealTypes.Add(item);
            foreach (var item in classTask.Result) Classes.Add(item);
            foreach (var item in groupTask.Result) Groups.Add(item);
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
        IsLoading = true; IsOffline = false; ErrorMessage = null;
        try
        {
            var result = await api.SearchAsync(new MealEntitlementQuery(ToDate(StartsOn), ToDate(EndsOn), Empty(StudentNo),
                Empty(CardNumber), Empty(StudentName), Empty(ClassName), SelectedGroup?.Id, SelectedMeal?.Id, Status,
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
        IsGrantOpen = true; Preview = null; PreviewMessage = null;
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
            PreviewMessage = $"{result.CreatedCount:N0} hak oluşturuldu, {result.UpdatedCount:N0} hak güncellendi.";
            Preview = null; await LoadAsync(1); IsGrantOpen = false;
        }
        catch (Exception ex) { PreviewMessage = Friendly(ex, "Hakedişler uygulanamadı. Yeniden önizleyin."); Preview = null; }
    }

    private void RequestCancel()
    {
        ErrorMessage = null;
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
            await api.CancelAsync(new CancelEntitlementsRequest(ids, ids.Length));
            IsCancelConfirmationOpen = false; await LoadAsync(Page);
        }
        catch (Exception ex) { IsCancelConfirmationOpen = false; ErrorMessage = Friendly(ex, "Haklar iptal edilemedi."); }
    }

    private EntitlementGrantRequest BuildGrant()
    {
        if (GrantMeal is null) throw new InvalidOperationException("Öğün seçilmelidir.");
        var ids = ManualStudentIds.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Guid.TryParse(x, out var id) ? id : throw new InvalidOperationException("Manuel öğrenci kimlikleri geçersiz."))
            .Distinct().ToArray();
        var target = new EntitlementTarget(TargetType, ids, GrantClass?.Id, Empty(Grade), GrantGroup?.Id);
        return new EntitlementGrantRequest(target, GrantMeal.Id, DateOnly.FromDateTime(GrantStartsOn),
            DateOnly.FromDateTime(GrantEndsOn), Quantity, IncludeSaturday, IncludeSunday, "WPF Quick Grant");
    }

    private void HandleError(Exception ex, string fallback)
    {
        IsOffline = ex is HttpRequestException or TaskCanceledException or InvalidDataException;
        ErrorMessage = ex is LoginRequiredException ? "Bu ekran için hakediş yetkisi olan bir oturum gerekiyor." : Friendly(ex, fallback);
    }
    private static string Friendly(Exception ex, string fallback) => ex is InvalidOperationException ? ex.Message : fallback;
    private void RaiseTargetVisibility() { Raise(nameof(IsManualTarget)); Raise(nameof(IsClassTarget)); Raise(nameof(IsGradeTarget)); Raise(nameof(IsGroupTarget)); }
    private static DateOnly? ToDate(DateTime? value) => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
