using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Net.Http;
using System.IO;
using Yemekhane.Application.Dashboard;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class QuickActionViewModel
{
    public required string Label { get; init; }
    public required string Route { get; init; }
    public required string UnavailableReason { get; init; }
    public required ICommand Command { get; init; }
    public bool IsAvailable { get; init; }
}

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IDashboardApiClient apiClient;
    private readonly IDashboardRealtimeClient realtimeClient;
    private readonly IJwtSession session;
    private DashboardSnapshot? snapshot;
    private bool isLoading;
    private bool loginRequired;
    private string? errorMessage;
    private RealtimeConnectionState realtimeState;
    private bool localApiAvailable;
    private bool cloudUnavailable;

    public DashboardViewModel(IDashboardApiClient apiClient, IDashboardRealtimeClient realtimeClient,
        IShellNavigationService navigation, IJwtSession session)
    {
        this.apiClient = apiClient;
        this.realtimeClient = realtimeClient;
        this.session = session;
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsLoading && session.IsAuthenticated);
        QuickActions = CreateQuickActions(navigation);
        NavigateDashboardCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Dashboard));
        NavigateDailyTrackingCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.DailyTracking),
            () => navigation.IsAvailable(ShellRoutes.DailyTracking));
        NavigateStudentsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Students),
            () => navigation.IsAvailable(ShellRoutes.Students));
        NavigateEntitlementsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Entitlements),
            () => navigation.IsAvailable(ShellRoutes.Entitlements));
        NavigateCalendarCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.HolidayTransfer),
            () => navigation.IsAvailable(ShellRoutes.HolidayTransfer));
        NavigateDevicesCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Devices),
            () => navigation.IsAvailable(ShellRoutes.Devices));
        NavigateDeviceCardsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.DeviceCards),
            () => navigation.IsAvailable(ShellRoutes.DeviceCards));
        NavigateSmsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Sms),
            () => navigation.IsAvailable(ShellRoutes.Sms));
        NavigateCashCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Cash),
            () => navigation.IsAvailable(ShellRoutes.Cash));
        NavigateReportsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Reports),
            () => navigation.IsAvailable(ShellRoutes.Reports));
        NavigateSettingsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Settings),
            () => navigation.IsAvailable(ShellRoutes.Settings));
        CanNavigateEntitlements = navigation.IsAvailable(ShellRoutes.Entitlements);
        CanNavigateCalendar = navigation.IsAvailable(ShellRoutes.HolidayTransfer);
        CanNavigateSms = navigation.IsAvailable(ShellRoutes.Sms);
        CanNavigateCash = navigation.IsAvailable(ShellRoutes.Cash);
        CanNavigateReports = navigation.IsAvailable(ShellRoutes.Reports);
        CanNavigateSettings = navigation.IsAvailable(ShellRoutes.Settings);
        realtimeClient.AccessReceived += OnAccessReceived;
        realtimeClient.DeviceStatusChanged += OnDeviceStatusChanged;
        realtimeClient.StateChanged += (_, state) => RunOnUi(() => RealtimeState = state);
    }

    public DashboardSnapshot? Snapshot { get => snapshot; private set { if (Set(ref snapshot, value)) { Raise(nameof(HasData)); Raise(nameof(IsEmpty)); } } }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) Raise(nameof(ShowContent)); } }
    public bool LoginRequired { get => loginRequired; private set { if (Set(ref loginRequired, value)) Raise(nameof(ShowContent)); } }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) { Raise(nameof(HasError)); Raise(nameof(ShowContent)); } } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasData => Snapshot is not null;
    public bool IsEmpty => Snapshot is not null && Snapshot.RecentAccess.Count == 0;
    public bool ShowContent => !IsLoading && !LoginRequired && !HasError;
    public RealtimeConnectionState RealtimeState { get => realtimeState; private set { if (Set(ref realtimeState, value)) { Raise(nameof(IsOffline)); Raise(nameof(ConnectionText)); } } }
    public bool IsOffline => !LocalApiAvailable || CloudUnavailable || RealtimeState != RealtimeConnectionState.Connected;
    public bool LocalApiAvailable { get => localApiAvailable; private set { if (Set(ref localApiAvailable, value)) { Raise(nameof(IsOffline)); Raise(nameof(ConnectionText)); } } }
    public bool CloudUnavailable { get => cloudUnavailable; private set { if (Set(ref cloudUnavailable, value)) { Raise(nameof(IsOffline)); Raise(nameof(ConnectionText)); } } }
    public string ConnectionText => !LocalApiAvailable ? "Yerel API çevrimdışı"
        : CloudUnavailable ? "Bulut çevrimdışı · yerel çalışma"
        : RealtimeState switch
    {
        RealtimeConnectionState.Connected => "Canlı",
        RealtimeConnectionState.Connecting => "Bağlanıyor",
        RealtimeConnectionState.Reconnecting => "Yeniden bağlanıyor",
        _ => "Çevrimdışı"
    };
    public ICommand RefreshCommand { get; }
    public IReadOnlyList<QuickActionViewModel> QuickActions { get; }
    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateDailyTrackingCommand { get; }
    public ICommand NavigateStudentsCommand { get; }
    public ICommand NavigateEntitlementsCommand { get; }
    public ICommand NavigateCalendarCommand { get; }
    public ICommand NavigateDevicesCommand { get; }
    public ICommand NavigateDeviceCardsCommand { get; }
    public ICommand NavigateSmsCommand { get; }
    public ICommand NavigateCashCommand { get; }
    public ICommand NavigateReportsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public bool CanNavigateEntitlements { get; }
    public bool CanNavigateCalendar { get; }
    public bool CanNavigateSms { get; }
    public bool CanNavigateCash { get; }
    public bool CanNavigateReports { get; }
    public bool CanNavigateSettings { get; }

    public async Task InitializeAsync()
    {
        await LoadAsync();
        if (session.IsAuthenticated) await realtimeClient.ConnectAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        LoginRequired = false;
        try
        {
            Snapshot = await apiClient.GetAsync();
            LocalApiAvailable = true;
            var connectivity = await apiClient.GetConnectivityAsync();
            CloudUnavailable = string.Equals(connectivity.Cloud, "Offline", StringComparison.OrdinalIgnoreCase);
        }
        catch (LoginRequiredException) { Snapshot = null; LoginRequired = true; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            LocalApiAvailable = false;
            ErrorMessage = ex is TaskCanceledException ? "Dashboard isteği zaman aşımına uğradı." : "Dashboard verisi alınamadı. API bağlantısını kontrol edin.";
        }
        finally { IsLoading = false; }
    }

    private void OnAccessReceived(object? sender, AccessDecisionCommittedEvent value) => RunOnUi(() =>
    {
        if (Snapshot is null) return;
        var accesses = Snapshot.RecentAccess.ToList();
        var deviceName = Snapshot.Devices.FirstOrDefault(x => x.Id == value.DeviceId)?.Name ?? "Bilinmeyen cihaz";
        accesses.Insert(0, new DashboardAccessRow(value.OperationId, value.OccurredAt,
            value.StudentName ?? "Tanımsız kart", null, string.Empty, deviceName, null, value.Decision, value.Reason));
        if (accesses.Count > 20) accesses.RemoveAt(accesses.Count - 1);
        var kpis = Snapshot.Kpis;
        if (value.Decision == "DENY") kpis = kpis with { Denied = kpis.Denied + 1 };
        else if (value.Decision == "ALLOW") kpis = kpis with { Used = kpis.Used + 1, Remaining = Math.Max(0, kpis.Remaining - 1) };
        Snapshot = Snapshot with { RecentAccess = accesses, Kpis = kpis, GeneratedAt = value.OccurredAt };
    });

    private void OnDeviceStatusChanged(object? sender, DeviceStatusChangedEvent value) => RunOnUi(() =>
    {
        if (Snapshot is null) return;
        var devices = Snapshot.Devices.Select(x => x.Id == value.DeviceId
            ? x with { Status = value.Status, LastConnectedAt = value.CheckedAt ?? value.OccurredAt }
            : x).ToList();
        var summary = new DashboardDeviceSummary(devices.Count,
            devices.Count(x => IsStatus(x.Status, "Online", "Connected")),
            devices.Count(x => IsStatus(x.Status, "Offline", "Disconnected")),
            devices.Count(x => IsStatus(x.Status, "Error")));
        Snapshot = Snapshot with { Devices = devices, DeviceSummary = summary, GeneratedAt = value.OccurredAt };
    });

    private static QuickActionViewModel[] CreateQuickActions(IShellNavigationService navigation)
    {
        var definitions = new[]
        {
            ("+ Öğrenci", ShellRoutes.StudentsCreate), ("Kart Tanımla", ShellRoutes.Cards),
            ("Hakediş Ver", ShellRoutes.Entitlements), ("Tatil / Aktarım", ShellRoutes.HolidayTransfer),
            ("Kart Oku", ShellRoutes.CardReader), ("Kasa", ShellRoutes.Cash), ("Rapor", ShellRoutes.Reports)
        };
        return definitions.Select(item =>
        {
            var available = navigation.IsAvailable(item.Item2);
            return new QuickActionViewModel
            {
                Label = item.Item1, Route = item.Item2, IsAvailable = available,
                UnavailableReason = available ? string.Empty : "Bu işlem için oturum yetkisi bulunmuyor.",
                Command = new RelayCommand(() => navigation.Navigate(item.Item2), () => available)
            };
        }).ToArray();
    }

    private static bool IsStatus(string value, params string[] expected) =>
        expected.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action);
    }
}
