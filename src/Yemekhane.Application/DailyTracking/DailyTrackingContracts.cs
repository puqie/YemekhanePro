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
    Guid? StudentId = null,
    /// <summary>
    /// Bu GUNDEN itibaren (okul saatiyle, gun basi dahil). Onceden tarih suzgeci HIC yoktu:
    /// yalnizca son N kayit gelirdi ve "gecen yil 12 Eylul'de yemek yedi mi" sorusu
    /// cevaplanamiyordu; kullanici sayfa sayfa geriye gitmek zorundaydi.
    /// </summary>
    DateOnly? FromDate = null,
    /// <summary>Bu GUNE kadar (gun sonu dahil).</summary>
    DateOnly? ToDate = null);

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

        if (query.FromDate is { } requestedFrom && query.ToDate is { } requestedTo && requestedTo < requestedFrom)
            throw new RequestValidationException("Bitiş tarihi başlangıç tarihinden önce olamaz.");

        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Istanbul).DateTime);
        // Tarih aralığı verilmezse BUGÜN gösterilir (günlük takip ekranının davranışı).
        // Verilirse GEÇMİŞ YILLARA kadar gidilebilir: "geçen yıl 12 Eylül'de yemek yedi
        // mi" sorusu önceden cevaplanamıyordu, çünkü aralık her zaman bugüne sabitti.
        var fromDate = query.FromDate ?? today;
        var toDate = query.ToDate ?? fromDate;
        var start = DayStart(fromDate);
        // Bitiş günü DAHİL: kullanıcı "20 Eylül'e kadar" derken o günü de kasteder.
        var end = DayStart(toDate.AddDays(1));
        return repository.GetAsync(query with
        {
            Decision = query.Decision?.ToUpperInvariant(),
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim()
        }, start, end, now, cancellationToken);
    }

    /// <summary>Okul saatiyle gunun basi; SQLite karsilastirmalari icin UTC an olarak.</summary>
    private static DateTimeOffset DayStart(DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Istanbul),
            TimeSpan.Zero);

    private static TimeZoneInfo FindIstanbulTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
