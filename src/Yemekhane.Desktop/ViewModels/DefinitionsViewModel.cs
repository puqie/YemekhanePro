using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Tanimlar ekrani: eski programdaki "Öğün Tanım" ve "Departman/Bölüm/Sınıf/Görev Tanım"
/// formlarinin tek sekmeli karsiligi. Ogunler cekmeceyle duzenlenir; sinif/sube/bolum/gorev
/// sekmeleri satir ici ekleme kutusu, yeniden adlandirma ve iki adimli silme tasir.
/// </summary>
public sealed class DefinitionsViewModel : ObservableObject
{
    private readonly IDefinitionsApiClient api;
    private readonly List<MealTypeRow> allMeals = [];
    private MealTypeRow? selectedMeal;
    private Guid? editingMealId;
    private bool isLoading, isOffline, isMealOpen, mealIsActive = true, mealsLoaded;
    private string mealName = "", mealStartsAt = "", mealEndsAt = "", mealPriceText = "";
    private string? errorMessage, statusMessage, mealError;
    private int selectedTabIndex;

    public DefinitionsViewModel(IDefinitionsApiClient api, IEnumerable<string> permissions)
    {
        this.api = api;
        var set = permissions.ToHashSet(StringComparer.Ordinal);
        // Ogun uclari entitlements.manage, tanim uclari students.read/write ister (API).
        CanManageMeals = set.Contains("entitlements.manage");
        CanReadLookups = set.Contains("students.read") || set.Contains("students.write");
        CanManageLookups = set.Contains("students.write");
        Classes = new LookupTabViewModel(api, DefinitionsApiClient.Classes, "Sınıflar", "Sınıf", CanManageLookups);
        Sections = new LookupTabViewModel(api, DefinitionsApiClient.Sections, "Şubeler", "Şube", CanManageLookups);
        Departments = new LookupTabViewModel(api, DefinitionsApiClient.Departments, "Bölümler", "Bölüm", CanManageLookups);
        Jobs = new LookupTabViewModel(api, DefinitionsApiClient.Jobs, "Görevler", "Görev", CanManageLookups);
        Tabs = [Classes, Sections, Departments, Jobs];
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        OpenNewMealCommand = new RelayCommand(OpenNewMeal, () => CanManageMeals);
        OpenEditMealCommand = new RelayCommand(OpenEditMeal, () => CanManageMeals && SelectedMeal is not null);
        CloseMealCommand = new RelayCommand(() => IsMealOpen = false);
        SaveMealCommand = new AsyncCommand(SaveMealAsync, () => CanManageMeals && IsMealOpen);
        DeactivateMealCommand = new AsyncCommand(DeactivateMealAsync, () => CanManageMeals && SelectedMeal is { IsActive: true });
    }

    public bool CanManageMeals { get; }
    public bool CanReadLookups { get; }
    public bool CanManageLookups { get; }
    public LookupTabViewModel Classes { get; }
    public LookupTabViewModel Sections { get; }
    public LookupTabViewModel Departments { get; }
    public LookupTabViewModel Jobs { get; }
    public IReadOnlyList<LookupTabViewModel> Tabs { get; }

    /// <summary>Aktif ve pasif ogunler birlikte: pasif bir ogunun neden listede olmadigi sorulmasin.</summary>
    public ObservableCollection<MealTypeRow> Meals { get; } = [];
    public MealTypeRow? SelectedMeal
    {
        get => selectedMeal;
        set { if (Set(ref selectedMeal, value)) RefreshMealCommands(); }
    }
    public bool IsMealsEmpty => mealsLoaded && Meals.Count == 0;
    public string MealsSummary => $"{allMeals.Count(x => x.IsActive)} aktif, {allMeals.Count(x => !x.IsActive)} pasif öğün";

    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) (RefreshCommand as AsyncCommand)?.Refresh(); } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    /// <summary>Sekme degisince sayfa alt bandindaki ogun mesaji kalkar; Siniflar sekmesinde "Öğle Yemeği kaydedildi" okunmasin.</summary>
    public int SelectedTabIndex { get => selectedTabIndex; set { if (Set(ref selectedTabIndex, value)) { StatusMessage = null; ErrorMessage = null; } } }

    // ------------------------------------------------------------------ ogun cekmecesi
    public bool IsMealOpen { get => isMealOpen; private set { if (Set(ref isMealOpen, value)) (SaveMealCommand as AsyncCommand)?.Refresh(); } }
    public string MealFormTitle => editingMealId is null ? "Yeni Öğün" : "Öğünü Düzenle";
    public string MealName { get => mealName; set => Set(ref mealName, value ?? ""); }
    public string MealStartsAt { get => mealStartsAt; set => Set(ref mealStartsAt, value ?? ""); }
    public string MealEndsAt { get => mealEndsAt; set => Set(ref mealEndsAt, value ?? ""); }
    public string MealPriceText { get => mealPriceText; set => Set(ref mealPriceText, value ?? ""); }
    public bool MealIsActive { get => mealIsActive; set => Set(ref mealIsActive, value); }
    public string? MealError { get => mealError; private set { if (Set(ref mealError, value)) Raise(nameof(HasMealError)); } }
    public bool HasMealError => !string.IsNullOrWhiteSpace(MealError);

    public ICommand RefreshCommand { get; }
    public ICommand OpenNewMealCommand { get; }
    public ICommand OpenEditMealCommand { get; }
    public ICommand CloseMealCommand { get; }
    public ICommand SaveMealCommand { get; }
    public ICommand DeactivateMealCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        IsLoading = true; ErrorMessage = null; StatusMessage = null;
        try
        {
            if (CanManageMeals) await LoadMealsAsync();
            if (CanReadLookups) foreach (var tab in Tabs) await tab.LoadAsync();
            IsOffline = false;
        }
        catch (Exception ex) { HandleError(ex, "Tanımlar yüklenemedi."); }
        finally { IsLoading = false; }
    }

    private async Task LoadMealsAsync()
    {
        var items = await api.MealTypesAsync(includeInactive: true);
        var keep = SelectedMeal?.Id;
        allMeals.Clear(); Meals.Clear();
        // Aktifler basta, sonra ada gore: pasifler listenin sonunda toplanir.
        foreach (var item in items.OrderByDescending(x => x.IsActive).ThenBy(x => x.Name, StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true)))
        {
            var row = new MealTypeRow(item);
            allMeals.Add(row); Meals.Add(row);
        }
        mealsLoaded = true;
        SelectedMeal = Meals.FirstOrDefault(x => x.Id == keep);
        Raise(nameof(IsMealsEmpty)); Raise(nameof(MealsSummary));
    }

    private void OpenNewMeal()
    {
        editingMealId = null; MealName = ""; MealStartsAt = ""; MealEndsAt = ""; MealPriceText = ""; MealIsActive = true;
        MealError = null; StatusMessage = null; IsMealOpen = true; Raise(nameof(MealFormTitle));
    }

    private void OpenEditMeal()
    {
        if (SelectedMeal is null) return;
        editingMealId = SelectedMeal.Id; MealName = SelectedMeal.Name;
        MealStartsAt = SelectedMeal.StartsText; MealEndsAt = SelectedMeal.EndsText;
        MealPriceText = SelectedMeal.Price.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")); MealIsActive = SelectedMeal.IsActive;
        MealError = null; StatusMessage = null; IsMealOpen = true; Raise(nameof(MealFormTitle));
    }

    private async Task SaveMealAsync()
    {
        MealError = ValidateMeal(MealName, MealStartsAt, MealEndsAt, MealPriceText);
        if (MealError is not null) return;
        // ValidateMeal ucunu de zaten ayristirip dogruladi; sonuclar yine de denetlenir ki
        // dogrulama ileride degisirse ogun sessizce 00:00 saat ve 0 TL ile kaydedilmesin.
        if (!TryParseTime(MealStartsAt, out var starts) || !TryParseTime(MealEndsAt, out var ends)
            || !TryParsePrice(MealPriceText, out var price))
        { MealError = "Saat veya ücret okunamadı."; return; }
        var request = new SaveMealTypeRequest(MealName.Trim(), starts, ends, MealIsActive, price);
        try
        {
            var saved = editingMealId is null
                ? await api.CreateMealTypeAsync(request)
                : await api.UpdateMealTypeAsync(editingMealId.Value, request);
            IsMealOpen = false;
            await LoadMealsAsync();
            SelectedMeal = Meals.FirstOrDefault(x => x.Id == saved.Id);
            StatusMessage = $"{saved.Name} kaydedildi.";
        }
        catch (Exception ex) { MealError = Friendly(ex, "Öğün kaydedilemedi."); }
    }

    private async Task DeactivateMealAsync()
    {
        if (SelectedMeal is null) return;
        var name = SelectedMeal.Name;
        ErrorMessage = null;
        try
        {
            await api.DeactivateMealTypeAsync(SelectedMeal.Id);
            IsMealOpen = false;
            await LoadMealsAsync();
            StatusMessage = $"{name} pasifleştirildi.";
        }
        catch (Exception ex) { ErrorMessage = Friendly(ex, "Öğün pasifleştirilemedi."); }
    }

    private void RefreshMealCommands()
    {
        (OpenEditMealCommand as RelayCommand)?.Refresh();
        (DeactivateMealCommand as AsyncCommand)?.Refresh();
    }

    /// <summary>Cekmecedeki alanlarin dogrulamasi; ilk hata metni, yoksa null.</summary>
    public static string? ValidateMeal(string? name, string? startsText, string? endsText, string? priceText)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is < 2 or > 100) return "Öğün adı 2-100 karakter olmalıdır.";
        if (!TryParseTime(startsText, out var starts)) return "Başlangıç saati SS:dd biçiminde olmalıdır (örn. 11:30).";
        if (!TryParseTime(endsText, out var ends)) return "Bitiş saati SS:dd biçiminde olmalıdır (örn. 13:30).";
        if (starts.HasValue != ends.HasValue) return "Başlangıç ve bitiş saati birlikte girilmelidir.";
        if (starts.HasValue && starts >= ends) return "Bitiş saati başlangıçtan sonra olmalıdır.";
        if (!TryParsePrice(priceText, out var price)) return "Ücret 0 ya da en fazla iki ondalıklı bir tutar olmalıdır (örn. 250,50).";
        if (price > 100_000) return "Öğün ücreti 0 ile 100.000 ₺ arasında olmalıdır.";
        return null;
    }

    /// <summary>
    /// "SS:dd" (08:00, 8:00 da kabul) ya da bos. Bos = saat yok (null). Yanlis bicim false doner;
    /// TimeOnly.TryParse kulturle "8" gibi degerleri de kabul ederdi, kullanici 8 yazip 08:00
    /// beklerken saat 00:08 kaydedilebilirdi.
    /// </summary>
    public static bool TryParseTime(string? text, out TimeOnly? time)
    {
        time = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return true;
        if (!TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        time = parsed; return true;
    }

    /// <summary>
    /// Ucret: bos ya da sifir = ucretsiz ogun; aksi halde Kasa'daki Turkce tutar kurali
    /// (250,50 ve 250.50 ikisi de 250,50; 1.250,50 binlik ayracli). Kasa'nin ayristiricisi
    /// sifiri reddeder (tahsilat sifir olamaz), burada sifir gecerli bir degerdir.
    /// </summary>
    public static bool TryParsePrice(string? text, out decimal price)
    {
        price = 0;
        var value = text?.Trim().Replace("₺", "", StringComparison.Ordinal).Replace("TL", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(value)) return true;
        if (value.All(c => c == '0' || c == ',' || c == '.') && value.Count(c => c is ',' or '.') <= 1) return true;
        return CashViewModel.TryParseAmount(value, out price);
    }

    private void HandleError(Exception ex, string fallback)
    {
        IsOffline = ex is HttpRequestException or TaskCanceledException or InvalidDataException;
        ErrorMessage = ex is LoginRequiredException ? "Bu ekran için tanım yetkisi olan bir oturum gerekiyor." : Friendly(ex, fallback);
    }

    // ApiRequestException sunucunun Turkce ProblemDetails basligini tasir; oldugu gibi gosterilir.
    internal static string Friendly(Exception ex, string fallback) => ex switch
    {
        ApiRequestException or InvalidOperationException => ex.Message,
        LoginRequiredException => "Bu işlem için yetkili bir oturum gerekiyor.",
        HttpRequestException or TaskCanceledException => fallback + " Sunucuya ulaşılamadı; bağlantıyı kontrol edip tekrar deneyin.",
        _ => fallback,
    };
}

/// <summary>Listede gosterilen ogun satiri: saatler "SS:dd", saat yoksa "—", durum Turkce.</summary>
public sealed class MealTypeRow(MealTypeDetails details)
{
    public MealTypeDetails Details { get; } = details;
    public Guid Id => Details.Id;
    public string Name => Details.Name;
    public TimeOnly? StartsAt => Details.StartsAt;
    public TimeOnly? EndsAt => Details.EndsAt;
    public string StartsText => Details.StartsAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
    public string EndsText => Details.EndsAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
    public decimal Price => Details.Price;
    public bool IsActive => Details.IsActive;
    public string StatusText => Details.IsActive ? "Aktif" : "Pasif";
}

/// <summary>
/// Sinif / Sube / Bolum / Gorev sekmelerinden biri. Dort sekme ayni davranisi tasir; tek
/// fark API yolu (kind) ve etiketler. Silme iki adimlidir: ilk tiklama dugmeyi
/// "Silmeyi Onayla"ya cevirir, ikincisi siler; "Vazgeç" geri alir.
/// </summary>
public sealed class LookupTabViewModel : ObservableObject
{
    private readonly IDefinitionsApiClient api;
    private readonly string kind;
    private LookupRecord? selectedItem;
    private string newName = "", renameName = "";
    private string? errorMessage, statusMessage;
    private bool isRenameOpen, isDeleteArmed, loaded;

    public LookupTabViewModel(IDefinitionsApiClient api, string kind, string title, string singular, bool canManage)
    {
        this.api = api; this.kind = kind; Title = title; Singular = singular; CanManage = canManage;
        AddCommand = new AsyncCommand(AddAsync, () => CanManage && !string.IsNullOrWhiteSpace(NewName));
        OpenRenameCommand = new RelayCommand(OpenRename, () => CanManage && SelectedItem is not null);
        SaveRenameCommand = new AsyncCommand(SaveRenameAsync, () => CanManage && IsRenameOpen && SelectedItem is not null && !string.IsNullOrWhiteSpace(RenameName));
        CloseRenameCommand = new RelayCommand(() => IsRenameOpen = false);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanManage && SelectedItem is not null);
        CancelDeleteCommand = new RelayCommand(() => IsDeleteArmed = false, () => IsDeleteArmed);
    }

    public string Kind => kind;
    public string Title { get; }
    public string Singular { get; }
    public bool CanManage { get; }
    public string NewLabel => "Yeni " + Singular;
    public string NewPlaceholder => Singular + " adı";
    // CA1822 (static yapilabilir) BILEREK bastirildi: bu uye XAML'de {Binding EmptyText}
    // ile baglanir ve WPF baglamalari static uyeleri COZEMEZ; static yapilirsa metin
    // ekranda sessizce bos kalir.
#pragma warning disable CA1822
    public string EmptyText => "Henüz kayıt yok";
#pragma warning restore CA1822
    public ObservableCollection<LookupRecord> Items { get; } = [];
    public bool IsEmpty => loaded && Items.Count == 0;
    public string SummaryText => $"{Items.Count} kayıt";

    public LookupRecord? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!Set(ref selectedItem, value)) return;
            // Baska satira gecince yarim kalan silme onayi ve yeniden adlandirma kutusu kapanir:
            // "Silmeyi Onayla" bir onceki satir icin kurulmustu, yeni satirda yanlis kaydi silerdi.
            IsDeleteArmed = false; IsRenameOpen = false;
            RefreshCommands();
        }
    }
    public string NewName { get => newName; set { if (Set(ref newName, value ?? "")) (AddCommand as AsyncCommand)?.Refresh(); } }
    public string RenameName { get => renameName; set { if (Set(ref renameName, value ?? "")) (SaveRenameCommand as AsyncCommand)?.Refresh(); } }
    public bool IsRenameOpen { get => isRenameOpen; private set { if (Set(ref isRenameOpen, value)) (SaveRenameCommand as AsyncCommand)?.Refresh(); } }
    public bool IsDeleteArmed
    {
        get => isDeleteArmed;
        private set { if (Set(ref isDeleteArmed, value)) { Raise(nameof(DeleteButtonText)); (CancelDeleteCommand as RelayCommand)?.Refresh(); } }
    }
    public string DeleteButtonText => IsDeleteArmed ? "Silmeyi Onayla" : "Sil";
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public ICommand AddCommand { get; }
    public ICommand OpenRenameCommand { get; }
    public ICommand SaveRenameCommand { get; }
    public ICommand CloseRenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    /// <summary>Listeyi yeniden yukler; secili kayit kimligiyle korunur.</summary>
    public async Task LoadAsync()
    {
        var items = await api.LookupsAsync(kind);
        var keep = SelectedItem?.Id;
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        loaded = true;
        SelectedItem = Items.FirstOrDefault(x => x.Id == keep);
        Raise(nameof(IsEmpty)); Raise(nameof(SummaryText));
    }

    private async Task AddAsync()
    {
        ErrorMessage = null; StatusMessage = null;
        var name = NewName.Trim();
        if (name.Length is < 1 or > 100) { ErrorMessage = $"{Singular} adı 1-100 karakter olmalıdır."; return; }
        try
        {
            var created = await api.CreateLookupAsync(kind, name);
            NewName = "";
            await LoadAsync();
            SelectedItem = Items.FirstOrDefault(x => x.Id == created.Id);
            StatusMessage = $"{created.Name} eklendi.";
        }
        catch (Exception ex) { ErrorMessage = DefinitionsViewModel.Friendly(ex, $"{Singular} eklenemedi."); }
    }

    private void OpenRename()
    {
        if (SelectedItem is null) return;
        RenameName = SelectedItem.Name; ErrorMessage = null; StatusMessage = null; IsDeleteArmed = false; IsRenameOpen = true;
    }

    private async Task SaveRenameAsync()
    {
        if (SelectedItem is null) return;
        ErrorMessage = null; StatusMessage = null;
        var name = RenameName.Trim();
        if (name.Length is < 1 or > 100) { ErrorMessage = $"{Singular} adı 1-100 karakter olmalıdır."; return; }
        var id = SelectedItem.Id;
        try
        {
            var renamed = await api.RenameLookupAsync(kind, id, name);
            IsRenameOpen = false;
            await LoadAsync();
            SelectedItem = Items.FirstOrDefault(x => x.Id == renamed.Id);
            StatusMessage = $"{renamed.Name} olarak yeniden adlandırıldı.";
        }
        catch (Exception ex) { ErrorMessage = DefinitionsViewModel.Friendly(ex, $"{Singular} yeniden adlandırılamadı."); }
    }

    private async Task DeleteAsync()
    {
        if (SelectedItem is null) return;
        ErrorMessage = null; StatusMessage = null;
        if (!IsDeleteArmed) { IsDeleteArmed = true; return; }
        var target = SelectedItem;
        try
        {
            await api.DeleteLookupAsync(kind, target.Id);
            IsDeleteArmed = false;
            await LoadAsync();
            StatusMessage = $"{target.Name} silindi.";
        }
        catch (Exception ex)
        {
            // 409: "Sınıf 12 öğrencide kullanılıyor; önce öğrencileri başka bir tanıma taşıyın."
            // Sunucu metni AYNEN gosterilir; onay durumu kaldirilir ki kullanici tekrar tekrar denemesin.
            IsDeleteArmed = false;
            ErrorMessage = DefinitionsViewModel.Friendly(ex, $"{Singular} silinemedi.");
        }
    }

    private void RefreshCommands()
    {
        (OpenRenameCommand as RelayCommand)?.Refresh();
        (SaveRenameCommand as AsyncCommand)?.Refresh();
        (DeleteCommand as AsyncCommand)?.Refresh();
    }
}
