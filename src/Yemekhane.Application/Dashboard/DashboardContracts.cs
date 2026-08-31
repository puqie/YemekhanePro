namespace Yemekhane.Application.Dashboard;

public sealed record DashboardKpis(
    int ActiveStudents,
    int EntitledStudents,
    int EntitlementQuantity,
    int Used,
    int Remaining,
    int OnLeave,
    int Denied);

public sealed record DashboardAccessRow(
    Guid Id,
    DateTimeOffset Timestamp,
    string StudentName,
    string? StudentNo,
    string CardNumber,
    string DeviceName,
    string? MealType,
    string Decision,
    string Reason);

public sealed record DashboardDeviceSummary(int Total, int Online, int Offline, int Error);

public sealed record DashboardDeviceRow(
    Guid Id,
    string Name,
    string Type,
    string Status,
    DateTimeOffset? LastConnectedAt);

public sealed record DashboardClassUsage(string ClassName, int Used, int EntitlementQuantity);

public sealed record DashboardErrorRow(
    Guid Id,
    DateTimeOffset Timestamp,
    string DeviceName,
    string Severity,
    string Message);

public sealed record DashboardSnapshot(
    DateOnly Date,
    DateTimeOffset GeneratedAt,
    DashboardKpis Kpis,
    IReadOnlyList<DashboardAccessRow> RecentAccess,
    DashboardDeviceSummary DeviceSummary,
    IReadOnlyList<DashboardDeviceRow> Devices,
    IReadOnlyList<DashboardClassUsage> ClassUsage,
    IReadOnlyList<DashboardErrorRow> RecentErrors);

public interface IDashboardRepository
{
    Task<DashboardSnapshot> GetAsync(
        DateOnly currentDate,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);
}

public sealed class DashboardService(IDashboardRepository repository, TimeProvider timeProvider)
{
    private static readonly TimeZoneInfo Istanbul = FindIstanbulTimeZone();

    public Task<DashboardSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Istanbul).DateTime);
        var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, Istanbul), TimeSpan.Zero);
        return repository.GetAsync(date, start, start.AddDays(1), now, cancellationToken);
    }

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
