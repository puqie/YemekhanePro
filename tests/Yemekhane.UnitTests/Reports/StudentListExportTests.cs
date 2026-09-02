using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// Sicil Listesi disa aktarimi: olay raporlarinin ortak 16 sutunu (ogun, cihaz, tutar...) yerine
/// kisi sutunlari; ad ve soyad AYRI; TC yalnizca yetkili sorguda ve yetkisizde sutun bile yok.
/// </summary>
public sealed class StudentListExportTests
{
    private static readonly string[] Expected =
        ["Öğrenci No", "Ad", "Soyad", "Sınıf", "Şube", "Bölüm", "Görev", "Kart No", "Veli", "Veli Telefonu", "Durum", "Kayıt Tarihi"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsvWritesStudentColumnsAndNationalIdOnlyWhenGranted(bool sensitive)
    {
        var service = new ReportCsvService(new ReportService(new Repository(Rows())));
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.StudentList, new ReportQuery(IncludeSensitive: sensitive), output);

        var lines = Encoding.UTF8.GetString(output.ToArray()).TrimStart('﻿').Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(';').Select(x => x.Trim('"')).ToArray();
        Assert.Equal(sensitive ? [.. Expected, "TC Kimlik No"] : Expected, header);
        Assert.Equal(3, lines.Length);
        var ada = lines[1].Split(';').Select(x => x.Trim('"')).ToArray();
        Assert.Equal(["5001", "ADA", "YILMAZ", "6A", "B", "", "", "8350001", "YILMAZ VELİSİ", "05321234567", "Aktif", "15.09.2025"], ada.Take(12));
        if (sensitive) Assert.Equal("12345678901", ada[12]);
        Assert.Contains("Pasif", lines[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcelHeaderRowMatchesTheOldProgramAndTotalsShowActiveCounts(bool sensitive)
    {
        var service = new ReportExcelService(new ReportService(new Repository(Rows())),
            Options.Create(new ReportExcelOptions { SchoolName = "Test Okulu", BatchSize = 50, MaximumRowsPerSheet = 1000 }),
            TimeProvider.System);
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.StudentList, new ReportQuery(IncludeSensitive: sensitive), output);

        using var document = SpreadsheetDocument.Open(output, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var rows = sheet.Descendants<Row>().ToDictionary(x => x.RowIndex!.Value);
        Assert.Contains("Sicil Listesi", Text(rows[1].Elements<Cell>().First()));
        var header = rows[5].Elements<Cell>().Select(Text).ToArray();
        Assert.Equal(sensitive ? [.. Expected, "TC Kimlik No"] : Expected, header);
        var total = rows[8].Elements<Cell>().Select(Text).ToArray();
        Assert.Equal("Toplam", total[0]);
        Assert.Equal("Aktif: 1 | Pasif: 1", total[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PdfIsLandscapeWithTurkishHeadersAndStudentSummary(bool sensitive)
    {
        var service = new ReportPdfService(new ReportService(new Repository(Rows())),
            Options.Create(new ReportPdfOptions { SchoolName = "Test Okulu" }), TimeProvider.System);
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.StudentList, new ReportQuery(IncludeSensitive: sensitive), output);

        using var pdf = PdfDocument.Open(output.ToArray());
        var page = pdf.GetPage(1);
        Assert.True(page.Width > page.Height, "12+ sütun yatay sayfa ister");
        var text = string.Join(" ", page.GetWords().Select(x => x.Text));
        Assert.Contains("Sicil Listesi", text);
        Assert.Contains("Veli Telefonu", text);
        Assert.Contains("Toplam öğrenci: 2 | Aktif: 1 | Pasif: 1", text);
        Assert.DoesNotContain("Geçen:", text);
        Assert.Equal(sensitive, text.Contains("TC Kimlik", StringComparison.Ordinal));
        Assert.Equal(sensitive, text.Contains("12345678901", StringComparison.Ordinal));
    }

    private static ReportRow[] Rows() =>
    [
        new()
        {
            Id = Guid.NewGuid(), Type = ReportType.StudentList, StudentNo = "5001", FirstName = "ADA", LastName = "YILMAZ",
            Class = "6A", Section = "B", CardNo = "8350001", ParentName = "YILMAZ VELİSİ", ParentPhone = "05321234567",
            Status = "ACTIVE", ReportDate = new DateOnly(2025, 9, 15), NationalId = "12345678901", MealCount = 1
        },
        new()
        {
            Id = Guid.NewGuid(), Type = ReportType.StudentList, StudentNo = "5003", FirstName = "ADA", LastName = "DEMİR",
            Class = "5A", Section = "A", CardNo = "8350003", Status = "INACTIVE", ReportDate = new DateOnly(2026, 9, 2), MealCount = 0
        }
    ];

    private static string Text(Cell cell) => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? "";

    /// <summary>Sunucu yetkisiz sorguda TC'yi hic uretmez; sahte depo bu davranisi taklit eder.</summary>
    private sealed class Repository(IReadOnlyList<ReportRow> rows) : IReportRepository
    {
        public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new ReportResult([], query.Page, query.PageSize,
                new ReportSummary(rows.Count, 0, 0, rows.Sum(x => (long)x.MealCount), 0m)));

        public async IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(ReportType type, ReportQuery query, int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return rows.Select(x => query.IncludeSensitive ? x : x with { NationalId = null }).ToArray();
        }
    }
}
