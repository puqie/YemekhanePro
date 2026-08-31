using Yemekhane.Application.Common;

namespace Yemekhane.Application.DailyTracking;

public sealed record DailyTrackingQuery(
    int PageSize = 100,
    string? Decision = null,
    Guid? MealTypeId = null,
    Guid? DeviceId = null,
    Guid? ClassId = null,
    string? Search = null,
    DateTimeOffset? CursorTimestamp = null,
    Guid? CursorOperationId = null,
    DateTimeOffset? SinceTimestamp = null,
    Guid? SinceOperationId = null,
    Guid? StudentId = null);

public sealed record DailyTrackingRow(
    Guid OperationId,
    DateTimeOffset Timestamp,
    string CardNumber,
    Guid? StudentId,
    string? StudentNo,
    string StudentName,
    Guid? ClassId,
    string? ClassName,
    Guid? MealTypeId,
    string? MealType,
    Guid DeviceId,
    string DeviceName,
    string Decision,
    string Reason);

public sealed record DailyTrackingSummary(int Total, int Allowed, int Denied);

public sealed record DailyTrackingPage(
    IReadOnlyList<DailyTrackingRow> Items,
    DailyTrackingSummary Summary,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? NextCursorTimestamp,
    Guid? NextCursorOperationId,
    bool HasMore);

public interface IDailyTrackingRepository
{
    Task<DailyTrackingPage> GetAsync(DailyTrackingQuery request, DateTimeOffset dayStart,
        DateTimeOffset dayEnd, DateTimeOffset generatedAt, CancellationToken cancellationToken);
}

public sealed class DailyTrackingService(IDailyTrackingRepository repository, TimeProvider timeProvider)
{
    private static readonly TimeZoneInfo Istanbul = FindIstanbulTimeZone();

    public Task<DailyTrackingPage> GetAsync(DailyTrackingQuery query, CancellationToken cancellationToken = default)
    {
        if (query.PageSize is < 1 or > 200) throw new RequestValidationException("Sayfa boyutu 1 ile 200 arasında olmalıdır.");
        if (query.Decision is not null && !string.Equals(query.Decision, "ALLOW", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(query.Decision, "DENY", StringComparison.OrdinalIgnoreCase))
            throw new RequestValidationException("Karar filtresi ALLOW veya DENY olmalıdır.");
        if (query.Search?.Length > 100) throw new RequestValidationException("Arama en fazla 100 karakter olabilir.");
        if (query.CursorTimestamp.HasValue != query.CursorOperationId.HasValue)
            throw new RequestValidationException("Cursor timestamp ve operationId birlikte verilmelidir.");
        if (query.SinceTimestamp.HasValue != query.SinceOperationId.HasValue)
            throw new RequestValidationException("Since timestamp ve operationId birlikte verilmelidir.");
        if (query.CursorTimestamp.HasValue && query.SinceTimestamp.HasValue)
            throw new RequestValidationException("Cursor ve since aynı istekte kullanılamaz.");

        var now = timeProvider.GetUtcNow();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Istanbul).DateTime);
        var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, Istanbul), TimeSpan.Zero);
        return repository.GetAsync(query with
        {
            Decision = query.Decision?.ToUpperInvariant(),
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim()
        }, start, start.AddDays(1), now, cancellationToken);
    }

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
