using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Devices;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>Tek bir cihazin kart yukleme ozeti.</summary>
public sealed class DeviceCardSummaryViewModel(DeviceCardSummary value)
{
    public Guid DeviceId => value.DeviceId;
    public string DeviceName => value.DeviceName;
    public int Loaded => value.Loaded;
    public int Pending => value.Pending;
    public int Failed => value.Failed;

    /// <summary>Operatorun mudahale etmesi gereken cihaz: bekleyen veya hatali karti var.</summary>
    public bool NeedsAttention => value.Pending > 0 || value.Failed > 0;
    public bool HasFailures => value.Failed > 0;

    public string StatusText => value switch
    {
        { Failed: > 0, Pending: > 0 } => $"{value.Pending} bekliyor, {value.Failed} hatalı",
        { Failed: > 0 } => $"{value.Failed} kart yüklenemedi",
        { Pending: > 0 } => $"{value.Pending} kart bekliyor",
        _ => "Tüm kartlar yüklü"
    };
}

/// <summary>Bir cihazda yuklenmeyi bekleyen kart satiri.</summary>
public sealed class PendingCardViewModel(PendingDeviceCard value)
{
    public string CardNumber => value.CardNumber;
    public string StudentName => value.StudentName;
    /// <summary>"No 5003 · 5A · Kart 8350003": ayni adli ogrenciler kuyrukta ancak boyle ayirt edilir.</summary>
    public string IdentityText => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(value.StudentNo) ? null : "No " + value.StudentNo,
        string.IsNullOrWhiteSpace(value.ClassName) ? null : value.ClassName,
        "Kart " + value.CardNumber
    }.Where(x => x is not null));
    public int AttemptCount => value.AttemptCount;
    public string ActionText => value.IsRemoval ? "Siliniyor" : "Yükleniyor";
    public bool HasRetried => value.AttemptCount > 0;
    public string AttemptText => value.AttemptCount == 0 ? "İlk deneme" : $"{value.AttemptCount} başarısız deneme";
}

/// <summary>
/// "Cihazdaki kartlar" tablosunun tek satiri (eski programdaki Cihaz Sicil Listesi'nin karsiligi).
/// Kaynak sunucunun kart-cihaz durum tablosudur; cihaz bellegi dogrudan okunmaz.
/// </summary>
public sealed class DeviceCardRowViewModel(DeviceCardListRow value)
{
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();
    public Guid CardId => value.CardId;
    public string StudentNo => value.StudentNo;
    public string StudentName => value.StudentName;
    public string ClassName => value.ClassName ?? "";
    public string CardNumber => value.CardNumber;
    public string Status => value.Status;
    public string StatusText => value.Status switch
    {
        DeviceCardSyncStatus.Loaded => "Yüklendi",
        DeviceCardSyncStatus.Pending => "Bekliyor",
        DeviceCardSyncStatus.PendingRemoval => "Siliniyor",
        DeviceCardSyncStatus.Failed => "Hata",
        DeviceCardSyncStatus.Removed => "Silindi",
        _ => value.Status
    };
    public bool IsFailed => value.Status == DeviceCardSyncStatus.Failed;
    public bool IsLoaded => value.Status == DeviceCardSyncStatus.Loaded;
    public string LastSyncedText => value.LastSyncedAt is { } at
        ? TimeZoneInfo.ConvertTime(at, Istanbul).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))
        : "";
    public string LastError => value.LastError ?? "";
    public int AttemptCount => value.AttemptCount;
    /// <summary>
    /// Yeniden yukleme yalnizca HATALI kartta anlamlidir: sunucu (DeviceCardSyncService.QueueCardAsync) yuklu
    /// karti kuyruga almaz, bekleyen zaten siradadir. Dugme her satirda gorunur ama nedeni ipucunda soylenir.
    /// </summary>
    public bool CanResync => IsFailed;
    public string ResyncHint => value.Status switch
    {
        DeviceCardSyncStatus.Failed => "Kartı yeniden yükleme kuyruğuna alır",
        DeviceCardSyncStatus.Loaded => "Kart cihazda zaten yüklü",
        DeviceCardSyncStatus.PendingRemoval => "Kart cihazdan silinmeyi bekliyor",
        _ => "Kart zaten yükleme sırasında"
    };

    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}

/// <summary>
/// Kart-cihaz yukleme durumu ekrani.
///
/// Operatorun gormesi gereken sey toplam sayilar degil, hangi cihazda ne eksik oldugudur:
/// bir turnikede eksik kalan tek bir kart, o ogrencinin o kapidan gecememesi demektir.
/// </summary>
public sealed class DeviceCardsViewModel : ObservableObject, IDisposable
{
    public const int CardsPageSize = 50;
    /// <summary>Panel sekmeleri: 0 = cihazdaki kartlar, 1 = bekleyen kartlar (DeviceCardsView sirasi).</summary>
    public const int CardsTab = 0;
    public const int PendingTab = 1;
    private readonly IDeviceCardsApiClient api;
    private readonly IDeviceCardListApiClient? cards;
    private bool isLoading;
    private bool isPushing;
    private bool isCardsLoading;
    private string? error;
    private string? statusMessage;
    private string? cardSearch;
    private int cardsPage = 1, cardsTotal;
    private int selectedPanelTab;
    private DeviceCardSummaryViewModel? selectedDevice;

    /// <param name="cards">
    /// Cihazdaki kart listesi istemcisi. Verilmezse <paramref name="api"/> ayni arayuzu uyguluyorsa o kullanilir
    /// (gercek DeviceCardsApiClient ikisini de uygular); hicbiri yoksa "Cihazdaki kartlar" sekmesi bos aciklama gosterir.
    /// </param>
    public DeviceCardsViewModel(IDeviceCardsApiClient api, IDeviceCardListApiClient? cards = null)
    {
        this.api = api;
        this.cards = cards ?? api as IDeviceCardListApiClient;
        RefreshCommand = new AsyncCommand(InitializeAsync);
        PushNowCommand = new AsyncCommand(PushNowAsync, () => !IsPushing);
        SelectDeviceCommand = new AsyncCommand<DeviceCardSummaryViewModel>(SelectDeviceAsync);
        ShowCardsCommand = new AsyncCommand<DeviceCardSummaryViewModel>(ShowCardsAsync);
        SearchCardsCommand = new AsyncCommand(() => LoadCardsAsync(1), () => HasSelection && !IsCardsLoading);
        NextCardsPageCommand = new AsyncCommand(() => LoadCardsAsync(CardsPage + 1), () => CardsPage * CardsPageSize < CardsTotal && !IsCardsLoading);
        PreviousCardsPageCommand = new AsyncCommand(() => LoadCardsAsync(CardsPage - 1), () => CardsPage > 1 && !IsCardsLoading);
        ResyncCardCommand = new AsyncCommand<DeviceCardRowViewModel>(ResyncCardAsync);
    }

    public ObservableCollection<DeviceCardSummaryViewModel> Devices { get; } = [];
    public ObservableCollection<PendingCardViewModel> PendingCards { get; } = [];
    public ObservableCollection<DeviceCardRowViewModel> DeviceCards { get; } = [];

    public bool HasCardList => cards is not null;
    public bool IsCardsLoading { get => isCardsLoading; private set { if (Set(ref isCardsLoading, value)) { Raise(nameof(IsCardsEmpty)); RefreshCardCommands(); } } }
    public string? CardSearch { get => cardSearch; set => Set(ref cardSearch, value); }
    public int CardsPage { get => cardsPage; private set { if (Set(ref cardsPage, value)) Raise(nameof(CardsPageText)); } }
    public int CardsTotal { get => cardsTotal; private set { if (Set(ref cardsTotal, value)) { Raise(nameof(CardsPageText)); Raise(nameof(IsCardsEmpty)); Raise(nameof(CardsTabHeader)); } } }
    public string CardsPageText => $"Sayfa {CardsPage} / {Math.Max(1, (int)Math.Ceiling(CardsTotal / (double)CardsPageSize))}   •   {CardsTotal} kart";
    public string CardsTabHeader => HasSelection ? $"Cihazdaki kartlar ({CardsTotal})" : "Cihazdaki kartlar";
    public string PendingTabHeader => HasSelection ? $"Bekleyen kartlar ({PendingCards.Count})" : "Bekleyen kartlar";
    /// <summary>"Kayit yok" yalnizca yukleme bitip hic satir gelmediyse; hata varken false kalir.</summary>
    public bool IsCardsEmpty => HasSelection && !IsCardsLoading && Error is null && CardsTotal == 0;
    /// <summary>0 = Cihazdaki kartlar, 1 = Bekleyen kartlar. Dugmeye gore secilir; kullanici sekmeyle degistirebilir.</summary>
    public int SelectedPanelTab { get => selectedPanelTab; set => Set(ref selectedPanelTab, value); }

    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public bool IsPushing { get => isPushing; private set { if (Set(ref isPushing, value)) PushNowCommand.Refresh(); } }
    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); Raise(nameof(IsCardsEmpty)); } } }
    public bool HasError => Error is not null;
    /// <summary>"Şimdi yükle" sonucunun kullaniciya gorunen ozeti; hata degil, bilgi.</summary>
    public string? StatusMessage { get => statusMessage; private set { if (Set(ref statusMessage, value)) Raise(nameof(HasStatusMessage)); } }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public DeviceCardSummaryViewModel? SelectedDevice
    {
        get => selectedDevice;
        private set { if (Set(ref selectedDevice, value)) { Raise(nameof(HasSelection)); Raise(nameof(IsCardsEmpty)); Raise(nameof(CardsTabHeader)); Raise(nameof(PendingTabHeader)); RefreshCardCommands(); } }
    }

    public bool HasSelection => SelectedDevice is not null;

    /// <summary>Filoda yuklenmeyi bekleyen veya hatali toplam kart sayisi.</summary>
    public int TotalOutstanding => Devices.Sum(device => device.Pending + device.Failed);
    public bool HasOutstanding => TotalOutstanding > 0;

    public string SummaryText => Devices.Count == 0
        ? "Kart yükleyen cihaz tanımlı değil."
        : HasOutstanding
            ? $"{TotalOutstanding} kart {Devices.Count(device => device.NeedsAttention)} cihazda bekliyor."
            : $"{Devices.Count} cihazın tüm kartları güncel.";

    public ICommand RefreshCommand { get; }
    public AsyncCommand PushNowCommand { get; }
    public ICommand SelectDeviceCommand { get; }
    public ICommand ShowCardsCommand { get; }
    public AsyncCommand SearchCardsCommand { get; }
    public AsyncCommand NextCardsPageCommand { get; }
    public AsyncCommand PreviousCardsPageCommand { get; }
    public ICommand ResyncCardCommand { get; }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var summaries = await api.GetSummaryAsync();
            Devices.Clear();
            // Mudahale gereken cihazlar basa alinir; operatorun aramasi gerekmesin.
            foreach (var summary in summaries
                         .OrderByDescending(value => value.Failed > 0)
                         .ThenByDescending(value => value.Pending)
                         .ThenBy(value => value.DeviceName, StringComparer.CurrentCulture))
                Devices.Add(new DeviceCardSummaryViewModel(summary));
            RaiseTotals();
        }
        catch (LoginRequiredException)
        {
            Error = "Kart durumunu görüntülemek için devices.read izni olan bir oturum gerekiyor.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Kart yükleme durumu alınamadı. API bağlantısını kontrol edin.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"Bekleyen kartları göster": paneli bekleyenler sekmesiyle acar; kart listesi de tazelenir.</summary>
    public Task SelectDeviceAsync(DeviceCardSummaryViewModel device) => SelectDeviceAsync(device, PendingTab);

    /// <summary>Cihaz secilir ve panel istenen sekmeyle acilir.</summary>
    private async Task SelectDeviceAsync(DeviceCardSummaryViewModel device, int tab)
    {
        ArgumentNullException.ThrowIfNull(device);
        var changed = !ReferenceEquals(SelectedDevice, device);
        SelectedDevice = device;
        // Sekme, veri gelmeden ONCE secilir: yukleme suresince panel once yanlis sekmeyi gosterip
        // sonra atlamamali (kullanici "Cihazdaki kartlar"a basip bekleyenler listesini goruyordu).
        SelectedPanelTab = tab;
        Error = null;
        try
        {
            var pending = await api.GetPendingAsync(device.DeviceId, 100);
            PendingCards.Clear();
            foreach (var card in pending) PendingCards.Add(new PendingCardViewModel(card));
            Raise(nameof(PendingTabHeader));
        }
        catch (LoginRequiredException)
        {
            Error = "Kart kuyruğunu görüntülemek için devices.read izni olan bir oturum gerekiyor.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Bekleyen kart listesi alınamadı. API bağlantısını kontrol edin.";
        }
        // Sekme basligindaki sayi ve tablo ayni cihazi gostermeli; cihaz degistiyse arama sifirlanir.
        if (changed) CardSearch = null;
        await LoadCardsAsync(changed ? 1 : CardsPage);
    }

    /// <summary>"Cihazdaki kartlar": paneli kart listesi sekmesiyle acar.</summary>
    public Task ShowCardsAsync(DeviceCardSummaryViewModel device) => SelectDeviceAsync(device, CardsTab);

    /// <summary>Secili cihazin kart listesini (arama + sayfa) sunucudan ceker.</summary>
    public async Task LoadCardsAsync(int page)
    {
        if (SelectedDevice is not { } device || cards is null) return;
        IsCardsLoading = true;
        try
        {
            var result = await cards.GetCardsAsync(device.DeviceId, CardSearch, Math.Max(1, page), CardsPageSize);
            DeviceCards.Clear();
            foreach (var row in result.Items) DeviceCards.Add(new DeviceCardRowViewModel(row));
            CardsPage = result.Page;
            CardsTotal = result.TotalCount;
        }
        catch (LoginRequiredException)
        {
            Error = "Cihaz kart listesini görüntülemek için devices.read izni olan bir oturum gerekiyor.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Cihaz kart listesi alınamadı. API bağlantısını kontrol edin.";
        }
        finally
        {
            IsCardsLoading = false;
        }
    }

    /// <summary>Satirdaki "Yeniden yükle": karti kuyruga alir, listeyi ve cihaz ozetini tazeler.</summary>
    public async Task ResyncCardAsync(DeviceCardRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Error = null;
        StatusMessage = null;
        try
        {
            await api.ResyncCardAsync(row.CardId);
            StatusMessage = $"{row.StudentName} (No {row.StudentNo}, kart {row.CardNumber}) yeniden yükleme kuyruğuna alındı.";
            await InitializeAsync();
            if (SelectedDevice is { } device)
            {
                // InitializeAsync cihaz nesnelerini yeniden kurar; secim ayni cihaza (kimlige gore) tasinir.
                SelectedDevice = Devices.FirstOrDefault(x => x.DeviceId == device.DeviceId) ?? device;
                await SelectDeviceAsync(SelectedDevice, SelectedPanelTab);
            }
        }
        catch (LoginRequiredException)
        {
            Error = "Kartı yeniden yüklemek için devices.manage izni olan bir oturum gerekiyor.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Kart yeniden yükleme kuyruğuna alınamadı. API bağlantısını kontrol edin.";
        }
    }

    private void RefreshCardCommands()
    {
        SearchCardsCommand.Refresh(); NextCardsPageCommand.Refresh(); PreviousCardsPageCommand.Refresh();
    }

    /// <summary>Zamanlayiciyi beklemeden kuyrugu hemen isler ve sonucu tazeler.</summary>
    public async Task PushNowAsync()
    {
        IsPushing = true;
        Error = null;
        StatusMessage = null;
        var before = TotalOutstanding;
        try
        {
            await api.PushNowAsync();
            await InitializeAsync();
            if (SelectedDevice is { } device) await SelectDeviceAsync(device);
            // Sunucu yalnizca BAGLI cihazlarin kuyrugunu isler (DeviceCardPushWorker); cihazlar
            // cevrimdisiyken istek "kabul edildi" doner ama hicbir sey degismez. Kullanici bunu
            // sessiz bir dugmeden anlayamaz; sonuc her durumda soylenir.
            StatusMessage = before == 0
                ? "Bekleyen kart yok; yüklenecek bir şey bulunmadı."
                : TotalOutstanding == 0
                    ? $"{before} kart yüklendi; tüm cihazlar güncel."
                    : TotalOutstanding < before
                        ? $"{before - TotalOutstanding} kart yüklendi, {TotalOutstanding} kart hâlâ bekliyor."
                        : $"{TotalOutstanding} kart yüklenemedi: cihazlar çevrimdışı ya da yanıt vermiyor. Bağlantı kurulunca yükleme otomatik denenecek.";
        }
        catch (LoginRequiredException)
        {
            Error = "Kart yüklemek için devices.manage izni olan bir oturum gerekiyor.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Kart yükleme başlatılamadı. API bağlantısını kontrol edin.";
        }
        finally
        {
            IsPushing = false;
        }
    }

    private void RaiseTotals()
    {
        Raise(nameof(TotalOutstanding));
        Raise(nameof(HasOutstanding));
        Raise(nameof(SummaryText));
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
