using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Meals;
using Yemekhane.Desktop.Converters;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class BulkOperationWizardViewModel : ObservableObject
{
    private readonly IBulkOperationApiClient api;
    private readonly bool canBulk, canHistory;
    private int step = 1;
    private bool isOpen, isBusy, isHistoryOpen;
    private string operation = "CancelEntitlements", transferBehavior = "Delete", manualStudentIds = "", explicitDates = "";
    private string? errorMessage, resultMessage, description;
    private DateTime startsOn = DateTime.Today, endsOn = DateTime.Today, targetDate = DateTime.Today.AddDays(1);
    private CalendarScopeOption? selectedScope;
    private EntitlementFilterOption? selectedMealFilter;
    private BulkOperationPreview? preview;
    private BulkCalendarOperationRequest? previewRequest;

    public BulkOperationWizardViewModel(IBulkOperationApiClient api, IEnumerable<string> permissions)
    {
        this.api = api;
        var values = permissions.ToHashSet(StringComparer.Ordinal);
        canHistory = values.Contains("calendar.manage");
        canBulk = values.Contains("entitlements.bulk") && canHistory;
        OpenCommand = new RelayCommand(Open, () => canBulk);
        CloseCommand = new RelayCommand(() => IsOpen = false, () => !IsBusy);
        NextCommand = new AsyncCommand(NextAsync, () => IsOpen && !IsBusy && Step < 7);
        BackCommand = new RelayCommand(Back, () => !IsBusy && Step > 1 && Step < 7);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !IsBusy && Step == 6 && Preview is not null);
        OpenHistoryCommand = new AsyncCommand(OpenHistoryAsync, () => canHistory && !IsBusy);
        CloseHistoryCommand = new RelayCommand(() => IsHistoryOpen = false);
        UndoCommand = new RelayCommand<BulkOperationHistoryItem>(item => _ = UndoAsync(item), item => canBulk && !IsBusy && item?.CanUndo == true);
        selectedMealFilter = MealFilters[0];
    }

    /// <summary>
    /// Bir islem uygulandiginda ya da geri alindiginda tetiklenir. Sihirbazi barindiran
    /// ekran (Hakedisler listesi, Takvim) bununla kendini yeniler; aksi halde kullanici
    /// "Geri Al" dedikten sonra arkadaki listede satirlarin hala "Iptal" oldugunu gorur.
    /// </summary>
    public event EventHandler? Changed;

    public ObservableCollection<CalendarScopeOption> Scopes { get; } = [];
    public ObservableCollection<MealTypeDetails> MealTypes { get; } = [];
    /// <summary>Ogun kutusu "Tümü" ile baslar: bos acilir kutu kullaniciya "secilmemis" gibi gorunuyordu.</summary>
    public ObservableCollection<EntitlementFilterOption> MealFilters { get; } = [new("Tümü", null)];
    public ObservableCollection<BulkOperationHistoryItem> History { get; } = [];
    // Kodlar API sozlesmesidir; ekranda EnumTextConverter (BulkOperation / TransferBehavior) ile Turkcelesir.
    public IReadOnlyList<string> Operations { get; } = ["CancelEntitlements", "Holiday", "Trip", "Leave", "Transfer"];
    public IReadOnlyList<string> TransferBehaviors { get; } = ["Delete", "Forfeit", "NextBusinessDay", "SpecifiedDate"];
    public bool CanBulk => canBulk;
    public bool CanHistory => canHistory;
    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) RefreshCommands(); } }
    public bool IsHistoryOpen { get => isHistoryOpen; private set => Set(ref isHistoryOpen, value); }
    public int Step { get => step; private set { if (Set(ref step, value)) { RaiseStepFlags(); RefreshCommands(); } } }
    public string StepTitle => $"Adım {Step} / 7";
    public bool IsStep1 => Step == 1; public bool IsStep2 => Step == 2; public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4; public bool IsStep5 => Step == 5; public bool IsStep6 => Step == 6; public bool IsStep7 => Step == 7;
    public string Operation { get => operation; set { if (Set(ref operation, value)) { InvalidatePreview(); Raise(nameof(ConfirmationText)); } } }
    public string TransferBehavior { get => transferBehavior; set { if (Set(ref transferBehavior, value)) { InvalidatePreview(); Raise(nameof(IsSpecifiedDate)); Raise(nameof(ConfirmationText)); } } }
    public bool IsSpecifiedDate => TransferBehavior == "SpecifiedDate";
    public CalendarScopeOption? SelectedScope { get => selectedScope; set { if (Set(ref selectedScope, value)) { InvalidatePreview(); Raise(nameof(IsManualScope)); } } }
    public bool IsManualScope => SelectedScope?.ScopeType == "Manual";
    public EntitlementFilterOption? SelectedMealFilter
    {
        get => selectedMealFilter;
        set { if (Set(ref selectedMealFilter, value)) { InvalidatePreview(); Raise(nameof(SelectedMealType)); } }
    }
    /// <summary>Secili ogun (null = tumu). Kutu ogeleriyle karsilikli eslenir.</summary>
    public MealTypeDetails? SelectedMealType
    {
        get => MealTypes.FirstOrDefault(x => x.Id == SelectedMealFilter?.Id);
        set => SelectedMealFilter = MealFilters.FirstOrDefault(x => x.Id == value?.Id) ?? MealFilters[0];
    }
    /// <summary>Kimlik (GUID) ya da okul numarasi; virgul/bosluk/satir ile ayrilir.</summary>
    public string ManualStudentIds { get => manualStudentIds; set { if (Set(ref manualStudentIds, value)) InvalidatePreview(); } }
    public DateTime StartsOn { get => startsOn; set { if (Set(ref startsOn, value)) InvalidatePreview(); } }
    public DateTime EndsOn { get => endsOn; set { if (Set(ref endsOn, value)) InvalidatePreview(); } }
    public DateTime TargetDate { get => targetDate; set { if (Set(ref targetDate, value)) InvalidatePreview(); } }
    public string ExplicitDates { get => explicitDates; set { if (Set(ref explicitDates, value)) InvalidatePreview(); } }
    public string? Description { get => description; set { if (Set(ref description, value)) InvalidatePreview(); } }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ResultMessage { get => resultMessage; private set { if (Set(ref resultMessage, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultMessage);
    public BulkOperationPreview? Preview { get => preview; private set { if (Set(ref preview, value)) { Raise(nameof(ConfirmationText)); RefreshCommands(); } } }
    /// <summary>Onay metni: islem turu ve hak davranisi ham kodla degil Turkce adiyla yazilir.</summary>
    public string ConfirmationText => Preview is null ? "" :
        $"{Preview.StudentCount:N0} öğrencinin {Preview.Quantity:N0} hakkı için \"{EnumTextConverter.Translate(Operation, "BulkOperation")}\" işlemi uygulanacak " +
        $"(davranış: {EnumTextConverter.Translate(TransferBehavior, "TransferBehavior")}). Bu özeti onaylıyor musunuz?";
    public ICommand OpenCommand { get; } public ICommand CloseCommand { get; } public ICommand NextCommand { get; }
    public ICommand BackCommand { get; } public ICommand ApplyCommand { get; } public ICommand OpenHistoryCommand { get; }
    public ICommand CloseHistoryCommand { get; } public ICommand UndoCommand { get; }

    public async Task InitializeAsync()
    {
        if (!canBulk && !canHistory) return;
        try
        {
            if (canBulk)
            {
                var scopeTask = api.ScopesAsync(); var mealTask = api.MealTypesAsync(); await Task.WhenAll(scopeTask, mealTask);
                foreach (var value in scopeTask.Result) Scopes.Add(value);
                Scopes.Add(new CalendarScopeOption("Manual", null, "Manuel öğrenciler"));
                foreach (var value in mealTask.Result) { MealTypes.Add(value); MealFilters.Add(new(value.Name, value.Id)); }
                SelectedScope = Scopes.FirstOrDefault();
            }
            if (canHistory) await LoadHistoryAsync();
        }
        catch (Exception ex) { ErrorMessage = Friendly(ex); }
    }

    /// <summary>
    /// Sihirbazi baska bir ekrandan acarken on ayar: tarih (takvimde secili gun),
    /// ogrenciler (listede secili satirlar) ve hak davranisi (yeni olusturulan tatilin
    /// davranisi -- tatil kaydi haklari kendisi degistirmez, bu sihirbaz degistirir).
    /// </summary>
    public void Preset(DateOnly? date = null, IReadOnlyCollection<Guid>? studentIds = null, string? transferBehavior = null)
    {
        if (date.HasValue) StartsOn = EndsOn = date.Value.ToDateTime(TimeOnly.MinValue);
        if (studentIds?.Count > 0)
        {
            SelectedScope = Scopes.FirstOrDefault(x => x.ScopeType == "Manual") ?? new("Manual", null, "Manuel öğrenciler");
            ManualStudentIds = string.Join(", ", studentIds);
        }
        if (transferBehavior is not null && TransferBehaviors.Contains(transferBehavior)) TransferBehavior = transferBehavior;
    }

    private void Open() { ErrorMessage = null; ResultMessage = null; Preview = null; Step = 1; IsOpen = true; }
    private void Back() { ErrorMessage = null; Step--; }
    private async Task NextAsync()
    {
        ErrorMessage = null;
        try
        {
            ValidateStep();
            if (Step == 4)
            {
                IsBusy = true; previewRequest = BuildRequest(); Preview = await api.PreviewAsync(previewRequest); Step = 5;
            }
            else Step++;
        }
        catch (Exception ex) { ErrorMessage = Friendly(ex); }
        finally { IsBusy = false; }
    }

    private async Task ApplyAsync()
    {
        if (previewRequest is null || Preview is null) return;
        IsBusy = true; ErrorMessage = null;
        BulkOperationResult result;
        try { result = await api.ApplyAsync(new ApplyBulkOperationRequest(previewRequest, Preview.PreviewToken)); }
        catch (Exception ex) { ErrorMessage = Friendly(ex); Preview = null; Step = 4; IsBusy = false; return; }
        // Islem UYGULANDI. Bundan sonraki adimlar (gecmisi yenileme, dinleyicileri uyarma)
        // basarisiz olsa bile kullaniciya "uygulanamadi" denemez; onceden gecmis listesi
        // yuklenemeyince adim 4'e donuluyor ve kullanici islemi yeniden uygulamaya kalkiyordu.
        ResultMessage = $"{result.StudentCount:N0} öğrenci, {result.Quantity:N0} hak işlendi. İptal: {result.CancelledCount:N0}, aktarım: {result.TransferredCount:N0}.";
        Step = 7;
        try { if (canHistory) await LoadHistoryAsync(); }
        catch (Exception ex) { ErrorMessage = "İşlem uygulandı; geçmiş listesi yenilenemedi: " + Friendly(ex); }
        finally { IsBusy = false; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenHistoryAsync()
    {
        IsBusy = true; ErrorMessage = null; ResultMessage = null;
        try { await LoadHistoryAsync(); IsHistoryOpen = true; }
        catch (Exception ex) { ErrorMessage = Friendly(ex); }
        finally { IsBusy = false; }
    }
    private async Task LoadHistoryAsync() { var page = await api.HistoryAsync(); History.Clear(); foreach (var item in page.Items) History.Add(item); }
    private async Task UndoAsync(BulkOperationHistoryItem? item)
    {
        if (item is null) return; IsBusy = true; ErrorMessage = null;
        try
        {
            ResultMessage = (await api.UndoAsync(item.Id)).Message; await LoadHistoryAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ErrorMessage = Friendly(ex); }
        finally { IsBusy = false; }
    }

    private BulkCalendarOperationRequest BuildRequest()
    {
        var (ids, nos) = ManualStudentInput.Parse(ManualStudentIds);
        var dates = ExplicitDates.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : throw new InvalidOperationException("Tarih listesi yyyy-AA-gg biçiminde olmalıdır.")).ToArray();
        var scope = SelectedScope ?? throw new InvalidOperationException("Kapsam seçilmelidir.");
        var manual = scope.ScopeType == "Manual";
        return new BulkCalendarOperationRequest(Guid.NewGuid().ToString("N"),
            new BulkOperationScope(scope.ScopeType, scope.ScopeId, manual ? ids : [], manual && nos.Length > 0 ? nos : null), DateOnly.FromDateTime(StartsOn),
            DateOnly.FromDateTime(EndsOn), dates, SelectedMealType?.Id, Operation, TransferBehavior,
            IsSpecifiedDate ? DateOnly.FromDateTime(TargetDate) : null, Description);
    }

    private void ValidateStep()
    {
        if (Step == 1 && !Operations.Contains(Operation)) throw new InvalidOperationException("İşlem türü seçilmelidir.");
        if (Step == 2 && SelectedScope is null) throw new InvalidOperationException("Kapsam seçilmelidir.");
        if (Step == 2 && IsManualScope && string.IsNullOrWhiteSpace(ManualStudentIds))
            throw new InvalidOperationException("En az bir öğrenci numarası girin (örn. 5012, 5013) ya da Hakedişler listesinden satır seçerek sihirbazı açın.");
        if (Step == 3 && EndsOn.Date < StartsOn.Date) throw new InvalidOperationException("Bitiş tarihi başlangıç tarihinden önce olamaz.");
        if (Step == 4 && IsSpecifiedDate && TargetDate.Date <= StartsOn.Date) throw new InvalidOperationException("Hedef tarih kaynak tarihten sonra olmalıdır.");
    }
    private void InvalidatePreview() { Preview = null; previewRequest = null; }
    private void RaiseStepFlags() { Raise(nameof(StepTitle)); Raise(nameof(IsStep1)); Raise(nameof(IsStep2)); Raise(nameof(IsStep3)); Raise(nameof(IsStep4)); Raise(nameof(IsStep5)); Raise(nameof(IsStep6)); Raise(nameof(IsStep7)); }
    private void RefreshCommands() { (NextCommand as AsyncCommand)?.Refresh(); (BackCommand as RelayCommand)?.Refresh(); (ApplyCommand as AsyncCommand)?.Refresh(); (CloseCommand as RelayCommand)?.Refresh(); (UndoCommand as RelayCommand<BulkOperationHistoryItem>)?.Refresh(); }
    private static string Friendly(Exception ex) => ex switch
    {
        LoginRequiredException => "Bu işlem için gerekli yetki bulunmuyor.",
        // Baglanti hatasinin Ingilizce .NET mesaji ("No connection could be made...") kullaniciya gosterilmez.
        HttpRequestException or TaskCanceledException => "Sunucuya ulaşılamadı. Bağlantıyı kontrol edip yeniden deneyin.",
        _ => ex.Message
    };
}
