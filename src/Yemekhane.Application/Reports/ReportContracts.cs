namespace Yemekhane.Application.Reports;

public enum ReportType
{
    DailyAccess,
    MealEntitlement,
    StudentMealUsage,
    ClassMeal,
    DailyCash,
    Income,
    Sms,
    Turnstile,
    DeniedAccess,
    CardMovements,
    HolidayTransfer
}

public sealed record ReportQuery(
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    string? StudentNo = null,
    string? CardNo = null,
    string? FirstName = null,
    string? LastName = null,
    string? Class = null,
    string? Department = null,
    string? Section = null,
    string? Job = null,
    string? MealType = null,
    string? Device = null,
    string? Decision = null,
    string? Status = null,
    string SortBy = "timestamp",
    bool Descending = true,
    int Page = 1,
    int PageSize = 50);

public sealed record ReportRow
{
    public required Guid Id { get; init; }
    public required ReportType Type { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double SortValue { get; init; }
    public DateOnly? ReportDate { get; init; }
    public string? TimestampMilliseconds => Timestamp?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        System.Globalization.CultureInfo.InvariantCulture);
    public string? StudentNo { get; init; }
    public string? CardNo { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Class { get; init; }
    public string? Department { get; init; }
    public string? Section { get; init; }
    public string? Job { get; init; }
    public string? MealType { get; init; }
    public string? Device { get; init; }
    public string? Decision { get; init; }
    public string? Status { get; init; }
    public string? Description { get; init; }
    public int MealCount { get; init; }
    // Tutar icerde kurus (long) olarak tutulur; kayan nokta yuvarlama hatasi olmasin diye.
    // JSON'a yalnizca "amount" yazilir, ancak bu alan salt-okunur (computed) kalirsa masaustu
    // istemci yaniti geri okurken AmountCents=0 kalir ve her satir 0,00 TL gorunurdu.
    // Bu yuzden Amount'a bir setter verildi: deserialize sirasinda gelen lirayi kurusa
    // cevirip tek dogruluk kaynagi olan AmountCents'e yaziyor.
    [System.Text.Json.Serialization.JsonIgnore]
    public long AmountCents { get; init; }
    public decimal Amount
    {
        get => AmountCents / 100m;
        init => AmountCents = (long)decimal.Round(value * 100m, MidpointRounding.AwayFromZero);
    }
}

public sealed record ReportSummary(int TotalRecords, int Passed, int Denied, long TotalMeals, decimal Amount);

public sealed record ReportResult(
    IReadOnlyList<ReportRow> Items,
    int Page,
    int PageSize,
    ReportSummary Summary);

public interface IReportRepository
{
    Task<ReportResult> QueryAsync(ReportType type, ReportQuery query, CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(
        ReportType type,
        ReportQuery query,
        int batchSize,
        CancellationToken cancellationToken);
}

public interface IPdfService
{
    Task GenerateAsync(
        ReportType type,
        ReportQuery query,
        Stream output,
        CancellationToken cancellationToken = default);
}

public interface IExcelService
{
    Task GenerateAsync(
        ReportType type,
        ReportQuery query,
        Stream output,
        CancellationToken cancellationToken = default);
}

public interface ICsvService
{
    Task GenerateAsync(
        ReportType type,
        ReportQuery query,
        Stream output,
        CancellationToken cancellationToken = default);
}
