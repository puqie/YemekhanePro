using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Common;
using Yemekhane.Application.Tuition;
using Yemekhane.Desktop.Services;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>Anasinifi listesinin bir satiri; tabloda bicimlenmis haliyle gosterilir.</summary>
public sealed class KindergartenRowViewModel(KindergartenStudentRow row)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();

    public KindergartenStudentRow Row => row;
    public Guid StudentId => row.StudentId;
    public string StudentNo => row.StudentNo;
    public string StudentName => row.StudentName;
    public string ClassName => row.ClassName ?? "";
    public bool HasPlan => row.HasPlan;
    /// <summary>"3/10"; plan yoksa "Plan yok".</summary>
    public string Progress => row.Progress;
    public string PaymentCountText => row.HasPlan ? row.PaymentCount.ToString(CultureInfo.InvariantCulture) : "";
    public string TotalPaid => row.HasPlan ? row.TotalPaid.ToString("C2", Turkish) : "";
    public string Outstanding => row.HasPlan ? row.Outstanding.ToString("C2", Turkish) : "";
    public string OverdueText => row.OverdueCount == 0 ? "" : row.OverdueAmount.ToString("C2", Turkish);
    public string OverdueCountText => row.OverdueCount == 0 ? "" : $"{row.OverdueCount} taksit gecikmiş";
    public string NextDueText => row.NextDueOn is { } due ? due.ToString("dd.MM.yyyy", Turkish) : row.HasPlan ? "Tamamlandı" : "";
    public string LastPaidText => row.LastPaidAt is { } at ? TimeZoneInfo.ConvertTime(at, Istanbul).ToString("dd.MM.yyyy", Turkish) : "";
    /// <summary>Gecikmis borcu olan satir kirmizi: memur once bunlari arar.</summary>
    public bool IsOverdue => row.OverdueCount > 0;
    public bool IsComplete => row.HasPlan && row.InstallmentCount > 0 && row.PaidInstallments == row.InstallmentCount;
    /// <summary>Arama: ad, numara ve sinif Turkce harfe duyarsiz eslestirilir.</summary>
    public string SearchText { get; } = TurkishSearchText.Normalize(row.StudentName + " " + row.StudentNo + " " + (row.ClassName ?? ""));

    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}

/// <summary>Bir tahsilatin taksite sayilan parcasi; en yeni ustte.</summary>
public sealed class KindergartenPaymentRowViewModel(TuitionPaymentDetails row)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();

    public string Date => TimeZoneInfo.ConvertTime(row.TransactionAt, Istanbul).ToString("dd.MM.yyyy", Turkish);
    public string Amount => row.Amount.ToString("C2", Turkish);
    public string Installment => row.Sequence == 0 ? "Peşinat" : row.Sequence.ToString(CultureInfo.InvariantCulture) + ". taksit";
    public string IncomeType => row.IncomeTypeName;
    public string Description => row.Description ?? "";

    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}

/// <summary>
/// Anasinifi ekrani: anasinifi ogrencileri tek listede, her satirda "kacinci taksitte" (3/10),
/// odenen/kalan/gecikmis; satira tiklayinca taksit tablosu ve taksite sayilan tahsilatlar.
///
/// <para>
/// Saha: "anasinifi ogrencilerini gorebilecegim bir yer yok; odemelerini kasadan giriyorum ama
/// kacinci taksiti odedigini, kac kere odedigini gormem gerekiyor." Tahsilat kasadan girilmeye
/// devam eder; "taksite sayilir" isaretli turle girilen tutar sunucuda siradaki taksitlere
/// sayilir. Menu ogesi yalnizca anasinifi ogrencisi varsa gorunur (<see cref="HasStudents"/>).
/// </para>
/// </summary>
public sealed class KindergartenViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly ITuitionApiClient api;
    private readonly HashSet<string> permissions;
    private readonly List<KindergartenRowViewModel> all = [];
    private string? search, error, statusMessage;
    private bool isLoading, isDetailLoading, hasStudents, hasTuitionIncomeType = true;
    private int unappliedIncomeCount, selectVersion;
    private KindergartenRowViewModel? selectedStudent;
    private StudentTuitionSummary? detail;

    public KindergartenViewModel(ITuitionApiClient api, IEnumerable<string> permissions)
    {
        this.api = api;
        this.permissions = permissions.ToHashSet(StringComparer.Ordinal);
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsLoading);
        ReconcileCommand = new AsyncCommand(ReconcileAsync, () => ShowReconcile && !IsLoading);
        ClearSearchCommand = new RelayCommand(() => Search = null, () => !string.IsNullOrEmpty(Search));
    }

    public ObservableCollection<KindergartenRowViewModel> Students { get; } = [];
    public ObservableCollection<TuitionInstallmentRowViewModel> Installments { get; } = [];
    public ObservableCollection<KindergartenPaymentRowViewModel> Payments { get; } = [];

    public bool CanWrite => permissions.Contains("cash.write") || permissions.Contains("cash.manage");
    public string? Search { get => search; set { if (Set(ref search, value)) { ApplyFilter(); (ClearSearchCommand as RelayCommand)?.Refresh(); } } }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) { Raise(nameof(IsEmpty)); Raise(nameof(ShowContent)); RefreshCommands(); } } }
    public bool IsDetailLoading { get => isDetailLoading; private set => Set(ref isDetailLoading, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); Raise(nameof(IsEmpty)); Raise(nameof(ShowContent)); } } }
    public bool HasError => Error is not null;
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatusMessage)); } }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Kabuk bunu izler: false ise "Anasınıfı" menu ogesi gizlenir.</summary>
    public bool HasStudents { get => hasStudents; private set => Set(ref hasStudents, value); }
    /// <summary>Kasa > Gelir Turleri'nde isaretli tur yoksa tahsilatlar hic taksite sayilmaz; ekran bunu soyler.</summary>
    public bool HasTuitionIncomeType { get => hasTuitionIncomeType; private set { if (Set(ref hasTuitionIncomeType, value)) Raise(nameof(ShowTypeHint)); } }
    public bool ShowTypeHint => !HasTuitionIncomeType && !IsLoading && Error is null;
    public int UnappliedIncomeCount { get => unappliedIncomeCount; private set { if (Set(ref unappliedIncomeCount, value)) { Raise(nameof(ShowReconcile)); Raise(nameof(ReconcileText)); RefreshCommands(); } } }
    public bool ShowReconcile => CanWrite && UnappliedIncomeCount > 0;
    public string ReconcileText => $"Kasadaki {UnappliedIncomeCount} tahsilatı taksitlere işle";

    public bool IsEmpty => !IsLoading && Error is null && all.Count == 0;
    public bool ShowContent => !IsLoading && Error is null && all.Count > 0;
    /// <summary>Arama hic satir birakmadiysa: liste bos ama okulda ogrenci var.</summary>
    public bool HasFilterNoMatch => all.Count > 0 && Students.Count == 0;
    public string SummaryText
    {
        get
        {
            if (all.Count == 0) return "Kayıtlı anasınıfı öğrencisi yok.";
            var rows = all.Select(x => x.Row).ToList();
            var overdue = rows.Count(x => x.OverdueCount > 0);
            var complete = rows.Count(x => x.HasPlan && x.InstallmentCount > 0 && x.PaidInstallments == x.InstallmentCount);
            return $"{rows.Count} öğrenci · {complete} tamamladı · {overdue} gecikmiş · ödenen {rows.Sum(x => x.TotalPaid).ToString("C2", Turkish)} · kalan {rows.Sum(x => x.Outstanding).ToString("C2", Turkish)}";
        }
    }

    public KindergartenRowViewModel? SelectedStudent
    {
        get => selectedStudent;
        set { if (Set(ref selectedStudent, value)) _ = SelectAsync(value); }
    }

    public StudentTuitionSummary? Detail
    {
        get => detail;
        private set
        {
            if (!Set(ref detail, value)) return;
            Raise(nameof(HasDetail)); Raise(nameof(DetailHasPlan)); Raise(nameof(DetailTitle)); Raise(nameof(DetailPlanText)); Raise(nameof(DetailProgressText));
        }
    }
    public bool HasDetail => Detail is not null;
    public bool DetailHasPlan => Detail?.Plan is not null;
    public string DetailTitle => Detail is null ? "" : $"{Detail.StudentName} · No {Detail.StudentNo}" + (Detail.ClassName is null ? "" : " · " + Detail.ClassName);
    public string DetailPlanText => Detail?.Plan is not { } plan
        ? "Bu öğrenci için ücret planı tanımlı değil. Kasa → Anasınıfı Ücretleri sekmesinden sınıfa ya da öğrenciye plan tanımlayın."
        : $"{plan.KindLabel} · {plan.Period} · toplam {plan.TotalDue.ToString("C2", Turkish)} · ödenen {plan.TotalPaid.ToString("C2", Turkish)} · kalan {plan.Outstanding.ToString("C2", Turkish)}"
          + (plan.OverdueAmount > 0 ? $" · gecikmiş {plan.OverdueAmount.ToString("C2", Turkish)}" : "")
          + (Detail.Inherited ? " · sınıf planı" : " · öğrenciye özel plan")
          + (selectedStudent?.LastPaidText is { Length: > 0 } lastPaid ? $" · son ödeme {lastPaid}" : "");
    public string DetailProgressText
    {
        get
        {
            if (Detail?.Plan is not { } plan) return "";
            var live = plan.Installments.Where(x => !x.IsCancelled).ToList();
            var paid = live.Count(x => x.Status == TuitionInstallmentStatuses.Paid);
            return $"{paid}/{live.Count} taksit ödendi · {Detail.Payments?.Count ?? 0} tahsilat";
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand ReconcileCommand { get; }
    public ICommand ClearSearchCommand { get; }

    /// <summary>Kabuk acilista ve ekrana her giriste cagirir; API hatalari Error'a yazilir, firlatilmaz.</summary>
    public Task ProbeAsync() => LoadAsync();

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var overview = await api.KindergartenAsync();
            all.Clear();
            foreach (var row in overview.Students) all.Add(new KindergartenRowViewModel(row));
            UnappliedIncomeCount = overview.UnappliedIncomeCount;
            HasTuitionIncomeType = overview.HasTuitionIncomeType;
            HasStudents = all.Count > 0;
            ApplyFilter();
            Raise(nameof(SummaryText));
            // Secili ogrenci listede hala varsa ayrintisi tazelenir (tahsilat sonrasi Yenile).
            if (selectedStudent is { } selected)
            {
                var again = all.FirstOrDefault(x => x.StudentId == selected.StudentId);
                selectedStudent = again;
                Raise(nameof(SelectedStudent));
                await SelectAsync(again);
            }
        }
        catch (LoginRequiredException)
        {
            Error = "Anasınıfı listesini görüntülemek için kasa okuma izni olan bir oturum gerekiyor.";
        }
        catch (ApiRequestException exception)
        {
            Error = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Anasınıfı listesi alınamadı. API bağlantısını kontrol edin.";
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(ShowTypeHint));
        }
    }

    /// <summary>Satir secimi: taksit tablosu ve tahsilatlar sunucudan okunur.</summary>
    public async Task SelectAsync(KindergartenRowViewModel? row)
    {
        // Hizli art arda secimde yalnizca SON secimin yaniti islenir; eski yanit tabloyu doldurmaz.
        var version = ++selectVersion;
        if (row is null) { Installments.Clear(); Payments.Clear(); Detail = null; return; }
        IsDetailLoading = true;
        try
        {
            var summary = await api.ForStudentAsync(row.StudentId);
            if (version != selectVersion) return;
            Detail = summary;
            Installments.Clear();
            Payments.Clear();
            foreach (var installment in summary.Plan?.Installments ?? []) Installments.Add(new TuitionInstallmentRowViewModel(installment));
            foreach (var payment in summary.Payments ?? []) Payments.Add(new KindergartenPaymentRowViewModel(payment));
        }
        catch (LoginRequiredException) { Error = "Öğrenci taksitlerini görüntülemek için kasa okuma izni gerekiyor."; }
        catch (ApiRequestException exception) { Error = exception.Message; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        { Error = "Öğrencinin taksit bilgisi alınamadı. API bağlantısını kontrol edin."; }
        finally { IsDetailLoading = false; }
    }

    private async Task ReconcileAsync()
    {
        Error = null;
        StatusMessage = null;
        try
        {
            var result = await api.ReconcileAsync();
            StatusMessage = result.Applied == 0
                ? $"{result.Examined} tahsilat incelendi; açık taksiti olan öğrenci bulunmadığı için hiçbiri işlenmedi."
                : $"{result.Applied} tahsilat taksitlere sayıldı" + (result.SkippedNoInstallment > 0 ? $"; {result.SkippedNoInstallment} tahsilatın açık taksiti yok." : ".");
            await LoadAsync();
        }
        catch (LoginRequiredException) { Error = "Tahsilatları taksitlere işlemek için kasa yazma izni gerekiyor."; }
        catch (ApiRequestException exception) { Error = exception.Message; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        { Error = "Tahsilatlar taksitlere işlenemedi. API bağlantısını kontrol edin."; }
    }

    private void ApplyFilter()
    {
        var term = TurkishSearchText.Normalize(Search);
        Students.Clear();
        foreach (var row in all)
        {
            if (term.Length == 0 || row.SearchText.Contains(term, StringComparison.Ordinal)) Students.Add(row);
        }
        Raise(nameof(HasFilterNoMatch));
    }

    private void RefreshCommands()
    {
        (RefreshCommand as AsyncCommand)?.Refresh();
        (ReconcileCommand as AsyncCommand)?.Refresh();
    }
}
