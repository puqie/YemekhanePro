using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Settings;

public sealed partial class SettingsService(YemekhaneDbContext db, ISecretProtector protector, IAuditService audit,
    TimeProvider timeProvider, ILogger<SettingsService>? logger = null) : ISettingsService
{
    public const string SmsSecretKey = "Sms.Secret";
    public const string SyncSecretKey = "Sync.Secret";
    private static readonly HashSet<string> SecretKeys = [SmsSecretKey, SyncSecretKey];

    public async Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default)
    {
        var values = await db.Set<SystemSetting>().AsNoTracking().ToDictionaryAsync(x => x.Key, x => x, cancellationToken);
        var pending = await db.SyncOperations.CountAsync(x => x.SyncStatus == "Pending" || x.SyncStatus == "RetryPending", cancellationToken);
        var failed = await db.SyncOperations.CountAsync(x => x.SyncStatus == "PermanentFailure" || x.SyncStatus == "Conflict", cancellationToken);
        var last = await db.SyncOperations.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.CreatedAt)).Select(x => (DateTimeOffset?)x.UpdatedAt ?? x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var deviceItems = await db.Devices.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name + " - " + x.ConnectionStatus).ToListAsync(cancellationToken);
        var mealItems = await db.Set<MealType>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(cancellationToken);
        var status = await BuildSyncStatusAsync(values, pending, failed, last, cancellationToken);
        return Map(values, status, deviceItems, mealItems);
    }

    public async Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default)
    {
        SettingsValidation.Validate(request);
        var incoming = Values(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.Set<SystemSetting>().ToDictionaryAsync(x => x.Key, x => x, cancellationToken);
        var changedKeys = new List<string>();
        foreach (var pair in incoming)
        {
            if (current.TryGetValue(pair.Key, out var setting))
            {
                if (setting.Value == pair.Value && !setting.IsSecret) continue;
                setting.Value = pair.Value; setting.IsSecret = false; setting.UpdatedAt = timeProvider.GetUtcNow();
            }
            else
            {
                setting = new SystemSetting { Id = Guid.NewGuid(), Key = pair.Key, Value = pair.Value,
                    IsSecret = false, CreatedAt = timeProvider.GetUtcNow() };
                db.Add(setting); current[pair.Key] = setting;
            }
            changedKeys.Add(pair.Key);
        }
        SaveSecret(current, SmsSecretKey, request.Sms.Secret, changedKeys);
        SaveSecret(current, SyncSecretKey, request.Sync.Secret, changedKeys);
        var categories = changedKeys.Select(x => x.Split('.')[0]).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (changedKeys.Count > 0)
            audit.Record(new AuditEntry("SettingsUpdated", nameof(SystemSetting), null, "Sistem ayarları güncellendi.",
                changedKeys.Count, After: new { ChangedKeys = changedKeys.Order(), SecretKeysChanged = changedKeys.Where(SecretKeys.Contains).Order() }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = await GetAsync(cancellationToken);
        var restart = categories.Any(x => x is "Sms" or "Backup" or "Sync" or "Logs");
        return new SaveSettingsResult(result with { RestartRequired = restart }, categories, restart);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!SecretKeys.Contains(key)) throw new ArgumentException("Bilinmeyen gizli ayar anahtarı.", nameof(key));
        var value = await db.Set<SystemSetting>().AsNoTracking().SingleOrDefaultAsync(x => x.Key == key && x.IsSecret, cancellationToken);
        if (value is null) return null;
        try
        {
            return protector.Unprotect(value.Value);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // Baska makinede sifrelenmis blob (tasima/geri yukleme sonrasi) cozulemez.
            // Firlatmak uygulama baslangicini dusururdu ve UI'dan geri donus yolu kalmazdi.
            LogSecretUnreadable(logger, key);
            return null;
        }
    }

    public async Task<PagedResult<ApplicationLogItem>> LogsAsync(ApplicationLogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 200) throw new ArgumentException("Log sayfalama bilgisi geçersiz.");
        var source = from item in db.DeviceEvents.AsNoTracking()
                     join device in db.Devices.AsNoTracking() on item.DeviceId equals device.Id
                     select new { Item = item, DeviceName = device.Name };
        if (!string.IsNullOrWhiteSpace(query.Level)) source = source.Where(x => x.Item.Severity == query.Level.Trim());
        var total = await source.CountAsync(cancellationToken);
        var items = await source.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.Item.Timestamp)).ThenByDescending(x => x.Item.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new ApplicationLogItem(x.Item.Id, x.Item.Timestamp, x.Item.Severity,
                "Device/" + x.DeviceName, x.Item.Message, x.Item.PayloadJson)).ToListAsync(cancellationToken);
        return new PagedResult<ApplicationLogItem>(items, query.Page, query.PageSize, total);
    }

    public async Task<SyncStatus> SyncStatusAsync(CancellationToken cancellationToken = default)
    {
        var values = await db.Set<SystemSetting>().AsNoTracking().ToDictionaryAsync(x => x.Key, x => x, cancellationToken);
        var pending = await db.SyncOperations.CountAsync(x => x.SyncStatus == "Pending" || x.SyncStatus == "RetryPending", cancellationToken);
        var failed = await db.SyncOperations.CountAsync(x => x.SyncStatus == "PermanentFailure" || x.SyncStatus == "Conflict", cancellationToken);
        var last = await db.SyncOperations.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.CreatedAt)).Select(x => (DateTimeOffset?)x.UpdatedAt ?? x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return await BuildSyncStatusAsync(values, pending, failed, last, cancellationToken);
    }

    private async Task<SyncStatus> BuildSyncStatusAsync(Dictionary<string, SystemSetting> values,
        int pending, int failed, DateTimeOffset? last, CancellationToken cancellationToken)
    {
        if (!GetBool(values, "Sync.Enabled", false)) return new SyncStatus("Disabled", pending, failed, last, null);
        var transportFailure = await db.SyncOperations.AsNoTracking().AnyAsync(x =>
            x.SyncStatus == "RetryPending" && x.LastError != null &&
            (x.LastError.StartsWith("transport_error") || x.LastError.StartsWith("transport_timeout")), cancellationToken);
        if (transportFailure)
            return new SyncStatus("Offline", pending, failed, last, "Bulut sunucusuna ulaşılamıyor; yerel işlemler kuyruğa alınıyor.");
        if (failed > 0) return new SyncStatus("Attention", pending, failed, last, "Senkronizasyon çakışmaları inceleme bekliyor.");
        return new SyncStatus(pending > 0 ? "Pending" : "Ready", pending, failed, last, null);
    }

    private void SaveSecret(Dictionary<string, SystemSetting> current, string key, string? plaintext, List<string> changed)
    {
        // null  -> dokunma (istemci alani hic gondermedi)
        // ""    -> gizli bilgiyi TEMIZLE (sizan anahtari geri cekebilmek icin)
        // deger -> yeni gizli bilgiyi yaz
        if (plaintext is null) return;
        if (plaintext.Length == 0 || string.IsNullOrWhiteSpace(plaintext))
        {
            if (current.TryGetValue(key, out var existing))
            {
                db.Remove(existing);
                changed.Add(key);
            }

            return;
        }

        var encrypted = protector.Protect(plaintext);
        if (current.TryGetValue(key, out var setting))
        { setting.Value = encrypted; setting.IsSecret = true; setting.UpdatedAt = timeProvider.GetUtcNow(); }
        else
        { db.Add(new SystemSetting { Id = Guid.NewGuid(), Key = key, Value = encrypted, IsSecret = true, CreatedAt = timeProvider.GetUtcNow() }); }
        changed.Add(key);
    }

    private static Dictionary<string, string> Values(SaveSettingsRequest x) => new(StringComparer.Ordinal)
    {
        ["School.Name"] = x.School.Name.Trim(), ["School.Address"] = Clean(x.School.Address), ["School.Contact"] = Clean(x.School.Contact), ["School.LogoPath"] = Clean(x.School.LogoPath),
        ["Sms.Endpoint"] = Clean(x.Sms.Endpoint), ["Sms.AuthType"] = x.Sms.AuthType, ["Sms.Username"] = Clean(x.Sms.Username), ["Sms.Sender"] = Clean(x.Sms.Sender), ["Sms.TimeoutSeconds"] = Number(x.Sms.TimeoutSeconds),
        ["Backup.Enabled"] = Bool(x.Backup.Enabled), ["Backup.Frequency"] = x.Backup.Frequency, ["Backup.WeeklyDay"] = x.Backup.WeeklyDay.ToString(), ["Backup.Time"] = x.Backup.Time.ToString("HH:mm", CultureInfo.InvariantCulture), ["Backup.RetentionCount"] = Number(x.Backup.RetentionCount), ["Backup.Path"] = Clean(x.Backup.Path),
        ["Sync.Endpoint"] = Clean(x.Sync.Endpoint), ["Sync.DeviceId"] = Clean(x.Sync.DeviceId), ["Sync.IntervalMinutes"] = Number(x.Sync.IntervalMinutes), ["Sync.Enabled"] = Bool(x.Sync.Enabled),
        ["Logs.Level"] = x.Logs.Level, ["Logs.RetentionDays"] = Number(x.Logs.RetentionDays), ["Logs.Path"] = Clean(x.Logs.Path)
    };

    private static SettingsDocument Map(IReadOnlyDictionary<string, SystemSetting> v, SyncStatus status, List<string> devices, List<string> meals) => new(
        new(Get(v, "School.Name", "YemekhanePro"), Null(Get(v, "School.Address")), Null(Get(v, "School.Contact")), Null(Get(v, "School.LogoPath"))),
        new(Null(Get(v, "Sms.Endpoint")), Get(v, "Sms.AuthType", "None"), Null(Get(v, "Sms.Username")), Null(Get(v, "Sms.Sender")), GetInt(v, "Sms.TimeoutSeconds", 30), IsConfigured(v, SmsSecretKey)),
        new(GetBool(v, "Backup.Enabled", false), Get(v, "Backup.Frequency", "Daily"), Enum.TryParse<DayOfWeek>(Get(v, "Backup.WeeklyDay", "Sunday"), out var day) ? day : DayOfWeek.Sunday, TimeOnly.TryParseExact(Get(v, "Backup.Time", "02:00"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : new TimeOnly(2, 0), GetInt(v, "Backup.RetentionCount", 14), Null(Get(v, "Backup.Path"))),
        new(Null(Get(v, "Sync.Endpoint")), Null(Get(v, "Sync.DeviceId")), GetInt(v, "Sync.IntervalMinutes", 5), GetBool(v, "Sync.Enabled", false), IsConfigured(v, SyncSecretKey), status),
        new(Get(v, "Logs.Level", "Information"), GetInt(v, "Logs.RetentionDays", 30), Null(Get(v, "Logs.Path"))), new(devices.Count, devices, meals.Count, meals), false);

    private static string Get(IReadOnlyDictionary<string, SystemSetting> values, string key, string fallback = "") => values.TryGetValue(key, out var x) && !x.IsSecret ? x.Value : fallback;
    private static int GetInt(IReadOnlyDictionary<string, SystemSetting> values, string key, int fallback) => int.TryParse(Get(values, key), NumberFormatInfo.InvariantInfo, out var x) ? x : fallback;
    private static bool GetBool(IReadOnlyDictionary<string, SystemSetting> values, string key, bool fallback) => bool.TryParse(Get(values, key), out var x) ? x : fallback;
    private static bool IsConfigured(IReadOnlyDictionary<string, SystemSetting> values, string key) => values.TryGetValue(key, out var x) && x.IsSecret && x.Value.Length > 0;
    private static string Clean(string? value) => value?.Trim() ?? "";
    private static string? Null(string value) => value.Length == 0 ? null : value;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value.ToString(CultureInfo.InvariantCulture);

    private static readonly Action<ILogger, string, Exception?> SecretUnreadable = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(5201, nameof(LogSecretUnreadable)),
        "Gizli ayar cozulemedi ve yok sayildi: {SettingKey}. Yeniden girilmesi gerekiyor.");

    private static void LogSecretUnreadable(ILogger? logger, string key)
    {
        if (logger is not null) SecretUnreadable(logger, key, null);
    }
}
