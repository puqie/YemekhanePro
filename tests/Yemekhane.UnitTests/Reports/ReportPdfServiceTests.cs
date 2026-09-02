using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

public sealed class ReportPdfServiceTests
{
    public static TheoryData<ReportType> AllReports => new(Enum.GetValues<ReportType>());

    [Theory]
    [MemberData(nameof(AllReports))]
    public async Task EveryReportTypeProducesAParserReadablePdfWithExpectedOrientation(ReportType type)
    {
        var repository = new PdfRepository(CreateRows(type, 1));
        var service = CreateService(repository);
        await using var output = new MemoryStream();

        await service.GenerateAsync(type, new ReportQuery(), output);

        Assert.True(output.CanWrite);
        using var pdf = PdfDocument.Open(output.ToArray());
        Assert.Equal(1, pdf.NumberOfPages);
        var page = pdf.GetPage(1);
        // Gunluk Kasa gun x gelir turu kirilimi oldugu icin 5 sutunludur ve dikey sayfaya sigar.
        var landscape = type is ReportType.DailyAccess or ReportType.MealEntitlement
            or ReportType.StudentMealUsage or ReportType.ClassMeal
            or ReportType.Income or ReportType.Turnstile or ReportType.DeniedAccess
            or ReportType.CardMovements or ReportType.HolidayTransfer;
        Assert.Equal(landscape, page.Width > page.Height);
    }

    [Fact]
    public async Task MultiPagePdfEmbedsTurkishTextAndRepeatsHeadersFootersAndPageNumbers()
    {
        var rows = CreateRows(ReportType.DailyAccess, 120);
        var repository = new PdfRepository(rows);
        var service = CreateService(repository, batchSize: 17);
        await using var output = new MemoryStream();
        var query = new ReportQuery(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.FromHours(3)),
            FirstName: "ÇĞİÖŞÜ", Class: "10-Ş", Decision: "ALLOW");

        await service.GenerateAsync(ReportType.DailyAccess, query, output);

        using var pdf = PdfDocument.Open(output.ToArray());
        Assert.True(pdf.NumberOfPages > 1);
        for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
        {
            var text = ExtractText(pdf.GetPage(pageNumber));
            Assert.Contains("Öğrenci No", text);
            Assert.Contains($"Sayfa {pageNumber}/{pdf.NumberOfPages}", text);
            Assert.Contains("Europe/Istanbul", text);
        }

        var allText = string.Join(" ", Enumerable.Range(1, pdf.NumberOfPages)
            .Select(x => ExtractText(pdf.GetPage(x))));
        Assert.Contains("ÇĞİÖŞÜ", allText);
        Assert.Contains("Aktif filtreler:", allText);
        Assert.Contains("Ad=ÇĞİÖŞÜ", allText);
        Assert.Contains("Toplam kayıt: 120", allText);
        Assert.Contains("Geçen: 120", allText);
        Assert.Contains("Öğün: 120", allText);
        Assert.Contains("Tutar: 1.500,00 TL", allText);
        Assert.Equal(17, repository.MaximumYieldedBatchSize);
        Assert.Equal(query with { Page = 1, PageSize = 1 }, repository.SummaryQuery);
        Assert.Equal(query with { Page = 1, PageSize = ReportService.MaximumPageSize }, repository.StreamQuery);
    }

    private static ReportPdfService CreateService(PdfRepository repository, int batchSize = 200) =>
        new(new ReportService(repository),
            Options.Create(new ReportPdfOptions { SchoolName = "ÇĞİÖŞÜ Anadolu Lisesi", BatchSize = batchSize }),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 34, 0, TimeSpan.Zero)));

    private static ReportRow[] CreateRows(ReportType type, int count) =>
        Enumerable.Range(1, count).Select(index => new ReportRow
        {
            Id = Guid.NewGuid(), Type = type,
            Timestamp = new DateTimeOffset(2026, 8, 31, 12, 30, 0, TimeSpan.FromHours(3)).AddMinutes(index),
            ReportDate = new DateOnly(2026, 8, 31), StudentNo = $"{index:0000}", CardNo = $"K-{index:0000}",
            FirstName = "ÇĞİÖŞÜ", LastName = "Yılmaz", Class = "10-Ş", Department = "Bölüm",
            Section = "Şube", Job = "Öğrenci", MealType = "Öğle", Device = "Giriş Turnikesi",
            Decision = "ALLOW", Status = "Aktif", Description = "Türkçe açıklama", MealCount = 1,
            AmountCents = 1_250
        }).ToArray();

    private static string ExtractText(UglyToad.PdfPig.Content.Page page) =>
        string.Join(" ", page.GetWords().Select(x => x.Text));

    private sealed class PdfRepository(IReadOnlyList<ReportRow> rows) : IReportRepository
    {
        public ReportQuery? SummaryQuery { get; private set; }
        public ReportQuery? StreamQuery { get; private set; }
        public int MaximumYieldedBatchSize { get; private set; }

        public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query,
            CancellationToken cancellationToken)
        {
            SummaryQuery = query;
            var summary = new ReportSummary(rows.Count, rows.Count(x => x.Decision == "ALLOW"),
                rows.Count(x => x.Decision == "DENY"), rows.Sum(x => (long)x.MealCount), rows.Sum(x => x.Amount));
            return Task.FromResult(new ReportResult([], query.Page, query.PageSize, summary));
        }

        public async IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(
            ReportType type, ReportQuery query, int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamQuery = query;
            for (var index = 0; index < rows.Count; index += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = rows.Skip(index).Take(batchSize).ToArray();
                MaximumYieldedBatchSize = Math.Max(MaximumYieldedBatchSize, batch.Length);
                yield return batch;
                await Task.Yield();
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
