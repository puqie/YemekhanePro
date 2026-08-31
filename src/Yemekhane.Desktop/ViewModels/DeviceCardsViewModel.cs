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
    public int AttemptCount => value.AttemptCount;
    public string ActionText => value.IsRemoval ? "Siliniyor" : "Yükleniyor";
    public bool HasRetried => value.AttemptCount > 0;
    public string AttemptText => value.AttemptCount == 0 ? "İlk deneme" : $"{value.AttemptCount} başarısız deneme";
}

/// <summary>
/// Kart-cihaz yukleme durumu ekrani.
///
/// Operatorun gormesi gereken sey toplam sayilar degil, hangi cihazda ne eksik oldugudur:
/// bir turnikede eksik kalan tek bir kart, o ogrencinin o kapidan gecememesi demektir.
/// </summary>
public sealed class DeviceCardsViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceCardsApiClient api;
    private bool isLoading;
    private bool isPushing;
    private string? error;
    private DeviceCardSummaryViewModel? selectedDevice;

    public DeviceCardsViewModel(IDeviceCardsApiClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncCommand(InitializeAsync);
        PushNowCommand = new AsyncCommand(PushNowAsync, () => !IsPushing);
        SelectDeviceCommand = new AsyncCommand<DeviceCardSummaryViewModel>(SelectDeviceAsync);
    }

    public ObservableCollection<DeviceCardSummaryViewModel> Devices { get; } = [];
    public ObservableCollection<PendingCardViewModel> PendingCards { get; } = [];

    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public bool IsPushing { get => isPushing; private set { if (Set(ref isPushing, value)) PushNowCommand.Refresh(); } }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error is not null;

    public DeviceCardSummaryViewModel? SelectedDevice
    {
        get => selectedDevice;
        private set { if (Set(ref selectedDevice, value)) Raise(nameof(HasSelection)); }
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

    public async Task SelectDeviceAsync(DeviceCardSummaryViewModel device)
    {
        ArgumentNullException.ThrowIfNull(device);
        SelectedDevice = device;
        Error = null;
        try
        {
            var pending = await api.GetPendingAsync(device.DeviceId, 100);
            PendingCards.Clear();
            foreach (var card in pending) PendingCards.Add(new PendingCardViewModel(card));
        }
        catch (LoginRequiredException)
        {
            Error = "Kart kuyruğunu görüntülemek için devices.read izni olan bir oturum gerekiyor.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Error = "Bekleyen kart listesi alınamadı. API bağlantısını kontrol edin.";
        }
    }

    /// <summary>Zamanlayiciyi beklemeden kuyrugu hemen isler ve sonucu tazeler.</summary>
    public async Task PushNowAsync()
    {
        IsPushing = true;
        Error = null;
        try
        {
            await api.PushNowAsync();
            await InitializeAsync();
            if (SelectedDevice is { } device) await SelectDeviceAsync(device);
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
