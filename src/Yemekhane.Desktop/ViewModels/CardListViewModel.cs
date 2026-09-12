using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Cards;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>Durum suzgeci secenegi: Value null = tumu, true = aktif, false = pasif.</summary>
public sealed record CardStatusOption(string Name, bool? Value);

/// <summary>
/// Kartlar ekraninin tek satiri. Pasiflestirme IKI ADIMDIR (neden + onay) ve o durum satirda
/// tutulur: sunucu nedeni zorunlu tutar, bos nedenle "Onayla" pasif kalir.
/// </summary>
public sealed class CardListRowViewModel(CardListRow value) : ObservableObject
{
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private bool isDeactivateArmed;
    private string? deactivateReason;

    public Guid CardId => value.CardId;
    public Guid StudentId => value.StudentId;
    public string StudentNo => value.StudentNo;
    public string StudentName => value.StudentName;
    public string ClassName => value.ClassName ?? "";
    public string CardNumber => value.CardNumber;
    public string PrintedNumber => value.PrintedNumber ?? "";
    public bool IsActive => value.IsActive;
    public bool IsPassive => !value.IsActive;
    /// <summary>Ogrenci pasif/silinmisse kart aktif olsa da turnikeden gecemez; durumda soylenir.</summary>
    public string StatusText => value.IsActive
        ? value.StudentActive ? "Aktif" : "Aktif · öğrenci pasif"
        : "Pasif";
    public string PeriodText => Format(value.ValidFrom) + " – " + (value.ValidTo is { } to ? Format(to) : "devam ediyor");
    public string ReasonText => value.ReplacementReason ?? "";

    public bool IsDeactivateArmed
    {
        get => isDeactivateArmed;
        set { if (Set(ref isDeactivateArmed, value)) { Raise(nameof(IsDeactivateIdle)); Raise(nameof(CanConfirmDeactivate)); } }
    }
    public bool IsDeactivateIdle => !IsDeactivateArmed;
    public string? DeactivateReason
    {
        get => deactivateReason;
        set { if (Set(ref deactivateReason, value)) Raise(nameof(CanConfirmDeactivate)); }
    }
    public bool CanConfirmDeactivate => IsDeactivateArmed && !string.IsNullOrWhiteSpace(DeactivateReason);

    private static string Format(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Istanbul).ToString("dd.MM.yyyy", Turkish);

    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}

/// <summary>
/// Kartlar ekrani: okuldaki TUM kartlarin aktif/pasif durumu tek listede; satirdan
/// pasiflestirme ve geri acma.
///
/// <para>
/// Saha: turnike "Kart pasif" deyince operator karti nereden acacagini bilmiyordu; pasif
/// kartlar yalnizca ogrencinin Kartlar sekmesinde, salt okunur satir olarak gorunuyordu.
/// </para>
/// </summary>
public sealed class CardListViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 50;
    private readonly ICardListApiClient api;
    private readonly HashSet<string> permissions;
    private string? search, error, statusMessage;
    private bool isLoading;
    private int page = 1, totalCount, activeCount, passiveCount;
    private CardStatusOption selectedStatus;

    public CardListViewModel(ICardListApiClient api, IEnumerable<string> permissions)
    {
        this.api = api;
        this.permissions = permissions.ToHashSet(StringComparer.Ordinal);
        selectedStatus = StatusOptions[0];
        RefreshCommand = new AsyncCommand(InitializeAsync, () => !IsLoading);
        SearchCommand = new AsyncCommand(() => LoadAsync(1), () => !IsLoading);
        NextPageCommand = new AsyncCommand(() => LoadAsync(Page + 1), () => Page * PageSize < TotalCount && !IsLoading);
        PreviousPageCommand = new AsyncCommand(() => LoadAsync(Page - 1), () => Page > 1 && !IsLoading);
        ArmDeactivateCommand = new RelayCommand<CardListRowViewModel>(row => { row.IsDeactivateArmed = true; Error = null; }, _ => CanManage);
        CancelDeactivateCommand = new RelayCommand<CardListRowViewModel>(row => { row.IsDeactivateArmed = false; row.DeactivateReason = null; });
        ConfirmDeactivateCommand = new AsyncCommand<CardListRowViewModel>(DeactivateAsync, _ => CanManage);
        ReactivateCommand = new AsyncCommand<CardListRowViewModel>(ReactivateAsync, _ => CanManage);
    }

    public IReadOnlyList<CardStatusOption> StatusOptions { get; } = [new("Tümü", null), new("Aktif", true), new("Pasif", false)];
    public ObservableCollection<CardListRowViewModel> Rows { get; } = [];

    public bool CanManage => permissions.Contains("cards.manage");
    public string? Search { get => search; set => Set(ref search, value); }
    public CardStatusOption SelectedStatus { get => selectedStatus; set => Set(ref selectedStatus, value); }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) { Raise(nameof(IsEmpty)); RefreshCommands(); } } }
    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); Raise(nameof(IsEmpty)); } } }
    public bool HasError => Error is not null;
    /// <summary>Pasiflestirme / geri acma sonucu; hata degil, bilgi.</summary>
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatusMessage)); } }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public int Page { get => page; private set { if (Set(ref page, value)) Raise(nameof(PageText)); } }
    public int TotalCount { get => totalCount; private set { if (Set(ref totalCount, value)) { Raise(nameof(PageText)); Raise(nameof(IsEmpty)); RefreshCommands(); } } }
    public int ActiveCount { get => activeCount; private set { if (Set(ref activeCount, value)) Raise(nameof(SummaryText)); } }
    public int PassiveCount { get => passiveCount; private set { if (Set(ref passiveCount, value)) Raise(nameof(SummaryText)); } }
    public string PageText => $"Sayfa {Page} / {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize))}   •   {TotalCount} kart";
    /// <summary>Suzgecten BAGIMSIZ toplamlar: "312 aktif, 14 pasif kart".</summary>
    public string SummaryText => ActiveCount + PassiveCount == 0
        ? "Henüz kart tanımlı değil."
        : $"{ActiveCount} aktif, {PassiveCount} pasif kart";
    /// <summary>"Kayit yok" yalnizca yukleme bitip hata yokken ve hic satir gelmediyse dogrudur.</summary>
    public bool IsEmpty => !IsLoading && Error is null && TotalCount == 0;

    public ICommand RefreshCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public ICommand ArmDeactivateCommand { get; }
    public ICommand CancelDeactivateCommand { get; }
    public ICommand ConfirmDeactivateCommand { get; }
    public ICommand ReactivateCommand { get; }

    /// <summary>Acilis ve Yenile: bulunulan sayfa korunur, suzgec korunur.</summary>
    public Task InitializeAsync() => LoadAsync(Page);

    public async Task LoadAsync(int requestedPage)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var result = await api.ListAsync(Search, SelectedStatus.Value, Math.Max(1, requestedPage), PageSize);
            Rows.Clear();
            foreach (var row in result.Items) Rows.Add(new CardListRowViewModel(row));
            Page = result.Page;
            TotalCount = result.TotalCount;
            ActiveCount = result.ActiveCount;
            PassiveCount = result.PassiveCount;
        }
        catch (LoginRequiredException)
        {
            Error = "Kartları görüntülemek için cards.manage izni olan bir oturum gerekiyor.";
        }
        catch (ApiRequestException exception)
        {
            Error = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Kart listesi alınamadı. API bağlantısını kontrol edin.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Satirdaki "Onayla": neden bossa sunucuya gidilmez, hata satirda degil ustte soylenir.</summary>
    private async Task DeactivateAsync(CardListRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanConfirmDeactivate) { Error = "Pasifleştirme nedeni zorunludur."; return; }
        Error = null;
        StatusMessage = null;
        try
        {
            await api.DeactivateAsync(row.CardId, row.DeactivateReason!.Trim());
            StatusMessage = $"{row.CardNumber} numaralı kart pasifleştirildi ({row.StudentName}). Turnike bu kartı artık tanımaz.";
            await LoadAsync(Page);
        }
        catch (LoginRequiredException)
        {
            Error = "Kart pasifleştirmek için cards.manage izni olan bir oturum gerekiyor.";
        }
        catch (ApiRequestException exception)
        {
            Error = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Kart pasifleştirilemedi. API bağlantısını kontrol edin.";
        }
    }

    /// <summary>Satirdaki "Aktifleştir": sunucu reddederse (ogrencinin aktif karti var) mesaji AYNEN gosterilir.</summary>
    private async Task ReactivateAsync(CardListRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Error = null;
        StatusMessage = null;
        try
        {
            var card = await api.ReactivateAsync(row.CardId);
            StatusMessage = $"{card.CardNumber} numaralı kart yeniden aktif ({row.StudentName}); turnike bu kartı yine tanır.";
            await LoadAsync(Page);
        }
        catch (LoginRequiredException)
        {
            Error = "Kart aktifleştirmek için cards.manage izni olan bir oturum gerekiyor.";
        }
        catch (ApiRequestException exception)
        {
            Error = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Kart aktifleştirilemedi. API bağlantısını kontrol edin.";
        }
    }

    private void RefreshCommands()
    {
        (RefreshCommand as AsyncCommand)?.Refresh();
        SearchCommand.Refresh(); NextPageCommand.Refresh(); PreviousPageCommand.Refresh();
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
