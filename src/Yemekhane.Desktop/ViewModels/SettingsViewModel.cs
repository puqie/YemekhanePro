using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Windows.Input;
using Yemekhane.Application.Settings;
using Yemekhane.Desktop.Converters;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Log seviyesi secenegi: ekranda <paramref name="Name"/> (Turkce),
/// yapilandirmaya <paramref name="Value"/> (Serilog'un tanidigi İngilizce ad) yazilir.
/// </summary>
public sealed record LogLevelOption(string Name, string Value);

/// <summary>Yedekleme siklik/gun secenekleri icin ad-deger cifti.</summary>
/// <remarks>
/// Once ComboBox'lar ham degeri gosteriyordu: kullanici Turkce arayuzde
/// "Daily" ve "Sunday" goruyordu. Ad ekranda, Value ise API'ye gider --
/// sunucu sozlesmesi degismez.
/// </remarks>
public sealed record BackupFrequencyOption(string Name, string Value);
public sealed record WeekDayOption(string Name, DayOfWeek Value);

/// <summary>SMS saglayici kimlik dogrulama secenegi; Value SettingsValidation.AuthTypes ile aynidir.</summary>
public sealed record SmsAuthTypeOption(string Name, string Value);

public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>Geri yukleme onay metni. ASCII ve KASITLI: Turkce klavye/duzen farki onayi engellemesin.</summary>
    public const string RestoreConfirmationPhrase = "GERI YUKLE";

    private readonly ISettingsApiClient api;
    private readonly IShellNavigationService navigation;
    private readonly IFileDialogService files;
    private SettingsDocument? original;
    private bool isLoading, isOffline;
    private string? errorMessage, statusMessage, smsSecret, syncSecret, restorePath, restoreConfirmation, lastBackupFile;
    private string schoolName = "", schoolAddress = "", schoolContact = "", logoPath = "", smsEndpoint = "", smsAuthType = "None", smsUsername = "", smsSender = "";
    // Sayisal alanlar METIN olarak tutulur. Once int'e baglaniyordu: kullanici "abc" ya da
    // "-5" yazinca WPF baglamasi sessizce reddediyor, kutu kirmizi cerceve aliyor ama HICBIR
    // mesaj cikmiyor ve Kaydet eski degeri gonderiyordu. Metin tutulunca dogrulama bizde:
    // hata Turkce ve alan adiyla soylenir, hatali degerle kayit yapilmaz.
    private string smsTimeoutText = "30", backupRetentionText = "14", syncIntervalText = "5", logRetentionText = "30";
    private bool backupEnabled, syncEnabled;
    private string backupFrequency = "Daily", backupTime = "02:00", backupPath = "", syncEndpoint = "", syncDeviceId = "", logLevel = "Information", logPath = "";
    private DayOfWeek backupWeeklyDay = DayOfWeek.Sunday;

    public SettingsViewModel(ISettingsApiClient api, IShellNavigationService navigation, IEnumerable<string> permissions,
        IFileDialogService? files = null)
    {
        this.api = api; this.navigation = navigation;
        // Diyalog dikisi: yedek dosyasi secimi testte diyalog acmadan surulebilsin.
        this.files = files ?? new FileDialogService();
        var set = permissions.ToHashSet(StringComparer.Ordinal); CanRead = set.Contains("settings.read") || set.Contains("settings.manage"); CanManage = set.Contains("settings.manage");
        SaveCommand = new AsyncCommand(SaveAsync, () => CanManage && IsDirty && !IsLoading);
        CancelCommand = new RelayCommand(Cancel, () => IsDirty && !IsLoading);
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsLoading);
        BackupNowCommand = new AsyncCommand(BackupNowAsync, () => CanManage && !IsLoading);
        ChooseRestoreCommand = new RelayCommand(ChooseRestore, () => CanManage && !IsLoading);
        ValidateBackupCommand = new AsyncCommand(ValidateBackupAsync, () => CanManage && File.Exists(RestorePath) && !IsLoading);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => CanManage && File.Exists(RestorePath) && IsRestoreConfirmed && !IsLoading);
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
    // Ekranda Turkce ad, API'ye SettingsValidation.AuthTypes'taki kod gider. Once ham "None" gorunuyordu.
    public IReadOnlyList<SmsAuthTypeOption> SmsAuthTypes { get; } =
    [
        new("Yok", "None"), new("Temel (kullanıcı adı / şifre)", "Basic"),
        new("Taşıyıcı jeton (Bearer)", "Bearer"), new("API anahtarı", "ApiKey")
    ];
    public IReadOnlyList<BackupFrequencyOption> BackupFrequencies { get; } =
        [new("Günlük", "Daily"), new("Haftalık", "Weekly")];
    public IReadOnlyList<WeekDayOption> WeekDays { get; } =
    [
        new("Pazartesi", DayOfWeek.Monday), new("Salı", DayOfWeek.Tuesday),
        new("Çarşamba", DayOfWeek.Wednesday), new("Perşembe", DayOfWeek.Thursday),
        new("Cuma", DayOfWeek.Friday), new("Cumartesi", DayOfWeek.Saturday),
        new("Pazar", DayOfWeek.Sunday)
    ];
    // Ekranda Turkce ad gorunur, API'ye ve appsettings'e İngilizce seviye adi gider.
    // Serilog "Bilgi" adinda bir seviye tanimaz; ad ile deger ayrilmazsa loglama bozulur.
    public IReadOnlyList<LogLevelOption> LogLevels { get; } =
    [
        new("İzleme", "Trace"), new("Ayıklama", "Debug"), new("Bilgi", "Information"),
        new("Uyarı", "Warning"), new("Hata", "Error"), new("Kritik", "Critical")
    ];
    public ObservableCollection<ApplicationLogItem> Logs { get; } = [];
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RefreshCommands(); } }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    /// <summary>
    /// Kaydedilecek bir sey var mi? Gecersiz sayisal girdi de "kirli" sayilir: aksi halde
    /// Kaydet pasif kalir ve kullanici neden kaydedemedigini asla ogrenemez.
    /// </summary>
    public bool IsDirty => original is not null && (!BuildRequest().Equals(ToRequest(original)) || HasInvalidInput)
        || !string.IsNullOrWhiteSpace(SmsSecret) || !string.IsNullOrWhiteSpace(SyncSecret);
    public bool HasInvalidInput => Validate().Count > 0;
    public string SchoolName { get => schoolName; set => Change(ref schoolName, value); } public string SchoolAddress { get => schoolAddress; set => Change(ref schoolAddress, value); }
    public string SchoolContact { get => schoolContact; set => Change(ref schoolContact, value); } public string LogoPath { get => logoPath; set => Change(ref logoPath, value); }
    public string SmsEndpoint { get => smsEndpoint; set => Change(ref smsEndpoint, value); } public string SmsAuthType { get => smsAuthType; set => Change(ref smsAuthType, value); }
    public string SmsUsername { get => smsUsername; set => Change(ref smsUsername, value); } public string SmsSender { get => smsSender; set => Change(ref smsSender, value); }
    public string SmsTimeoutText { get => smsTimeoutText; set => Change(ref smsTimeoutText, value); }
    public int SmsTimeoutSeconds { get => ParseOr(SmsTimeoutText, original?.Sms.TimeoutSeconds ?? 30); set => SmsTimeoutText = value.ToString(CultureInfo.InvariantCulture); }
    public bool SmsSecretConfigured => original?.Sms.SecretConfigured == true;
    public string? SmsSecret { get => smsSecret; set => Change(ref smsSecret, value); }
    public bool BackupEnabled { get => backupEnabled; set => Change(ref backupEnabled, value); } public string BackupFrequency { get => backupFrequency; set { if (Set(ref backupFrequency, value)) { Raise(nameof(IsWeeklyBackup)); Raise(nameof(IsDirty)); RefreshCommands(); } } }
    public bool IsWeeklyBackup => BackupFrequency == "Weekly";
    public DayOfWeek BackupWeeklyDay { get => backupWeeklyDay; set => Change(ref backupWeeklyDay, value); } public string BackupTime { get => backupTime; set => Change(ref backupTime, value); }
    public string BackupRetentionText { get => backupRetentionText; set => Change(ref backupRetentionText, value); }
    public int BackupRetentionCount { get => ParseOr(BackupRetentionText, original?.Backup.RetentionCount ?? 14); set => BackupRetentionText = value.ToString(CultureInfo.InvariantCulture); }
    public string BackupPath { get => backupPath; set => Change(ref backupPath, value); }
    public bool SyncEnabled { get => syncEnabled; set { Change(ref syncEnabled, value); RefreshCommands(); } } public string SyncEndpoint { get => syncEndpoint; set => Change(ref syncEndpoint, value); }
    public string SyncDeviceId { get => syncDeviceId; set => Change(ref syncDeviceId, value); }
    public string SyncIntervalText { get => syncIntervalText; set => Change(ref syncIntervalText, value); }
    public int SyncIntervalMinutes { get => ParseOr(SyncIntervalText, original?.Sync.IntervalMinutes ?? 5); set => SyncIntervalText = value.ToString(CultureInfo.InvariantCulture); }
    public bool SyncSecretConfigured => original?.Sync.SecretConfigured == true; public string? SyncSecret { get => syncSecret; set => Change(ref syncSecret, value); }
    // Durum kodu ("Disabled", "Ready"...) sunucudan İngilizce gelir; satirin geri kalani zaten Turkce.
    public string SyncStatusText => original is null ? "-" : $"{EnumTextConverter.Translate(original.Sync.Status.State, "SyncState")} | Bekleyen: {original.Sync.Status.Pending} | Hatalı: {original.Sync.Status.Failed} | Çakışma: {original.Sync.Status.Conflicts}";
    /// <summary>Son eşitleme zamanı; hiç çalışmadıysa (sunucu 0001-01-01 yollar) bunu açıkça söyler.</summary>
    public string SyncLastRunText => original?.Sync.Status.LastRunAt is { } at && at.Year > 2000
        ? $"Son eşitleme: {at.ToLocalTime():dd.MM.yyyy HH:mm}" : "Henüz eşitleme çalışmadı.";

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
    public string LogLevel { get => logLevel; set => Change(ref logLevel, value); }
    public string LogRetentionText { get => logRetentionText; set => Change(ref logRetentionText, value); }
    public int LogRetentionDays { get => ParseOr(LogRetentionText, original?.Logs.RetentionDays ?? 30); set => LogRetentionText = value.ToString(CultureInfo.InvariantCulture); }
    public string LogPath { get => logPath; set => Change(ref logPath, value); }
    public string? RestorePath { get => restorePath; set { if (Set(ref restorePath, value)) { Raise(nameof(RestoreFileText)); RefreshCommands(); } } }
    public string RestoreFileText => string.IsNullOrWhiteSpace(RestorePath) ? "Henüz yedek dosyası seçilmedi." : RestorePath;
    public string? RestoreConfirmation { get => restoreConfirmation; set { if (Set(ref restoreConfirmation, value)) { Raise(nameof(IsRestoreConfirmed)); Raise(nameof(RestoreConfirmationHint)); RefreshCommands(); } } }
    /// <summary>Onay tam eslesme ister (ASCII, buyuk harf). Yanlis yazimda dugme pasif kalir ve neden soylenir.</summary>
    public bool IsRestoreConfirmed => string.Equals(RestoreConfirmation, RestoreConfirmationPhrase, StringComparison.Ordinal);
    public string RestoreConfirmationHint => string.IsNullOrEmpty(RestoreConfirmation) ? $"Geri yüklemek için kutuya tam olarak {RestoreConfirmationPhrase} yazın (Türkçe karakter kullanmadan, büyük harfle)."
        : IsRestoreConfirmed ? "Onay alındı; Geri Yükle düğmesi aktif." : $"Onay metni eşleşmiyor. Tam olarak {RestoreConfirmationPhrase} yazın.";
    public string? LastBackupFile { get => lastBackupFile; private set => Set(ref lastBackupFile, value); }
    public int DeviceCount => original?.Links.Devices ?? 0; public int MealTypeCount => original?.Links.ActiveMealTypes ?? 0;
    // Sunucu "CihazAdi - Connected" bicminde birlestirilmis metin yollar (SettingsService).
    // Cihaz adinda da " - " gecebilecegi icin SON ayirici bolunur; yalnizca durum kismi cevrilir.
    // Ayirici yoksa metne dokunulmaz -- beklenmedik bicim kirpilmamalidir.
    public IReadOnlyList<string> DeviceSummaries =>
        (original?.Links.DeviceSummaries ?? []).Select(TranslateDeviceSummary).ToList();

    private static string TranslateDeviceSummary(string summary)
    {
        var separator = summary.LastIndexOf(" - ", StringComparison.Ordinal);
        if (separator < 0) return summary;
        var name = summary[..separator];
        var status = summary[(separator + 3)..];
        return $"{name} - {EnumTextConverter.Translate(status, "DeviceStatus")}";
    }
    public IReadOnlyList<string> MealTypes => original?.Links.MealTypes ?? [];
    public ICommand SaveCommand { get; } public ICommand CancelCommand { get; } public ICommand RefreshCommand { get; }
    public ICommand BackupNowCommand { get; } public ICommand ChooseRestoreCommand { get; } public ICommand ValidateBackupCommand { get; } public ICommand RestoreCommand { get; }
    private SyncConflictItem? selectedConflict;
    public ICommand SyncNowCommand { get; } public ICommand RefreshLogsCommand { get; } public ICommand NavigateDevicesCommand { get; } public ICommand NavigateMealsCommand { get; } public ICommand NavigateHolidaysCommand { get; } public ICommand NavigateUsersCommand { get; }

    public Task InitializeAsync() => LoadAsync();
    public async Task LoadAsync() => await Run(async () => { Apply(await api.GetAsync()); await LoadLogsCoreAsync(); await LoadConflictsAsync(); StatusMessage = null; });
    public async Task SaveAsync()
    {
        // Sunucuya gitmeden once yerel dogrulama: hata alan adiyla ve Turkce soylenir.
        var problems = Validate();
        if (problems.Count > 0) { ErrorMessage = string.Join(Environment.NewLine, problems); StatusMessage = null; validationErrorShown = true; return; }
        validationErrorShown = false;
        await Run(async () =>
        {
            var result = await api.SaveAsync(BuildRequest()); Apply(result.Settings);
            StatusMessage = result.RestartRequired ? "Kaydedildi. Servis ayarlarının (SMS, yedekleme, eşitleme, log) uygulanması için uygulama yeniden başlatılmalıdır." : "Ayarlar kaydedildi.";
        });
    }
    public void Cancel() { if (original is not null) Apply(original); ErrorMessage = null; StatusMessage = "Değişiklikler geri alındı."; }
    public void SetSmsSecret(string value) => SmsSecret = value; public void SetSyncSecret(string value) => SyncSecret = value;
    private async Task BackupNowAsync() => await Run(async () => { var x = await api.BackupNowAsync(); LastBackupFile = x.FileName; StatusMessage = $"Yedek oluşturuldu: {x.FileName} ({x.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}). Şema sürümü {x.SchemaVersion}, uygulama {x.AppVersion}."; });
    private async Task ValidateBackupAsync() => await Run(async () => { var x = await api.ValidateBackupAsync(RestorePath!); StatusMessage = $"Yedek doğrulandı: {x.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm} tarihli, şema sürümü {x.SchemaVersion}, uygulama {x.AppVersion}."; });
    private async Task RestoreAsync() => await Run(async () => { var x = await api.RestoreAsync(RestorePath!, RestoreConfirmation!); StatusMessage = x.RestartRequired ? "Geri yükleme tamamlandı. Uygulama yeniden başlatılmalıdır." : "Geri yükleme tamamlandı."; RestoreConfirmation = null; });
    private async Task SyncNowAsync() => await Run(async () => { var x = await api.RunSyncAsync(); StatusMessage = $"Eşitleme tamamlandı: {x.Succeeded} başarılı, {x.RetryPending} bekliyor, {x.Conflicts} çakışma."; await LoadAsync(); await LoadConflictsAsync(); });

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
    private async Task LoadLogsCoreAsync() { var page = await api.LogsAsync(1, 100); Logs.Clear(); foreach (var x in page.Items) Logs.Add(x); Raise(nameof(HasLogs)); }
    public bool HasLogs => Logs.Count > 0;
    private void ChooseRestore()
    {
        var chosen = files.OpenFile("Geri yüklenecek yedek dosyasını seçin", "YemekhanePro yedeği (*.zip)|*.zip");
        if (!string.IsNullOrWhiteSpace(chosen)) { RestorePath = chosen; StatusMessage = null; ErrorMessage = null; }
    }

    /// <summary>
    /// Sunucuya gonderilmeden once yakalanan girdi hatalari. Sunucu da ayni sinirlari
    /// dogrular (SettingsValidation) ama oradaki mesaj ancak istek gittikten sonra gelir.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(SchoolName)) problems.Add("Okul adı boş olamaz.");
        CheckNumber(problems, SmsTimeoutText, 1, 300, "SMS zaman aşımı (saniye)");
        CheckNumber(problems, BackupRetentionText, 1, 365, "Saklanacak yedek sayısı");
        CheckNumber(problems, SyncIntervalText, 1, 1440, "Eşitleme aralığı (dakika)");
        CheckNumber(problems, LogRetentionText, 1, 3650, "Log saklama süresi (gün)");
        if (!TryParseTime(BackupTime, out _)) problems.Add("Yedekleme saati SS:dd biçiminde olmalıdır (örn. 02:00); saat 0-23, dakika 0-59.");
        if (SyncEnabled && (string.IsNullOrWhiteSpace(SyncEndpoint) || string.IsNullOrWhiteSpace(SyncDeviceId)))
            problems.Add("Eşitleme etkinken sunucu adresi ve cihaz kimliği zorunludur.");
        return problems;
    }

    private static void CheckNumber(List<string> problems, string text, int min, int max, string name)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) problems.Add($"{name} sayı olmalıdır; girilen: '{text}'.");
        else if (value < min || value > max) problems.Add($"{name} {min} ile {max} arasında olmalıdır; girilen: {value}.");
    }

    private static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private async Task Run(Func<Task> action)
    {
        IsLoading = true; ErrorMessage = null; IsOffline = false;
        try { await action(); }
        catch (LoginRequiredException) { ErrorMessage = "Bu işlem için oturum ve gerekli izin bulunamadı."; }
        // Sunucunun reddi (400/409) bir dogrulama mesajidir, baglanti kopmasi degil:
        // mesaj aynen gosterilir, "Çevrimdışı" rozeti yakilmaz.
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { IsOffline = true; ErrorMessage = "Ayarlar servisine ulaşılamadı."; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    private void Apply(SettingsDocument x)
    {
        original = x; schoolName = x.School.Name; schoolAddress = x.School.Address ?? ""; schoolContact = x.School.Contact ?? ""; logoPath = x.School.LogoPath ?? "";
        smsEndpoint = x.Sms.Endpoint ?? ""; smsAuthType = x.Sms.AuthType; smsUsername = x.Sms.Username ?? ""; smsSender = x.Sms.Sender ?? ""; smsTimeoutText = x.Sms.TimeoutSeconds.ToString(CultureInfo.InvariantCulture); smsSecret = null;
        backupEnabled = x.Backup.Enabled; backupFrequency = x.Backup.Frequency; backupWeeklyDay = x.Backup.WeeklyDay; backupTime = x.Backup.Time.ToString("HH:mm", CultureInfo.InvariantCulture); backupRetentionText = x.Backup.RetentionCount.ToString(CultureInfo.InvariantCulture); backupPath = x.Backup.Path ?? "";
        syncEnabled = x.Sync.Enabled; syncEndpoint = x.Sync.Endpoint ?? ""; syncDeviceId = x.Sync.DeviceId ?? ""; syncIntervalText = x.Sync.IntervalMinutes.ToString(CultureInfo.InvariantCulture); syncSecret = null;
        logLevel = x.Logs.Level; logRetentionText = x.Logs.RetentionDays.ToString(CultureInfo.InvariantCulture); logPath = x.Logs.Path ?? "";
        foreach (var name in GetType().GetProperties().Where(p => p.CanRead).Select(p => p.Name)) Raise(name); RefreshCommands();
    }
    private SaveSettingsRequest BuildRequest() => new(new(SchoolName, EmptyToNull(SchoolAddress), EmptyToNull(SchoolContact), EmptyToNull(LogoPath)), new(EmptyToNull(SmsEndpoint), SmsAuthType, EmptyToNull(SmsUsername), EmptyToNull(SmsSender), SmsTimeoutSeconds, EmptyToNull(SmsSecret)), new(BackupEnabled, BackupFrequency, BackupWeeklyDay, TryParseTime(BackupTime, out var time) ? time : original?.Backup.Time ?? TimeOnly.MinValue, BackupRetentionCount, EmptyToNull(BackupPath)), new(EmptyToNull(SyncEndpoint), EmptyToNull(SyncDeviceId), SyncIntervalMinutes, SyncEnabled, EmptyToNull(SyncSecret)), new(LogLevel, LogRetentionDays, EmptyToNull(LogPath)));
    private static SaveSettingsRequest ToRequest(SettingsDocument x) => new(new(x.School.Name, x.School.Address, x.School.Contact, x.School.LogoPath), new(x.Sms.Endpoint, x.Sms.AuthType, x.Sms.Username, x.Sms.Sender, x.Sms.TimeoutSeconds, null), new(x.Backup.Enabled, x.Backup.Frequency, x.Backup.WeeklyDay, x.Backup.Time, x.Backup.RetentionCount, x.Backup.Path), new(x.Sync.Endpoint, x.Sync.DeviceId, x.Sync.IntervalMinutes, x.Sync.Enabled, null), new(x.Logs.Level, x.Logs.RetentionDays, x.Logs.Path));
    // Yerel dogrulama hatasi gosterildikten sonra kullanici alani duzeltirse mesaj kalkar;
    // aksi halde "abc" uyarisi, kutu "2" yazarken bile ekranda asili kaliyordu.
    private bool validationErrorShown;
    private void Change<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        if (validationErrorShown && Validate().Count == 0) { validationErrorShown = false; ErrorMessage = null; }
        Raise(nameof(IsDirty)); Raise(nameof(HasInvalidInput)); RefreshCommands();
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void RefreshCommands() { foreach (var c in new[] { SaveCommand, CancelCommand, RefreshCommand, BackupNowCommand, ChooseRestoreCommand, ValidateBackupCommand, RestoreCommand, SyncNowCommand, RefreshConflictsCommand, RequeueConflictCommand, RefreshLogsCommand }) if (c is AsyncCommand a) a.Refresh(); else if (c is RelayCommand r) r.Refresh(); }
}
