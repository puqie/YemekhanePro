namespace Yemekhane.Infrastructure.Backup;

public enum BackupScheduleFrequency
{
    Daily,
    Weekly
}

public sealed class BackupOptions
{
    public string? Directory { get; set; }
    public bool ScheduleEnabled { get; set; }
    public BackupScheduleFrequency Schedule { get; set; } = BackupScheduleFrequency.Daily;
    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Sunday;
    public TimeOnly Time { get; set; } = new(2, 0);
    public int RetentionCount { get; set; } = 14;
    public long MaximumArchiveBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumExtractedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
}

public sealed record BackupFileManifest(string Path, long Size, string Sha256);

public sealed record BackupManifest(
    int FormatVersion,
    Guid BackupId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset CreatedAtIstanbul,
    string AppVersion,
    string SchemaVersion,
    IReadOnlyList<BackupFileManifest> Files);

public sealed record BackupResult(Guid BackupId, string FileName, string ArchivePath, BackupManifest Manifest);

public sealed record RestoreResult(Guid BackupId, bool Restored, bool RestartRequired, string SafetyBackupFileName);
public sealed record ValidatedBackup(Guid BackupId, DateTimeOffset CreatedAtUtc, string SchemaVersion, string AppVersion);

public sealed class BackupValidationException(string message) : Exception(message);

public static class BackupSchedule
{
    public static DateTimeOffset GetNextRun(DateTimeOffset now, BackupOptions options, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var date = DateOnly.FromDateTime(localNow.DateTime);
        if (options.Schedule == BackupScheduleFrequency.Weekly)
        {
            var days = ((int)options.WeeklyDay - (int)localNow.DayOfWeek + 7) % 7;
            date = date.AddDays(days);
        }

        var localCandidate = date.ToDateTime(options.Time, DateTimeKind.Unspecified);
        if (localCandidate <= localNow.DateTime)
            localCandidate = localCandidate.AddDays(options.Schedule == BackupScheduleFrequency.Daily ? 1 : 7);

        return new DateTimeOffset(localCandidate, timeZone.GetUtcOffset(localCandidate)).ToUniversalTime();
    }
}
