using Yemekhane.Application.Common;

namespace Yemekhane.Application.Settings;

public sealed record SchoolSettings(string Name, string? Address, string? Contact, string? LogoPath);
public sealed record SmsProviderSettings(string? Endpoint, string AuthType, string? Username, string? Sender,
    int TimeoutSeconds, bool SecretConfigured);
public sealed record BackupSettings(bool Enabled, string Frequency, DayOfWeek WeeklyDay, TimeOnly Time,
    int RetentionCount, string? Path);
public sealed record SyncSettings(string? Endpoint, string? DeviceId, int IntervalMinutes, bool Enabled,
    bool SecretConfigured, SyncStatus Status);
public sealed record SyncStatus(string State, int Pending, int Failed, DateTimeOffset? LastRunAt, string? Message,
    int Conflicts = 0);

/// <summary>Cakisma nedeniyle duran bir islem; operator karar verebilsin diye nedeni tasir.</summary>
public sealed record SyncConflictItem(Guid OperationId, string EntityName, string? EntityId,
    string OperationType, DateTimeOffset Timestamp, int AttemptCount, string? LastError);
public sealed record LogSettings(string Level, int RetentionDays, string? Path);
public sealed record SettingsLinks(int Devices, IReadOnlyList<string> DeviceSummaries, int ActiveMealTypes,
    IReadOnlyList<string> MealTypes);
public sealed record SettingsDocument(SchoolSettings School, SmsProviderSettings Sms, BackupSettings Backup,
    SyncSettings Sync, LogSettings Logs, SettingsLinks Links, bool RestartRequired);

public sealed record SaveSchoolSettings(string Name, string? Address, string? Contact, string? LogoPath);
public sealed record SaveSmsProviderSettings(string? Endpoint, string AuthType, string? Username, string? Sender,
    int TimeoutSeconds, string? Secret);
public sealed record SaveBackupSettings(bool Enabled, string Frequency, DayOfWeek WeeklyDay, TimeOnly Time,
    int RetentionCount, string? Path);
public sealed record SaveSyncSettings(string? Endpoint, string? DeviceId, int IntervalMinutes, bool Enabled, string? Secret);
public sealed record SaveLogSettings(string Level, int RetentionDays, string? Path);
public sealed record SaveSettingsRequest(SaveSchoolSettings School, SaveSmsProviderSettings Sms,
    SaveBackupSettings Backup, SaveSyncSettings Sync, SaveLogSettings Logs);
public sealed record SaveSettingsResult(SettingsDocument Settings, IReadOnlyList<string> ChangedCategories,
    bool RestartRequired);

public sealed record ApplicationLogItem(Guid Id, DateTimeOffset Timestamp, string Level, string Source,
    string Message, string? Properties);
public sealed record ApplicationLogQuery(int Page = 1, int PageSize = 50, string? Level = null);
public sealed record BackupCommandResult(Guid BackupId, string FileName, DateTimeOffset CreatedAt,
    string SchemaVersion, string AppVersion);
public sealed record BackupValidationResult(Guid BackupId, DateTimeOffset CreatedAt, string SchemaVersion,
    string AppVersion, bool Valid);

public static class SettingsValidation
{
    private static readonly string[] AuthTypes = ["None", "Basic", "Bearer", "ApiKey"];
    private static readonly string[] Frequencies = ["Daily", "Weekly"];
    private static readonly string[] LogLevels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    public static void Validate(SaveSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Required(request.School.Name, 200, "Okul adı");
        Optional(request.School.Address, 500, "Adres"); Optional(request.School.Contact, 200, "İletişim");
        Optional(request.School.LogoPath, 500, "Logo yolu");
        OptionalOutboundUri(request.Sms.Endpoint, "SMS endpoint");
        OneOf(request.Sms.AuthType, AuthTypes, "SMS kimlik doğrulama türü");
        Optional(request.Sms.Username, 200, "SMS kullanıcı adı"); Optional(request.Sms.Sender, 50, "SMS gönderici");
        Range(request.Sms.TimeoutSeconds, 1, 300, "SMS zaman aşımı");
        OneOf(request.Backup.Frequency, Frequencies, "Yedek sıklığı");
        Range(request.Backup.RetentionCount, 1, 365, "Yedek saklama sayısı");
        OptionalLocalDirectory(request.Backup.Path, "Yedek yolu");
        OptionalOutboundUri(request.Sync.Endpoint, "Sync endpoint"); Optional(request.Sync.DeviceId, 100, "Cihaz kimliği");
        Range(request.Sync.IntervalMinutes, 1, 1440, "Sync aralığı");
        if (request.Sync.Enabled && (string.IsNullOrWhiteSpace(request.Sync.Endpoint) || string.IsNullOrWhiteSpace(request.Sync.DeviceId)))
            throw new ArgumentException("Sync etkin olduğunda endpoint ve cihaz kimliği zorunludur.");
        OneOf(request.Logs.Level, LogLevels, "Log seviyesi"); Range(request.Logs.RetentionDays, 1, 3650, "Log saklama süresi");
        OptionalLocalDirectory(request.Logs.Path, "Log yolu");
        Optional(request.Sms.Secret, 4096, "SMS gizli bilgisi"); Optional(request.Sync.Secret, 4096, "Sync gizli bilgisi");
    }

    private static void Required(string? value, int max, string name)
    { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) throw new ArgumentException($"{name} zorunlu ve en fazla {max} karakter olmalıdır."); }
    private static void Optional(string? value, int max, string name)
    { if (value?.Trim().Length > max) throw new ArgumentException($"{name} en fazla {max} karakter olmalıdır."); }
    private static void Range(int value, int min, int max, string name)
    { if (value < min || value > max) throw new ArgumentException($"{name} {min} ile {max} arasında olmalıdır."); }
    private static void OneOf(string value, IReadOnlyCollection<string> values, string name)
    { if (!values.Contains(value, StringComparer.Ordinal)) throw new ArgumentException($"{name} geçersiz."); }
    /// <summary>
    /// Dosya sistemine yazan/silen dizin ayarlarini dogrular. Bu yollar arka planda
    /// <c>Directory.CreateDirectory</c> ve arsiv silme icin kullanildigindan, yalnizca
    /// mutlak yerel yollar kabul edilir: goreli yol calisma dizinine, UNC yolu ise
    /// uzak paylasima yazmaya izin verirdi.
    /// </summary>
    private static void OptionalLocalDirectory(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var path = value.Trim();
        Optional(path, 1000, name);
        if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || path.Contains('\0'))
            throw new ArgumentException($"{name} geçersiz karakter içeriyor.");
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException($"{name} tam nitelikli mutlak bir yol olmalıdır.");
        // "C:\Backup\..\..\Windows" gibi degerler normallestirildiginde bambaska bir dizine isaret eder.
        if (path.Split('\\', '/').Any(segment => segment == ".."))
            throw new ArgumentException($"{name} üst dizine çıkan (..) bölüm içeremez.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException($"{name} ağ paylaşımı (UNC) olamaz; yerel bir dizin belirtin.");
    }

    private static void OptionalOutboundUri(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { OutboundEndpointPolicy.ValidateSyntax(value, allowPrivateNetworks: true); }
        catch (RequestValidationException exception) { throw new ArgumentException($"{name}: {exception.Message}"); }
    }
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public interface ISettingsService
{
    Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default);
    Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task<PagedResult<ApplicationLogItem>> LogsAsync(ApplicationLogQuery query, CancellationToken cancellationToken = default);
    Task<SyncStatus> SyncStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default);
    Task SyncRequeueAsync(Guid operationId, CancellationToken cancellationToken = default);
}
