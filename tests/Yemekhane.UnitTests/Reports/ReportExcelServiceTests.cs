using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

public sealed class ReportExcelServiceTests
{
    public static TheoryData<ReportType> AllReports => new(Enum.GetValues<ReportType>());

    [Theory]
    [MemberData(nameof(AllReports))]
    public async Task EveryReportTypeProducesAValidWorkbook(ReportType type)
    {
        var repository = new ExcelRepository(CreateRows(type, 1));
        var service = CreateService(repository);
        await using var output = new MemoryStream();

        await service.GenerateAsync(type, new ReportQuery(), output);

        using var document = SpreadsheetDocument.Open(output, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Workbook part missing.");
        Assert.Single(workbookPart.Workbook!.Sheets!.Elements<Sheet>());
    }

    [Fact]
    public async Task WorkbookPreservesTurkishTextTypedCellsTotalsFiltersAndFormulaSafetyWhileStreaming()
    {
        // Gelir raporu: islem islem 7 sutunlu para duzeni (Gunluk Kasa artik gun x tur kirilimidir, 5 sutun).
        var rows = CreateRows(ReportType.Income, 5);
        rows[0] = rows[0] with { Description = "=2+2" };
        var repository = new ExcelRepository(rows);
        var service = CreateService(repository, batchSize: 2);
        var query = new ReportQuery(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.FromHours(3)),
            FirstName: "ÇĞİÖŞÜ", Class: "10-Ş", Status: "Aktif");
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.Income, query, output);

        using var document = SpreadsheetDocument.Open(output, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Workbook part missing.");
        var worksheet = workbookPart.WorksheetParts.Single().Worksheet!;
        var cells = worksheet.Descendants<Cell>().ToDictionary(x => x.CellReference!.Value!);
        Assert.Contains("ÇĞİÖŞÜ Anadolu Lisesi", CellText(cells["A1"]));
        Assert.Contains("Ad=ÇĞİÖŞÜ", CellText(cells["A2"]));
        Assert.Contains("Europe/Istanbul", CellText(cells["A3"]));
        Assert.Equal(CellValues.Number, cells["A6"].DataType!.Value);
        Assert.Equal(5U, cells["A6"].StyleIndex!.Value);
        Assert.Equal(CellValues.Number, cells["G6"].DataType!.Value);
        Assert.Equal(6U, cells["G6"].StyleIndex!.Value);
        Assert.Equal("'=2+2", CellText(cells["E6"]));
        Assert.Null(cells["E6"].CellFormula);
        Assert.Equal("5", cells["B11"].CellValue!.Text);
        Assert.Equal("62.5", cells["G11"].CellValue!.Text);
        Assert.Equal("A5:G10", worksheet.GetFirstChild<AutoFilter>()!.Reference!.Value);
        Assert.Equal(PaneStateValues.Frozen, worksheet.Descendants<Pane>().Single().State!.Value);
        Assert.Equal(2, repository.MaximumYieldedBatchSize);
        Assert.Equal(query with { Page = 1, PageSize = ReportService.MaximumPageSize }, repository.StreamQuery);
    }

    [Fact]
    public async Task RowLimitSplitsDataAcrossSheetsWithoutBufferingAllRows()
    {
        var repository = new ExcelRepository(CreateRows(ReportType.DailyAccess, 5));
        var service = CreateService(repository, batchSize: 2, maximumRows: 8);
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.DailyAccess, new ReportQuery(), output);

        using var document = SpreadsheetDocument.Open(output, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Workbook part missing.");
        Assert.Equal(3, workbookPart.Workbook!.Sheets!.Count());
        Assert.Equal([2, 2, 1], workbookPart.WorksheetParts
            .Select(x => (x.Worksheet!.GetFirstChild<SheetData>() ?? throw new InvalidOperationException("Sheet data missing."))
                .Elements<Row>().Count(row => row.RowIndex!.Value >= 6 &&
                CellText(row.Elements<Cell>().First()) != "Toplam"))
            .ToArray());
        Assert.All(workbookPart.WorksheetParts, part => Assert.True(
            (part.Worksheet!.GetFirstChild<SheetData>() ?? throw new InvalidOperationException("Sheet data missing."))
            .Elements<Row>().Max(x => x.RowIndex!.Value) <= 8));
        Assert.Equal(2, repository.MaximumYieldedBatchSize);
    }

    /// <summary>
    /// Kullanici Ayarlar > Okul'a gercek okul adini yazip kaydediyordu ama rapor basligi
    /// appsettings.json'daki sabit adi tasidigi icin degismiyordu. Kayitli ad artik onceliklidir;
    /// kayit yoksa/bossa yapilandirmadaki ad yedek kalir.
    /// </summary>
    [Theory]
    [InlineData("Şehit Öğretmen Ortaokulu", "Şehit Öğretmen Ortaokulu")]
    [InlineData("   ", "ÇĞİÖŞÜ Anadolu Lisesi")]
    [InlineData(null, "ÇĞİÖŞÜ Anadolu Lisesi")]
    public async Task BaslikKayitliOkulAdiniKullanir(string? saved, string expected)
    {
        var repository = new ExcelRepository(CreateRows(ReportType.DailyAccess, 1));
        var service = new ReportExcelService(new ReportService(repository),
            Options.Create(new ReportExcelOptions { SchoolName = "ÇĞİÖŞÜ Anadolu Lisesi" }),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 34, 0, TimeSpan.Zero)),
            new StubBranding(saved));
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.DailyAccess, new ReportQuery(), output);

        Assert.Contains(expected, FirstCellText(output));
    }

    /// <summary>Ad okunamazsa rapor uretimi durmaz; yapilandirmadaki ad ile devam eder.</summary>
    [Fact]
    public async Task OkulAdiOkunamazsaRaporYineUretilir()
    {
        var repository = new ExcelRepository(CreateRows(ReportType.DailyAccess, 1));
        var service = new ReportExcelService(new ReportService(repository),
            Options.Create(new ReportExcelOptions { SchoolName = "ÇĞİÖŞÜ Anadolu Lisesi" }),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 34, 0, TimeSpan.Zero)),
            new ThrowingBranding());
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.DailyAccess, new ReportQuery(), output);

        Assert.Contains("ÇĞİÖŞÜ Anadolu Lisesi", FirstCellText(output));
    }

    private static string FirstCellText(MemoryStream output)
    {
        output.Position = 0;
        using var document = SpreadsheetDocument.Open(output, false);
        var part = document.WorkbookPart!.WorksheetParts.First();
        var sheetData = part.Worksheet!.GetFirstChild<SheetData>()!;
        var cell = sheetData.Elements<Row>().First().Elements<Cell>().First();
        return cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? "";
    }

    private sealed class StubBranding(string? value) : IReportBrandingProvider
    {
        public Task<string?> SchoolNameAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    }

    private sealed class ThrowingBranding : IReportBrandingProvider
    {
        public Task<string?> SchoolNameAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("veritabanı okunamadı");
    }

    private static ReportExcelService CreateService(ExcelRepository repository, int batchSize = 200,
        int maximumRows = ReportExcelOptions.ExcelMaximumRows) =>
        new(new ReportService(repository), Options.Create(new ReportExcelOptions
        {
            SchoolName = "ÇĞİÖŞÜ Anadolu Lisesi",
            BatchSize = batchSize,
            MaximumRowsPerSheet = maximumRows
        }), new FixedTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 34, 0, TimeSpan.Zero)));

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

    private static string CellText(Cell cell) => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty;

    private sealed class ExcelRepository(IReadOnlyList<ReportRow> rows) : IReportRepository
    {
        public ReportQuery? StreamQuery { get; private set; }
        public int MaximumYieldedBatchSize { get; private set; }

        public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Excel export must stream rows.");

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
