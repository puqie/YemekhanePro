using System.Runtime.CompilerServices;
using Yemekhane.Application.Common;
using Yemekhane.Application.Reports;

namespace Yemekhane.Reports;

public sealed class ReportService(IReportRepository repository)
{
    public const int MaximumPageSize = 200;
    public static readonly IReadOnlySet<string> SortColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "studentNo", "cardNo", "firstName", "lastName", "class", "department", "section",
        "job", "mealType", "device", "decision", "status", "mealCount", "amount"
    };

    public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query,
        CancellationToken cancellationToken = default) =>
        repository.QueryAsync(type, Validate(query), cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(
        ReportType type,
        ReportQuery query,
        int batchSize = MaximumPageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaximumPageSize)
            throw new RequestValidationException($"Batch size 1-{MaximumPageSize} aralığında olmalıdır.");

        var validated = Validate(query) with { Page = 1, PageSize = MaximumPageSize };
        await foreach (var batch in repository.StreamBatchesAsync(type, validated, batchSize, cancellationToken))
            yield return batch;
    }

    private static ReportQuery Validate(ReportQuery query)
    {
        if (query.Page < 1) throw new RequestValidationException("Sayfa numarası en az 1 olmalıdır.");
        if (query.PageSize is < 1 or > MaximumPageSize)
            throw new RequestValidationException($"Sayfa boyutu 1-{MaximumPageSize} aralığında olmalıdır.");
        if (query.Start > query.End) throw new RequestValidationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
        if (!SortColumns.Contains(query.SortBy))
            throw new RequestValidationException($"Desteklenmeyen sıralama alanı: {query.SortBy}");
        return query;
    }
}
