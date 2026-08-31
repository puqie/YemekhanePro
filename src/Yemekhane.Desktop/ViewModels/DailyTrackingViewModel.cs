using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed record TrackingFilterOption(Guid Id, string Name);

public sealed class DailyTrackingViewModel : ObservableObject
{
    public const int MaximumRows = 500;
    private readonly IDailyTrackingApiClient apiClient;
    private readonly IDashboardRealtimeClient realtimeClient;
    private readonly IDailyTrackingPreferences preferences;
    private readonly ITrackingSoundPlayer soundPlayer;
    private readonly HashSet<Guid> operationIds = [];
    private bool isLoading;
    private bool isLive = true;
    private bool isInitialized;
    private bool loginRequired;
    private string? errorMessage;
    private string? search;
    private string? selectedDecision;
    private Guid? selectedMealTypeId;
    private Guid? selectedDeviceId;
    private Guid? selectedClassId;
    private bool soundEnabled;
    private RealtimeConnectionState realtimeState;
    private DailyTrackingSummary summary = new(0, 0, 0);
    private DateTimeOffset? nextCursorTimestamp;
    private Guid? nextCursorOperationId;
    private bool hasMore;
    private int recoveryRunning;

    public DailyTrackingViewModel(IDailyTrackingApiClient apiClient, IDashboardRealtimeClient realtimeClient,
        IDailyTrackingPreferences preferences, ITrackingSoundPlayer soundPlayer)
    {
        this.apiClient = apiClient;
        this.realtimeClient = realtimeClient;
        this.preferences = preferences;
        this.soundPlayer = soundPlayer;
        soundEnabled = preferences.SoundEnabled;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        ApplyFiltersCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => !IsLoading && HasMore);
        ToggleLiveCommand = new AsyncCommand(ToggleLiveAsync);
        OpenStudentCommand = new ParameterCommand<DailyTrackingRow>(row =>
        {
            if (row.StudentId.HasValue) StudentDetailNavigationRequested?.Invoke(this,
                $"{ShellRoutes.StudentDetail}/{row.StudentId.Value:D}");
        }, row => row?.StudentId.HasValue == true);
        realtimeClient.AccessReceived += OnAccessReceived;
        realtimeClient.StateChanged += OnRealtimeStateChanged;
    }

    public ObservableCollection<DailyTrackingRow> Rows { get; } = [];
    public ObservableCollection<TrackingFilterOption> MealTypes { get; } = [];
    public ObservableCollection<TrackingFilterOption> Devices { get; } = [];
    public ObservableCollection<TrackingFilterOption> Classes { get; } = [];
    public IReadOnlyList<string> Decisions { get; } = ["ALLOW", "DENY"];
    public event EventHandler<string>? StudentDetailNavigationRequested;
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RaiseState(); } }
    public bool IsLive { get => isLive; private set { if (Set(ref isLive, value)) { Raise(nameof(LiveText)); Raise(nameof(IsPaused)); } } }
    public bool IsPaused => !IsLive;
    public string LiveText => IsLive ? "Canlı" : "Duraklatıldı";
    public bool LoginRequired { get => loginRequired; private set { if (Set(ref loginRequired, value)) RaiseState(); } }
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) RaiseState(); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !LoginRequired && !HasError && Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;
    public bool HasMore => hasMore;
    public DailyTrackingSummary Summary { get => summary; private set => Set(ref summary, value); }
    public string? Search { get => search; set => Set(ref search, value); }
    public string? SelectedDecision { get => selectedDecision; set => Set(ref selectedDecision, value); }
    public Guid? SelectedMealTypeId { get => selectedMealTypeId; set => Set(ref selectedMealTypeId, value); }
    public Guid? SelectedDeviceId { get => selectedDeviceId; set => Set(ref selectedDeviceId, value); }
    public Guid? SelectedClassId { get => selectedClassId; set => Set(ref selectedClassId, value); }
    public bool SoundEnabled { get => soundEnabled; set { if (Set(ref soundEnabled, value)) preferences.SoundEnabled = value; } }
    public RealtimeConnectionState RealtimeState { get => realtimeState; private set { if (Set(ref realtimeState, value)) { Raise(nameof(IsOffline)); Raise(nameof(ConnectionText)); } } }
    public bool IsOffline => RealtimeState != RealtimeConnectionState.Connected;
    public string ConnectionText => RealtimeState switch
    {
        RealtimeConnectionState.Connected => "Bağlı",
        RealtimeConnectionState.Connecting => "Bağlanıyor",
        RealtimeConnectionState.Reconnecting => "Yeniden bağlanıyor",
        _ => "Çevrimdışı"
    };
    public ICommand RefreshCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand ToggleLiveCommand { get; }
    public ICommand OpenStudentCommand { get; }

    public async Task InitializeAsync()
    {
        if (isInitialized) return;
        isInitialized = true;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        LoginRequired = false;
        try
        {
            var page = await apiClient.GetAsync(CreateQuery());
            ReplaceRows(page);
        }
        catch (LoginRequiredException) { LoginRequired = true; ClearRows(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            ErrorMessage = ex is TaskCanceledException ? "Günlük takip isteği zaman aşımına uğradı." : "Günlük takip verisi alınamadı.";
        }
        finally { IsLoading = false; RaiseState(); }
    }

    public async Task LoadMoreAsync()
    {
        if (!HasMore || !nextCursorTimestamp.HasValue || !nextCursorOperationId.HasValue) return;
        IsLoading = true;
        try
        {
            var page = await apiClient.GetAsync(CreateQuery() with
            {
                CursorTimestamp = nextCursorTimestamp,
                CursorOperationId = nextCursorOperationId
            });
            MergeRows(page.Items, false, false);
            SetPaging(page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            ErrorMessage = "Diğer kayıtlar alınamadı.";
        }
        finally { IsLoading = false; RaiseState(); }
    }

    private async Task ToggleLiveAsync()
    {
        IsLive = !IsLive;
        if (IsLive) await RecoverGapAsync();
    }

    private void OnAccessReceived(object? sender, AccessDecisionCommittedEvent value)
    {
        if (!IsLive || operationIds.Contains(value.OperationId)) return;
        _ = RecoverGapAsync();
    }

    private void OnRealtimeStateChanged(object? sender, RealtimeConnectionState state)
    {
        RunOnUi(() => RealtimeState = state);
        if (state == RealtimeConnectionState.Connected && IsLive && isInitialized) _ = RecoverGapAsync();
    }

    private async Task RecoverGapAsync()
    {
        if (Interlocked.Exchange(ref recoveryRunning, 1) != 0) return;
        try
        {
            var newest = RunOnUi(() => Rows.FirstOrDefault());
            var query = CreateQuery();
            if (newest is not null) query = query with { SinceTimestamp = newest.Timestamp, SinceOperationId = newest.OperationId };
            var page = await apiClient.GetAsync(query);
            RunOnUi(() =>
            {
                var added = MergeRows(page.Items, true, SoundEnabled);
                Summary = page.Summary;
                if (added > 0) UpdateOptions();
                ErrorMessage = null;
            });
        }
        catch (LoginRequiredException) { RunOnUi(() => LoginRequired = true); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            RunOnUi(() => ErrorMessage = "Canlı kayıtlar eşitlenemedi; bağlantı yeniden kurulduğunda tekrar denenecek.");
        }
        finally { Interlocked.Exchange(ref recoveryRunning, 0); }
    }

    private DailyTrackingQuery CreateQuery() => new(100, SelectedDecision, SelectedMealTypeId,
        SelectedDeviceId, SelectedClassId, Search);

    private void ReplaceRows(DailyTrackingPage page)
    {
        ClearRows();
        MergeRows(page.Items, false, false);
        Summary = page.Summary;
        SetPaging(page);
        UpdateOptions();
    }

    private int MergeRows(IEnumerable<DailyTrackingRow> incoming, bool newest, bool playSound)
    {
        var added = 0;
        foreach (var row in incoming.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.OperationId))
        {
            if (!operationIds.Add(row.OperationId)) continue;
            var index = 0;
            while (index < Rows.Count && (Rows[index].Timestamp > row.Timestamp
                || (Rows[index].Timestamp == row.Timestamp && Rows[index].OperationId.CompareTo(row.OperationId) > 0))) index++;
            Rows.Insert(index, row);
            added++;
            if (playSound && newest) _ = soundPlayer.PlayAsync(row.Decision).AsTask();
        }
        while (Rows.Count > MaximumRows)
        {
            operationIds.Remove(Rows[^1].OperationId);
            Rows.RemoveAt(Rows.Count - 1);
        }
        RaiseState();
        return added;
    }

    private void SetPaging(DailyTrackingPage page)
    {
        nextCursorTimestamp = page.NextCursorTimestamp;
        nextCursorOperationId = page.NextCursorOperationId;
        hasMore = page.HasMore;
        Raise(nameof(HasMore));
        ((AsyncCommand)LoadMoreCommand).Refresh();
    }

    private void UpdateOptions()
    {
        ReplaceOptions(MealTypes, Rows.Where(x => x.MealTypeId.HasValue && x.MealType is not null).Select(x => new TrackingFilterOption(x.MealTypeId!.Value, x.MealType!)));
        ReplaceOptions(Devices, Rows.Select(x => new TrackingFilterOption(x.DeviceId, x.DeviceName)));
        ReplaceOptions(Classes, Rows.Where(x => x.ClassId.HasValue && x.ClassName is not null).Select(x => new TrackingFilterOption(x.ClassId!.Value, x.ClassName!)));
    }

    private static void ReplaceOptions(ObservableCollection<TrackingFilterOption> target, IEnumerable<TrackingFilterOption> values)
    {
        target.Clear();
        foreach (var value in values.DistinctBy(x => x.Id).OrderBy(x => x.Name)) target.Add(value);
    }

    private void ClearRows() { Rows.Clear(); operationIds.Clear(); RaiseState(); }
    private void RaiseState() { Raise(nameof(IsEmpty)); Raise(nameof(HasRows)); Raise(nameof(HasError)); }
    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() || !dispatcher.Thread.IsAlive) action(); else dispatcher.Invoke(action);
    }
    private static T RunOnUi<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess() || !dispatcher.Thread.IsAlive ? action() : dispatcher.Invoke(action);
    }
}

public sealed class ParameterCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand where T : class
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);
    public void Execute(object? parameter) { if (parameter is T value && CanExecute(value)) execute(value); }
}
