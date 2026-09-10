using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Tuition;
using Yemekhane.Desktop.Services;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>Ucret bicimi secenegi (anahtar + ekranda gorunen Turkce etiket).</summary>
public sealed record TuitionKindOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Taksit satiri; tabloda bicimlenmis haliyle gosterilir.</summary>
public sealed class TuitionInstallmentRowViewModel(TuitionInstallmentDetails row)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public Guid Id => row.Id;
    public string Sequence => row.Sequence == 0 ? "Peşinat" : row.Sequence.ToString(CultureInfo.InvariantCulture) + ". taksit";
    public string DueOn => row.DueOn.ToString("dd.MM.yyyy", Turkish);
    public string Amount => row.Amount.ToString("C2", Turkish);
    public string Paid => row.Paid.ToString("C2", Turkish);
    public string Remaining => row.Remaining.ToString("C2", Turkish);
    public string Status => row.StatusLabel;
    /// <summary>Gecikmis taksit kirmizi gosterilir; memur once bunlari arar.</summary>
    public bool IsOverdue => row.Status == TuitionInstallmentStatuses.Overdue;
}

/// <summary>
/// Anasinifi ucret plani ekrani: sinifa ya da tek ogrenciye toplam+taksit, aylik sabit ya da
/// gunluk ucret tanimlanir. Taksitler kaydedildiginde uretilir; ekran kalan ve gecikmis
/// borcu gosterir.
/// </summary>
public sealed class TuitionViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly ITuitionApiClient api;
    private readonly IDefinitionsApiClient? definitions;
    private readonly bool canManage;
    private TuitionPlanDetails? selectedPlan;
    private LookupRecord? selectedClass;
    private TuitionKindOption kind;
    private string period = DefaultPeriod();
    private string amountText = "", downPaymentText = "", installmentCountText = "10", dueDayText = "5";
    private DateTime startsOn = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string? note, errorMessage, statusMessage;
    private bool isLoading, deleteArmed;

    public TuitionViewModel(ITuitionApiClient api, IEnumerable<string> permissions, IDefinitionsApiClient? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        this.api = api;
        this.definitions = definitions;
        var set = permissions.ToHashSet(StringComparer.Ordinal);
        canManage = set.Contains("cash.manage");
        kind = Kinds[0];
        RefreshCommand = new AsyncCommand(LoadAsync);
        SaveCommand = new AsyncCommand(SaveAsync, () => canManage && !IsLoading);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => canManage && SelectedPlan is not null && !IsLoading);
        CancelDeleteCommand = new RelayCommand(() => DeleteArmed = false);
        NewPlanCommand = new RelayCommand(ResetForm, () => canManage);
    }

    public static IReadOnlyList<TuitionKindOption> Kinds { get; } =
    [
        new(TuitionPlanKinds.Installment, TuitionPlanKinds.Label(TuitionPlanKinds.Installment)),
        new(TuitionPlanKinds.Monthly, TuitionPlanKinds.Label(TuitionPlanKinds.Monthly)),
        new(TuitionPlanKinds.Daily, TuitionPlanKinds.Label(TuitionPlanKinds.Daily))
    ];

    public ObservableCollection<TuitionPlanDetails> Plans { get; } = [];
    public ObservableCollection<TuitionInstallmentRowViewModel> Installments { get; } = [];
    /// <summary>Yalnizca anasinifi siniflari; ucret plani ozellikle bu siniflar icin eklendi.</summary>
    public ObservableCollection<LookupRecord> Classes { get; } = [];

    public bool CanManage => canManage;
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RefreshCommands(); } }
    public string? ErrorMessage { get => errorMessage; private set => Set(ref errorMessage, value); }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }

    public LookupRecord? SelectedClass { get => selectedClass; set => Set(ref selectedClass, value); }
    public TuitionKindOption SelectedKind
    {
        get => kind;
        set
        {
            if (!Set(ref kind, value)) return;
            // Gunluk ucrette taksit alanlari anlamsizdir; sunucu da reddediyor.
            Raise(nameof(ShowInstallmentFields)); Raise(nameof(ShowDownPayment)); Raise(nameof(AmountLabel));
        }
    }
    public bool ShowInstallmentFields => SelectedKind.Key != TuitionPlanKinds.Daily;
    public bool ShowDownPayment => SelectedKind.Key == TuitionPlanKinds.Installment;
    public string AmountLabel => SelectedKind.Key switch
    {
        TuitionPlanKinds.Monthly => "Aylık ücret (₺)",
        TuitionPlanKinds.Daily => "Günlük ücret (₺)",
        _ => "Toplam ücret (₺)"
    };

    public string Period { get => period; set => Set(ref period, value); }
    public string AmountText { get => amountText; set => Set(ref amountText, value); }
    public string DownPaymentText { get => downPaymentText; set => Set(ref downPaymentText, value); }
    public string InstallmentCountText { get => installmentCountText; set => Set(ref installmentCountText, value); }
    public string DueDayText { get => dueDayText; set => Set(ref dueDayText, value); }
    public DateTime StartsOn { get => startsOn; set => Set(ref startsOn, value); }
    public string? Note { get => note; set => Set(ref note, value); }
    public bool DeleteArmed { get => deleteArmed; private set { if (Set(ref deleteArmed, value)) Raise(nameof(DeleteButtonText)); } }
    public string DeleteButtonText => DeleteArmed ? "Silmeyi Onayla" : "Planı Sil";

    public TuitionPlanDetails? SelectedPlan
    {
        get => selectedPlan;
        set
        {
            if (!Set(ref selectedPlan, value)) return;
            DeleteArmed = false;
            FillForm(value);
            Installments.Clear();
            foreach (var row in value?.Installments ?? []) Installments.Add(new TuitionInstallmentRowViewModel(row));
            Raise(nameof(HasInstallments)); Raise(nameof(PlanSummaryText));
            RefreshCommands();
        }
    }

    public bool HasInstallments => Installments.Count > 0;
    public string PlanSummaryText => SelectedPlan is null
        ? "Soldan bir plan seçin ya da yeni plan tanımlayın."
        : $"{SelectedPlan.KindLabel} · {SelectedPlan.Amount.ToString("C2", Turkish)}"
          + $" · Ödenen {SelectedPlan.TotalPaid.ToString("C2", Turkish)}"
          + $" · Kalan {SelectedPlan.Outstanding.ToString("C2", Turkish)}"
          + (SelectedPlan.OverdueAmount > 0 ? $" · Gecikmiş {SelectedPlan.OverdueAmount.ToString("C2", Turkish)}" : "");

    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand NewPlanCommand { get; }

    /// <summary>Sinif listesini disaridan alir (Tanimlar ekraniyla ayni kaynak).</summary>
    public void SetClasses(IEnumerable<LookupRecord> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        Classes.Clear();
        // Ucret plani anasinifi icin eklendi; listeyi daraltmak yanlis sinifa plan yazmayi onler.
        foreach (var item in classes.Where(x => x.Kind == ClassKinds.Preschool)) Classes.Add(item);
        SelectedClass ??= Classes.FirstOrDefault();
    }

    public async Task LoadAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            // Sinif listesi bir kez cekilir; plan yalnizca anasinifi siniflarina tanimlanir.
            if (Classes.Count == 0 && definitions is not null)
                SetClasses(await definitions.LookupsAsync(DefinitionsApiClient.Classes));
            var result = await api.PlansAsync(new TuitionPlanFilter(PageSize: 200));
            Plans.Clear();
            foreach (var plan in result.Items) Plans.Add(plan);
            if (Plans.Count == 0) StatusMessage = "Henüz ücret planı tanımlanmadı.";
        }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = "Ücret planları alınamadı. Bağlantıyı kontrol edin."; }
        finally { IsLoading = false; }
    }

    /// <summary>Formu dogrular ve istegi kurar; hata varsa mesaj doner ve istek gonderilmez.</summary>
    public SaveTuitionPlanRequest? BuildRequest(out string? error)
    {
        error = null;
        if (SelectedClass is null) { error = "Sınıf seçin."; return null; }
        if (!TryMoney(AmountText, out var amount)) { error = "Tutar sıfırdan büyük ve en fazla iki ondalıklı olmalıdır (örn. 48.000,00)."; return null; }

        decimal down = 0;
        var count = 0;
        var dueDay = TuitionSchedule.MinDueDay;
        if (SelectedKind.Key != TuitionPlanKinds.Daily)
        {
            if (!int.TryParse(InstallmentCountText, NumberStyles.Integer, Turkish, out count)
                || count is < 1 or > TuitionSchedule.MaxInstallmentCount)
            { error = $"Taksit sayısı 1 ile {TuitionSchedule.MaxInstallmentCount} arasında olmalıdır."; return null; }
            if (!int.TryParse(DueDayText, NumberStyles.Integer, Turkish, out dueDay)
                || dueDay is < TuitionSchedule.MinDueDay or > TuitionSchedule.MaxDueDay)
            { error = $"Ayın günü {TuitionSchedule.MinDueDay} ile {TuitionSchedule.MaxDueDay} arasında olmalıdır (29-31 her ayda bulunmaz)."; return null; }
            if (SelectedKind.Key == TuitionPlanKinds.Installment && !string.IsNullOrWhiteSpace(DownPaymentText))
            {
                if (!TryMoney(DownPaymentText, out down)) { error = "Peşinat en fazla iki ondalıklı bir tutar olmalıdır."; return null; }
                if (down >= amount) { error = "Peşinat toplam ücretten küçük olmalıdır."; return null; }
            }
        }

        return new SaveTuitionPlanRequest(SelectedKind.Key, Period?.Trim() ?? "", amount, SelectedClass.Id, null,
            down, count, dueDay, DateOnly.FromDateTime(StartsOn.Date), string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
    }

    private async Task SaveAsync()
    {
        ErrorMessage = StatusMessage = null;
        var request = BuildRequest(out var error);
        if (request is null) { ErrorMessage = error; return; }
        IsLoading = true;
        try
        {
            var plan = await api.SavePlanAsync(request);
            StatusMessage = $"Plan kaydedildi: {plan.Installments.Count} taksit oluşturuldu.";
            await LoadAsync();
            SelectedPlan = Plans.FirstOrDefault(x => x.Id == plan.Id) ?? plan;
        }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = "Plan kaydedilemedi. Bağlantıyı kontrol edin."; }
        finally { IsLoading = false; }
    }

    /// <summary>Iki adimli silme: ilk basista onay istenir, ikincide silinir.</summary>
    private async Task DeleteAsync()
    {
        if (SelectedPlan is null) return;
        if (!DeleteArmed) { DeleteArmed = true; return; }
        IsLoading = true; ErrorMessage = StatusMessage = null;
        try
        {
            await api.DeletePlanAsync(SelectedPlan.Id);
            StatusMessage = "Plan kaldırıldı. Tahsilatı olan planlar silinmez, pasife alınır.";
            DeleteArmed = false;
            SelectedPlan = null;
            await LoadAsync();
        }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (IsApiFailure(ex)) { ErrorMessage = "Plan silinemedi. Bağlantıyı kontrol edin."; }
        finally { IsLoading = false; }
    }

    private void ResetForm()
    {
        SelectedPlan = null;
        SelectedKind = Kinds[0];
        Period = DefaultPeriod();
        AmountText = ""; DownPaymentText = ""; InstallmentCountText = "10"; DueDayText = "5";
        StartsOn = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Note = null; ErrorMessage = StatusMessage = null;
    }

    private void FillForm(TuitionPlanDetails? plan)
    {
        if (plan is null) return;
        SelectedKind = Kinds.FirstOrDefault(x => x.Key == plan.Kind) ?? Kinds[0];
        Period = plan.Period;
        AmountText = plan.Amount.ToString("N2", Turkish);
        DownPaymentText = plan.DownPayment > 0 ? plan.DownPayment.ToString("N2", Turkish) : "";
        InstallmentCountText = plan.InstallmentCount.ToString(CultureInfo.InvariantCulture);
        DueDayText = plan.DueDayOfMonth.ToString(CultureInfo.InvariantCulture);
        StartsOn = plan.StartsOn.ToDateTime(TimeOnly.MinValue);
        Note = plan.Note;
        if (plan.ClassId is { } classId) SelectedClass = Classes.FirstOrDefault(x => x.Id == classId) ?? SelectedClass;
    }

    /// <summary>Egitim yili etiketi: Eylul'den once onceki yil sayilir.</summary>
    private static string DefaultPeriod()
    {
        var year = DateTime.Today.Month >= 9 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        return $"{year}-{year + 1}";
    }

    /// <summary>
    /// Tutar ayristirma. KULTUR GUVENLI ayristiriciyi kullanir; <c>decimal.TryParse</c> +
    /// tr-TR DEGIL.
    ///
    /// <para>
    /// Eskiden <c>decimal.TryParse(text, NumberStyles.Number, Turkish, ...)</c> idi.
    /// <c>NumberStyles.Number</c> <c>AllowThousands</c> icerir ve tr-TR'de grup ayiraci
    /// NOKTA'dir: "6000.00" 600000 olarak okunuyor, yanindaki
    /// <c>decimal.Round(value, 2) == value</c> kontrolu de hicbir sey yakalamiyordu
    /// (sonuc zaten tam sayi). Sinif planina 6.000 TL yazan kullanici siniftaki her
    /// ogrenciye 600.000 TL borc yaziyordu.
    /// </para>
    /// <para>
    /// <see cref="CashViewModel.TryParseAmount"/> ayni hatayi Kasa'da cozmustu; ayni
    /// ayristirici burada da kullanilir ki iki ekran ayni yazimi ayni okusun.
    /// </para>
    /// </summary>
    private static bool TryMoney(string? text, out decimal value) =>
        CashViewModel.TryParseAmount(text, out value) && decimal.Round(value, 2) == value;

    private static bool IsApiFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException;

    private void RefreshCommands()
    {
        (SaveCommand as AsyncCommand)?.Refresh();
        (DeleteCommand as AsyncCommand)?.Refresh();
        (NewPlanCommand as RelayCommand)?.Refresh();
    }
}
