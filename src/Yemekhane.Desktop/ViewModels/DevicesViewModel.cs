using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class DeviceCardViewModel : ObservableObject
{
    private DeviceItem item;
    private bool isBusy;
    private string? operationMessage;
    private DateTimeOffset? lastAttemptAt;
    private DateTimeOffset? nextRetryAt;
    public DeviceCardViewModel(DeviceItem value) => item = value;
    public DeviceItem Item { get => item; private set { Set(ref item, value); RaiseAll(); } }
    public Guid Id => Item.Id;
    public string Name => Item.Name;
    public string Model => Item.Model ?? Item.DeviceType;
    public string Endpoint => Item.Endpoint;
    public string Location => string.IsNullOrWhiteSpace(Item.Location) ? "Konum belirtilmedi" : Item.Location;
    public string Status => Item.Status;
    public string StatusText => Status switch { "Connected" => "Bağlı", "Connecting" => "Bağlanıyor", "Reconnecting" => "Yeniden bağlanıyor", "Error" => "Hata", _ => "Bağlı değil" };
    public bool IsConnected => Status == "Connected";
    public bool IsError => Status == "Error";
    public bool IsBusy { get => isBusy; set => Set(ref isBusy, value); }
    public string? OperationMessage { get => operationMessage; set => Set(ref operationMessage, value); }
    public DateTimeOffset? LastAttemptAt { get => lastAttemptAt; private set => Set(ref lastAttemptAt, value); }
    public DateTimeOffset? NextRetryAt { get => nextRetryAt; private set => Set(ref nextRetryAt, value); }
    public void Update(DeviceItem value) => Item = value;
    public void UpdateStatus(string status) => Update(Item with { Status = status });
    public void UpdateRuntime(string status, string? message, DateTimeOffset occurredAt,
        DateTimeOffset? lastAttempt, DateTimeOffset? nextRetry)
    {
        UpdateStatus(status);
        LastAttemptAt = lastAttempt;
        NextRetryAt = nextRetry;
        if (!string.IsNullOrWhiteSpace(message))
        {
            OperationMessage = FormatFailure(message, lastAttempt ?? occurredAt, nextRetry);
        }
    }

    public string FormatFailure(string message, DateTimeOffset lastAttempt, DateTimeOffset? nextRetry)
    {
        var detail = message.Contains("Exception occurred", StringComparison.OrdinalIgnoreCase)
            ? "Cihaz bağlantısı kurulamadı."
            : message;
        return $"{Name} ({Endpoint}): {detail} Son deneme: {lastAttempt:dd.MM.yyyy HH:mm:ss}. " +
               (nextRetry is null
                   ? "Sonraki deneme: otomatik bağlantı etkinse yeniden bağlantı planına göre."
                   : $"Sonraki deneme: {nextRetry:dd.MM.yyyy HH:mm:ss}.");
    }
    private void RaiseAll() { Raise(nameof(Name)); Raise(nameof(Model)); Raise(nameof(Endpoint)); Raise(nameof(Location)); Raise(nameof(Status)); Raise(nameof(StatusText)); Raise(nameof(IsConnected)); Raise(nameof(IsError)); }
}

public sealed class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceApiClient api;
    private readonly IDashboardRealtimeClient realtime;
    private readonly bool canManage;
    private bool isLoading;
    private bool isOffline;
    private bool isEditorOpen;
    private bool isLogsOpen;
    private string? errorMessage;
    private DeviceCardViewModel? editing;
    private string name = "";
    private string selectedType = "EthernetReader";
    private string ipAddress = "";
    private int port = 4370;
    private string comPort = "COM1";
    private int baudRate = 9600;
    private string location = "";
    private string direction = "Entry";
    private bool isActive = true;
    private bool autoConnect;
    private bool hasTurnstile;
    private bool simulatorAllowed;

    public DevicesViewModel(IDeviceApiClient api, IDashboardRealtimeClient realtime, IReadOnlySet<string> permissions)
    {
        this.api = api; this.realtime = realtime;
        canManage = permissions.Contains("devices.manage");
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsLoading);
        AddCommand = new RelayCommand(OpenCreate, () => canManage);
        SaveCommand = new AsyncCommand(SaveAsync, () => canManage && !IsLoading);
        CloseEditorCommand = new RelayCommand(() => IsEditorOpen = false);
        CloseLogsCommand = new RelayCommand(() => IsLogsOpen = false);
        ConnectCommand = CardAction("connect"); DisconnectCommand = CardAction("disconnect");
        TestCommand = CardAction("test"); ReconnectCommand = CardAction("reconnect");
        EditCommand = new RelayCommand<DeviceCardViewModel>(OpenEdit, _ => canManage);
        DeactivateCommand = new RelayCommand<DeviceCardViewModel>(card => _ = DeactivateAsync(card), _ => canManage);
        LogsCommand = new RelayCommand<DeviceCardViewModel>(card => _ = OpenLogsAsync(card));
        realtime.DeviceStatusChanged += OnDeviceStatusChanged;
        realtime.StateChanged += OnRealtimeStateChanged;
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = [];
    public ObservableCollection<DeviceLogItem> Logs { get; } = [];
    public IReadOnlyList<string> DeviceTypes => SimulatorAllowed ? ["SF300", "ComReader", "EthernetReader", "Simulator"] : ["SF300", "ComReader", "EthernetReader"];
    public IReadOnlyList<string> Directions { get; } = ["Entry", "Exit", "Bidirectional"];
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) { Raise(nameof(ShowEmpty)); Raise(nameof(ShowContent)); } } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public bool IsEditorOpen { get => isEditorOpen; private set => Set(ref isEditorOpen, value); }
    public bool IsLogsOpen { get => isLogsOpen; private set => Set(ref isLogsOpen, value); }
    public bool SimulatorAllowed { get => simulatorAllowed; private set { if (Set(ref simulatorAllowed, value)) Raise(nameof(DeviceTypes)); } }
    public bool CanManage => canManage;
    public bool ShowEmpty => !IsLoading && Devices.Count == 0 && !HasError;
    public bool ShowContent => !IsLoading && !HasError;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) { Raise(nameof(HasError)); Raise(nameof(ShowEmpty)); Raise(nameof(ShowContent)); } } }
    public string EditorTitle => editing is null ? "Yeni cihaz" : "Cihaz ayarları";
    public string Name { get => name; set => Set(ref name, value); }
    public string SelectedType { get => selectedType; set { if (Set(ref selectedType, value)) { Raise(nameof(IsEthernet)); Raise(nameof(IsCom)); Raise(nameof(IsSimulator)); } } }
    public bool IsEthernet => SelectedType is "SF300" or "EthernetReader";
    public bool IsCom => SelectedType == "ComReader";
    public bool IsSimulator => SelectedType == "Simulator";
    public string IpAddress { get => ipAddress; set => Set(ref ipAddress, value); }
    public int Port { get => port; set => Set(ref port, value); }
    public string ComPort { get => comPort; set => Set(ref comPort, value); }
    public int BaudRate { get => baudRate; set => Set(ref baudRate, value); }
    public string Location { get => location; set => Set(ref location, value); }
    public string Direction { get => direction; set => Set(ref direction, value); }
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }
    public bool AutoConnect { get => autoConnect; set => Set(ref autoConnect, value); }
    public bool HasTurnstile { get => hasTurnstile; set => Set(ref hasTurnstile, value); }
    public ICommand RefreshCommand { get; } public ICommand AddCommand { get; } public ICommand SaveCommand { get; }
    public ICommand CloseEditorCommand { get; } public ICommand CloseLogsCommand { get; }
    public ICommand ConnectCommand { get; } public ICommand DisconnectCommand { get; } public ICommand TestCommand { get; }
    public ICommand ReconnectCommand { get; } public ICommand EditCommand { get; } public ICommand DeactivateCommand { get; } public ICommand LogsCommand { get; }

    public async Task InitializeAsync() => await LoadAsync();
    public async Task LoadAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var values = await api.ListAsync(); SimulatorAllowed = (await api.CapabilitiesAsync()).SimulatorAllowed;
            Devices.Clear(); foreach (var value in values) Devices.Add(new DeviceCardViewModel(value));
            Raise(nameof(ShowEmpty));
        }
        catch (LoginRequiredException) { ErrorMessage = "Cihazları görüntüleme yetkisi veya geçerli oturum bulunamadı."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { ErrorMessage = "Cihaz servisine ulaşılamadı."; IsOffline = true; }
        finally { IsLoading = false; }
    }

    private RelayCommand<DeviceCardViewModel> CardAction(string action) => new(card => _ = ExecuteAsync(card, action), card => canManage && !card.IsBusy && card.Item.IsActive);
    private async Task ExecuteAsync(DeviceCardViewModel card, string action)
    {
        card.IsBusy = true; card.OperationMessage = null;
        try
        {
            var result = await api.ActionAsync(card.Id, action);
            if (result.Device is not null) card.Update(result.Device);
            card.OperationMessage = result.Succeeded
                ? result.Message
                : card.FormatFailure(result.Message, result.Device?.LastStatusAt ?? DateTimeOffset.Now, null);
        }
        catch (Exception ex)
        {
            card.OperationMessage = ex is LoginRequiredException
                ? "Bu işlem için devices.manage yetkisi gerekir."
                : card.FormatFailure("Cihaz işlemi tamamlanamadı.", DateTimeOffset.Now, null);
        }
        finally { card.IsBusy = false; }
    }
    private void OpenCreate() { editing = null; Name = ""; SelectedType = "EthernetReader"; IpAddress = ""; Port = 4370; ComPort = "COM1"; BaudRate = 9600; Location = ""; Direction = "Entry"; IsActive = true; AutoConnect = false; HasTurnstile = false; ErrorMessage = null; Raise(nameof(EditorTitle)); IsEditorOpen = true; }
    private void OpenEdit(DeviceCardViewModel card) { editing = card; var x = card.Item; Name = x.Name; SelectedType = x.DeviceType; IpAddress = x.IpAddress ?? ""; Port = x.Port ?? 4370; ComPort = x.ComPort ?? "COM1"; BaudRate = x.BaudRate ?? 9600; Location = x.Location ?? ""; Direction = x.Direction; IsActive = x.IsActive; AutoConnect = x.AutoConnect; HasTurnstile = x.HasTurnstile; ErrorMessage = null; Raise(nameof(EditorTitle)); IsEditorOpen = true; }
    private async Task SaveAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try { var model = new DeviceWriteModel(Name, SelectedType, IsCom ? "COM" : IsSimulator ? "Simulator" : "Ethernet", IsEthernet ? IpAddress : null, IsEthernet ? Port : null, IsCom ? ComPort : null, IsCom ? BaudRate : null, IsActive, AutoConnect, HasTurnstile, Location, Direction); var value = editing is null ? await api.CreateAsync(model) : await api.UpdateAsync(editing.Id, model); if (editing is null) Devices.Add(new DeviceCardViewModel(value)); else editing.Update(value); IsEditorOpen = false; Raise(nameof(ShowEmpty)); }
        catch (HttpRequestException ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }
    private async Task DeactivateAsync(DeviceCardViewModel card) { try { card.Update(await api.DeactivateAsync(card.Id)); } catch { card.OperationMessage = "Cihaz pasifleştirilemedi."; } }
    private async Task OpenLogsAsync(DeviceCardViewModel card) { try { Logs.Clear(); foreach (var log in await api.LogsAsync(card.Id)) Logs.Add(log); IsLogsOpen = true; } catch { card.OperationMessage = "Loglar alınamadı."; } }
    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEvent value) => RunOnUi(() =>
    {
        var card = Devices.FirstOrDefault(x => x.Id == value.DeviceId);
        if (card is null) return;
        var status = value.Status == "Faulted" ? "Error" : value.Status;
        if (status is "Error" or "Reconnecting" && !string.IsNullOrWhiteSpace(value.Message))
            card.UpdateRuntime(status, value.Message, value.OccurredAt, value.LastAttemptAt, value.NextRetryAt);
        else
            card.UpdateStatus(status);
    });
    private void OnRealtimeStateChanged(object? sender, RealtimeConnectionState state) => RunOnUi(() => IsOffline = state != RealtimeConnectionState.Connected);
    private static void RunOnUi(Action action) { var dispatcher = System.Windows.Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action); }
    public void Dispose() { realtime.DeviceStatusChanged -= OnDeviceStatusChanged; realtime.StateChanged -= OnRealtimeStateChanged; }
}
