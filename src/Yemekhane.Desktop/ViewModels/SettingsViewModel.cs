using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Win32;
using Yemekhane.Application.Settings;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsApiClient api;
    private readonly IShellNavigationService navigation;
    private SettingsDocument? original;
    private bool isLoading, isOffline;
    private string? errorMessage, statusMessage, smsSecret, syncSecret, restorePath, restoreConfirmation;
    private string schoolName = "", schoolAddress = "", schoolContact = "", logoPath = "", smsEndpoint = "", smsAuthType = "None", smsUsername = "", smsSender = "";
    private int smsTimeoutSeconds = 30, backupRetentionCount = 14, syncIntervalMinutes = 5, logRetentionDays = 30;
    private bool backupEnabled, syncEnabled;
    private string backupFrequency = "Daily", backupTime = "02:00", backupPath = "", syncEndpoint = "", syncDeviceId = "", logLevel = "Information", logPath = "";
    private DayOfWeek backupWeeklyDay = DayOfWeek.Sunday;

    public SettingsViewModel(ISettingsApiClient api, IShellNavigationService navigation, IEnumerable<string> permissions)
    {
        this.api = api; this.navigation = navigation;
        var set = permissions.ToHashSet(StringComparer.Ordinal); CanRead = set.Contains("settings.read") || set.Contains("settings.manage"); CanManage = set.Contains("settings.manage");
        SaveCommand = new AsyncCommand(SaveAsync, () => CanManage && IsDirty && !IsLoading);
        CancelCommand = new RelayCommand(Cancel, () => IsDirty && !IsLoading);
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsLoading);
        BackupNowCommand = new AsyncCommand(BackupNowAsync, () => CanManage && !IsLoading);
        ChooseRestoreCommand = new RelayCommand(ChooseRestore, () => CanManage && !IsLoading);
        ValidateBackupCommand = new AsyncCommand(ValidateBackupAsync, () => CanManage && File.Exists(RestorePath) && !IsLoading);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => CanManage && File.Exists(RestorePath) && RestoreConfirmation == "GERI YUKLE" && !IsLoading);
        SyncNowCommand = new AsyncCommand(SyncNowAsync, () => CanManage && SyncEnabled && !IsLoading);
        RefreshConflictsCommand = new AsyncCommand(RefreshConflictsAsync, () => CanRead && !IsLoading);
        RequeueConflictCommand = new AsyncCommand(RequeueConflictAsync,
            () => CanManage && !IsLoading && SelectedConflict is not null);
        RefreshLogsCommand = new AsyncCommand(LoadLogsAsync, () => CanRead && !IsLoading);
        NavigateDevicesCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Devices), () => navigation.IsAvailable(ShellRoutes.Devices));
        NavigateMealsCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.Entitlements), () => navigation.IsAvailable(ShellRoutes.Entitlements));
        NavigateHolidaysCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.HolidayTransfer), () => navigation.IsAvailable(ShellRoutes.HolidayTransfer));
        NavigateUsersCommand = new RelayCommand(() => navigation.Navigate(ShellRoutes.UsersRoles), () => navigation.IsAvailable(ShellRoutes.UsersRoles));
        CanNavigateUsers = navigation.IsAvailable(ShellRoutes.UsersRoles) && set.Contains("users.manage");
    }

    public bool CanRead { get; } public bool CanManage { get; } public bool CanNavigateUsers { get; }
    public IReadOnlyList<string> SmsAuthTypes { get; } = ["None", "Basic", "Bearer", "ApiKey"];
    public IReadOnlyList<string> BackupFrequencies { get; } = ["Daily", "Weekly"];
    public IReadOnlyList<DayOfWeek> WeekDays { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<string> LogLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    public ObservableCollection<ApplicationLogItem> Logs { get; } = [];
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RefreshCommands(); } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public bool IsDirty => original is not null && !BuildRequest().Equals(ToRequest(original)) || !string.IsNullOrWhiteSpace(SmsSecret) || !string.IsNullOrWhiteSpace(SyncSecret);
    public string SchoolName { get => schoolName; set => Change(ref schoolName, value); } public string SchoolAddress { get => schoolAddress; set => Change(ref schoolAddress, value); }
    public string SchoolContact { get => schoolContact; set => Change(ref schoolContact, value); } public string LogoPath { get => logoPath; set => Change(ref logoPath, value); }
    public string SmsEndpoint { get => smsEndpoint; set => Change(ref smsEndpoint, value); } public string SmsAuthType { get => smsAuthType; set => Change(ref smsAuthType, value); }
    public string SmsUsername { get => smsUsername; set => Change(ref smsUsername, value); } public string SmsSender { get => smsSender; set => Change(ref smsSender, value); }
    public int SmsTimeoutSeconds { get => smsTimeoutSeconds; set => Change(ref smsTimeoutSeconds, value); } public bool SmsSecretConfigured => original?.Sms.SecretConfigured == true;
    public string? SmsSecret { get => smsSecret; set => Change(ref smsSecret, value); }
    public bool BackupEnabled { get => backupEnabled; set => Change(ref backupEnabled, value); } public string BackupFrequency { get => backupFrequency; set => Change(ref backupFrequency, value); }
    public DayOfWeek BackupWeeklyDay { get => backupWeeklyDay; set => Change(ref backupWeeklyDay, value); } public string BackupTime { get => backupTime; set => Change(ref backupTime, value); }
    public int BackupRetentionCount { get => backupRetentionCount; set => Change(ref backupRetentionCount, value); } public string BackupPath { get => backupPath; set => Change(ref backupPath, value); }
    public bool SyncEnabled { get => syncEnabled; set { Change(ref syncEnabled, value); RefreshCommands(); } } public string SyncEndpoint { get => syncEndpoint; set => Change(ref syncEndpoint, value); }
    public string SyncDeviceId { get => syncDeviceId; set => Change(ref syncDeviceId, value); } public int SyncIntervalMinutes { get => syncIntervalMinutes; set => Change(ref syncIntervalMinutes, value); }
    public bool SyncSecretConfigured => original?.Sync.SecretConfigured == true; public string? SyncSecret { get => syncSecret; set => Change(ref syncSecret, value); }
    public string SyncStatusText => original is null ? "-" : $"{original.Sync.Status.State} | Bekleyen: {original.Sync.Status.Pending} | Hatalı: {original.Sync.Status.Failed} | Çakışma: {original.Sync.Status.Conflicts}";

    /// <summary>Cakisan islemler operatorun karar vermesini bekler; listelenmezse sessizce olu kalirlar.</summary>
    public ObservableCollection<SyncConflictItem> Conflicts { get; } = [];
    public SyncConflictItem? SelectedConflict
    {
        get => selectedConflict;
        set { if (Set(ref selectedConflict, value)) RefreshCommands(); }
    }
    public bool HasConflicts => Conflicts.Count > 0;
    public ICommand RefreshConflictsCommand { get; }
    public ICommand RequeueConflictCommand { get; }
    public string LogLevel { get => logLevel; set => Change(ref logLevel, value); } public int LogRetentionDays { get => logRetentionDays; set => Change(ref logRetentionDays, value); } public string LogPath { get => logPath; set => Change(ref logPath, value); }
    public string? RestorePath { get => restorePath; set { if (Set(ref restorePath, value)) RefreshCommands(); } } public string? RestoreConfirmation { get => restoreConfirmation; set { if (Set(ref restoreConfirmation, value)) RefreshCommands(); } }
    public int DeviceCount => original?.Links.Devices ?? 0; public int MealTypeCount => original?.Links.ActiveMealTypes ?? 0;
    public IReadOnlyList<string> DeviceSummaries => original?.Links.DeviceSummaries ?? [];
    public IReadOnlyList<string> MealTypes => original?.Links.MealTypes ?? [];
    public ICommand SaveCommand { get; } public ICommand CancelCommand { get; } public ICommand RefreshCommand { get; }
    public ICommand BackupNowCommand { get; } public ICommand ChooseRestoreCommand { get; } public ICommand ValidateBackupCommand { get; } public ICommand RestoreCommand { get; }
    private SyncConflictItem? selectedConflict;
    public ICommand SyncNowCommand { get; } public ICommand RefreshLogsCommand { get; } public ICommand NavigateDevicesCommand { get; } public ICommand NavigateMealsCommand { get; } public ICommand NavigateHolidaysCommand { get; } public ICommand NavigateUsersCommand { get; }

    public Task InitializeAsync() => LoadAsync();
    public async Task LoadAsync() => await Run(async () => { Apply(await api.GetAsync()); await LoadLogsCoreAsync(); await LoadConflictsAsync(); StatusMessage = null; });
    public async Task SaveAsync() => await Run(async () => { var result = await api.SaveAsync(BuildRequest()); Apply(result.Settings); StatusMessage = result.RestartRequired ? "Kaydedildi. Servis ayarlarının uygulanması için API yeniden başlatılmalıdır." : "Ayarlar kaydedildi."; });
    public void Cancel() { if (original is not null) Apply(original); StatusMessage = "Değişiklikler geri alındı."; }
    public void SetSmsSecret(string value) => SmsSecret = value; public void SetSyncSecret(string value) => SyncSecret = value;
    private async Task BackupNowAsync() => await Run(async () => { var x = await api.BackupNowAsync(); StatusMessage = $"Yedek oluşturuldu: {x.FileName}"; });
    private async Task ValidateBackupAsync() => await Run(async () => { var x = await api.ValidateBackupAsync(RestorePath!); StatusMessage = $"Yedek doğrulandı: {x.CreatedAt:g} / {x.SchemaVersion}"; });
    private async Task RestoreAsync() => await Run(async () => { var x = await api.RestoreAsync(RestorePath!, RestoreConfirmation!); StatusMessage = x.RestartRequired ? "Geri yükleme tamamlandı. Uygulama yeniden başlatılmalıdır." : "Geri yükleme tamamlandı."; RestoreConfirmation = null; });
    private async Task SyncNowAsync() => await Run(async () => { var x = await api.RunSyncAsync(); StatusMessage = $"Sync tamamlandı: {x.Succeeded} başarılı, {x.RetryPending} bekliyor, {x.Conflicts} çakışma."; await LoadAsync(); await LoadConflictsAsync(); });

    private async Task RefreshConflictsAsync() => await Run(LoadConflictsAsync);

    private async Task RequeueConflictAsync()
    {
        if (SelectedConflict is not { } conflict) return;
        await Run(async () =>
        {
            await api.RequeueConflictAsync(conflict.OperationId);
            StatusMessage = "Çakışan işlem yeniden kuyruğa alındı; sonraki eşitlemede tekrar denenecek.";
            await LoadConflictsAsync();
            await LoadAsync();
        });
    }

    private async Task LoadConflictsAsync()
    {
        var items = await api.SyncConflictsAsync();
        Conflicts.Clear();
        foreach (var item in items) Conflicts.Add(item);
        SelectedConflict = null;
        Raise(nameof(HasConflicts));
    }
    private async Task LoadLogsAsync() => await Run(LoadLogsCoreAsync);
    private async Task LoadLogsCoreAsync() { var page = await api.LogsAsync(1, 100); Logs.Clear(); foreach (var x in page.Items) Logs.Add(x); }
    private void ChooseRestore() { var dialog = new OpenFileDialog { Filter = "YemekhanePro yedeği (*.zip)|*.zip", CheckFileExists = true }; if (dialog.ShowDialog() == true) RestorePath = dialog.FileName; }

    private async Task Run(Func<Task> action)
    {
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        try { await action(); }
        catch (LoginRequiredException) { ErrorMessage = "Bu işlem için oturum ve gerekli izin bulunamadı."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { IsOffline = true; ErrorMessage = "Ayarlar servisine ulaşılamadı."; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    private void Apply(SettingsDocument x)
    {
        original = x; schoolName = x.School.Name; schoolAddress = x.School.Address ?? ""; schoolContact = x.School.Contact ?? ""; logoPath = x.School.LogoPath ?? "";
        smsEndpoint = x.Sms.Endpoint ?? ""; smsAuthType = x.Sms.AuthType; smsUsername = x.Sms.Username ?? ""; smsSender = x.Sms.Sender ?? ""; smsTimeoutSeconds = x.Sms.TimeoutSeconds; smsSecret = null;
        backupEnabled = x.Backup.Enabled; backupFrequency = x.Backup.Frequency; backupWeeklyDay = x.Backup.WeeklyDay; backupTime = x.Backup.Time.ToString("HH:mm", CultureInfo.InvariantCulture); backupRetentionCount = x.Backup.RetentionCount; backupPath = x.Backup.Path ?? "";
        syncEnabled = x.Sync.Enabled; syncEndpoint = x.Sync.Endpoint ?? ""; syncDeviceId = x.Sync.DeviceId ?? ""; syncIntervalMinutes = x.Sync.IntervalMinutes; syncSecret = null;
        logLevel = x.Logs.Level; logRetentionDays = x.Logs.RetentionDays; logPath = x.Logs.Path ?? "";
        foreach (var name in GetType().GetProperties().Where(p => p.CanRead).Select(p => p.Name)) Raise(name); RefreshCommands();
    }
    private SaveSettingsRequest BuildRequest() => new(new(SchoolName, EmptyToNull(SchoolAddress), EmptyToNull(SchoolContact), EmptyToNull(LogoPath)), new(EmptyToNull(SmsEndpoint), SmsAuthType, EmptyToNull(SmsUsername), EmptyToNull(SmsSender), SmsTimeoutSeconds, EmptyToNull(SmsSecret)), new(BackupEnabled, BackupFrequency, BackupWeeklyDay, TimeOnly.TryParse(BackupTime, out var time) ? time : TimeOnly.MinValue, BackupRetentionCount, EmptyToNull(BackupPath)), new(EmptyToNull(SyncEndpoint), EmptyToNull(SyncDeviceId), SyncIntervalMinutes, SyncEnabled, EmptyToNull(SyncSecret)), new(LogLevel, LogRetentionDays, EmptyToNull(LogPath)));
    private static SaveSettingsRequest ToRequest(SettingsDocument x) => new(new(x.School.Name, x.School.Address, x.School.Contact, x.School.LogoPath), new(x.Sms.Endpoint, x.Sms.AuthType, x.Sms.Username, x.Sms.Sender, x.Sms.TimeoutSeconds, null), new(x.Backup.Enabled, x.Backup.Frequency, x.Backup.WeeklyDay, x.Backup.Time, x.Backup.RetentionCount, x.Backup.Path), new(x.Sync.Endpoint, x.Sync.DeviceId, x.Sync.IntervalMinutes, x.Sync.Enabled, null), new(x.Logs.Level, x.Logs.RetentionDays, x.Logs.Path));
    private void Change<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null) { if (Set(ref field, value, name)) { Raise(nameof(IsDirty)); RefreshCommands(); } }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void RefreshCommands() { foreach (var c in new[] { SaveCommand, CancelCommand, RefreshCommand, BackupNowCommand, ChooseRestoreCommand, ValidateBackupCommand, RestoreCommand, SyncNowCommand, RefreshConflictsCommand, RequeueConflictCommand, RefreshLogsCommand }) if (c is AsyncCommand a) a.Refresh(); else if (c is RelayCommand r) r.Refresh(); }
}
