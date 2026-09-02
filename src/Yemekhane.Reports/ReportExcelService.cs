using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Common;
using Yemekhane.Application.Reports;

namespace Yemekhane.Reports;

public sealed class ReportExcelService : IExcelService
{
    private const uint HeaderRow = 5;
    private const uint FirstDataRow = HeaderRow + 1;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private readonly ReportService reportService;
    private readonly ReportExcelOptions options;
    private readonly TimeProvider timeProvider;

    public ReportExcelService(ReportService reportService, IOptions<ReportExcelOptions> options,
        TimeProvider timeProvider)
    {
        this.reportService = reportService;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        if (this.options.BatchSize is < 1 or > ReportService.MaximumPageSize)
            throw new RequestValidationException(
                $"Excel batch boyutu 1-{ReportService.MaximumPageSize} aralığında olmalıdır.");
        if (this.options.MaximumRowsPerSheet is < (int)FirstDataRow + 1 or > ReportExcelOptions.ExcelMaximumRows)
            throw new RequestValidationException(
                $"Excel sheet satır sınırı {FirstDataRow + 1}-{ReportExcelOptions.ExcelMaximumRows} aralığında olmalıdır.");
        if (string.IsNullOrWhiteSpace(this.options.SchoolName))
            throw new RequestValidationException("Excel okul adı boş olamaz.");
    }

    public async Task GenerateAsync(ReportType type, ReportQuery query, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("Excel çıktı akışı yazılabilir olmalıdır.", nameof(output));

        var path = Path.Combine(Path.GetTempPath(), $"yemekhane-report-{Guid.NewGuid():N}.xlsx");
        await using var temporary = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        await GeneratePackageAsync(type, query, temporary, cancellationToken);
        temporary.Position = 0;
        await temporary.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private async Task GeneratePackageAsync(ReportType type, ReportQuery query, Stream output,
        CancellationToken cancellationToken)
    {
        var definition = DefinitionFor(type, query);
        var generatedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), Istanbul);
        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, true);
        var workbookPart = document.AddWorkbookPart();
        AddStyles(workbookPart);
        var sheets = new List<(string RelationshipId, string Name)>();
        SheetWriter? sheet = null;
        var capacity = options.MaximumRowsPerSheet - (int)HeaderRow - 1;

        await foreach (var batch in reportService.StreamBatchesAsync(type, query, options.BatchSize,
                           cancellationToken))
        {
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sheet is null || sheet.DataRows == capacity)
                {
                    sheet?.Dispose();
                    sheet = CreateSheet(workbookPart, definition, query, generatedAt);
                    sheets.Add((workbookPart.GetIdOfPart(sheet.Part), $"Rapor {sheets.Count + 1}"));
                }
                sheet.Write(row, definition.Columns);
            }
        }

        if (sheet is null)
        {
            sheet = CreateSheet(workbookPart, definition, query, generatedAt);
            sheets.Add((workbookPart.GetIdOfPart(sheet.Part), "Rapor 1"));
        }
        sheet.Dispose();

        workbookPart.Workbook = new Workbook(new Sheets(sheets.Select((value, index) => new Sheet
        {
            Id = value.RelationshipId,
            SheetId = (uint)index + 1,
            Name = value.Name
        })));
        workbookPart.Workbook.Save();
    }

    private SheetWriter CreateSheet(WorkbookPart workbookPart, ReportDefinition definition,
        ReportQuery query, DateTimeOffset generatedAt)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var writer = OpenXmlWriter.Create(part);
        writer.WriteStartElement(new Worksheet());
        writer.WriteElement(new SheetViews(new SheetView(
            new Pane { VerticalSplit = 5D, TopLeftCell = "A6", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen })
        { WorkbookViewId = 0U }));
        writer.WriteElement(new Columns(definition.Columns.Select((column, index) => new Column
        {
            Min = (uint)index + 1,
            Max = (uint)index + 1,
            Width = column.Width,
            CustomWidth = true
        })));
        writer.WriteStartElement(new SheetData());
        WriteRow(writer, 1, [TextCell("A1", $"{options.SchoolName} - {definition.Title}", 1)]);
        WriteRow(writer, 2, [TextCell("A2", FormatFilters(query), 2)]);
        WriteRow(writer, 3,
            [TextCell("A3", $"Oluşturulma: {generatedAt:dd.MM.yyyy HH:mm} Europe/Istanbul", 2)]);
        WriteRow(writer, HeaderRow, definition.Columns.Select((column, index) =>
            TextCell(Reference(index, HeaderRow), column.Title, 3)));
        return new SheetWriter(part, writer, definition.Columns, definition.Title == StudentListTitle);
    }

    private const string StudentListTitle = "Sicil Listesi";

    /// <summary>TC sutunu yalnizca yetkili sorguda yazilir; yetkisizde bos sutun bile birakilmaz.</summary>
    private static ReportDefinition DefinitionFor(ReportType type, ReportQuery query)
    {
        var definition = Definitions[type];
        return type == ReportType.StudentList && !query.IncludeSensitive
            ? definition with { Columns = definition.Columns.Where(x => x.Title != ReportCsvService.NationalIdHeader).ToArray() }
            : definition;
    }

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var part = workbookPart.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet = new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164, FormatCode = "dd.mm.yyyy" },
                new NumberingFormat { NumberFormatId = 165, FormatCode = "dd.mm.yyyy hh:mm:ss" },
                new NumberingFormat { NumberFormatId = 166, FormatCode = "#,##0.00" }) { Count = 3 },
            new Fonts(
                new Font(new FontSize { Val = 11D }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 14D }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11D }, new FontName { Val = "Calibri" })) { Count = 3 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDCE6F1" })
                { PatternType = PatternValues.Solid })) { Count = 3 },
            new Borders(new Border(), new Border(
                new LeftBorder { Style = BorderStyleValues.Thin },
                new RightBorder { Style = BorderStyleValues.Thin },
                new TopBorder { Style = BorderStyleValues.Thin },
                new BottomBorder { Style = BorderStyleValues.Thin },
                new DiagonalBorder())) { Count = 2 },
            new CellStyleFormats(new CellFormat()) { Count = 1 },
            new CellFormats(
                new CellFormat(),
                new CellFormat { FontId = 1, ApplyFont = true },
                new CellFormat { FontId = 0, ApplyFont = true },
                new CellFormat { FontId = 2, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true },
                new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 166, ApplyNumberFormat = true },
                new CellFormat { FontId = 2, ApplyFont = true }) { Count = 8 });
        part.Stylesheet.Save();
    }

    private static void WriteRow(OpenXmlWriter writer, uint index, IEnumerable<Cell> cells)
    {
        writer.WriteStartElement(new Row { RowIndex = index });
        foreach (var cell in cells) writer.WriteElement(cell);
        writer.WriteEndElement();
    }

    private static Cell TextCell(string reference, string? value, uint style = 0)
    {
        var text = ProtectText(value);
        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve })
        };
    }

    private static Cell NumberCell(string reference, decimal value, uint style = 0) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        StyleIndex = style,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
    };

    private static string ProtectText(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return text.Length <= 32_767 ? text : text[..32_767];
    }

    private static string Reference(int zeroBasedColumn, uint row) => $"{ColumnName(zeroBasedColumn)}{row}";

    private static string ColumnName(int zeroBasedColumn)
    {
        var value = zeroBasedColumn + 1;
        var name = string.Empty;
        while (value > 0)
        {
            value--;
            name = (char)('A' + value % 26) + name;
            value /= 26;
        }
        return name;
    }

    private static string FormatFilters(ReportQuery query)
    {
        var filters = new List<string>
        {
            $"Tarih aralığı={FormatDate(query.Start)} - {FormatDate(query.End)}"
        };
        Add("Öğrenci no", query.StudentNo);
        Add("Kart no", query.CardNo);
        Add("Ad", query.FirstName);
        Add("Soyad", query.LastName);
        Add("Sınıf", query.Class);
        Add("Bölüm", query.Department);
        Add("Şube", query.Section);
        Add("Görev", query.Job);
        Add("Öğün", query.MealType);
        Add("Cihaz", query.Device);
        Add("Karar", query.Decision);
        Add("Durum", query.Status);
        return "Filtreler: " + string.Join(" | ", filters);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) filters.Add($"{name}={value.Trim()}");
        }
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToString("dd.MM.yyyy HH:mm", Turkish) ?? "Tümü";

    private static object? Date(ReportRow row) => (object?)row.Timestamp ?? row.ReportDate;
    private static string Name(ReportRow row) => $"{row.FirstName} {row.LastName}".Trim();
    private static ReportColumn C(string title, ColumnKind kind, Func<ReportRow, object?> value, double width) =>
        new(title, kind, value, width);
    private static ReportDefinition Def(string title, params ReportColumn[] columns) => new(title, columns);

    private static readonly Dictionary<ReportType, ReportDefinition> Definitions = new()
    {
        [ReportType.DailyAccess] = Def("Günlük Geçiş Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Sınıf", ColumnKind.Text, x => x.Class, 12), C("Kart", ColumnKind.Text, x => x.CardNo, 16), C("Öğün", ColumnKind.Text, x => x.MealType, 14), C("Cihaz", ColumnKind.Text, x => x.Device, 20), C("Karar", ColumnKind.Text, x => ReportText.Decision(x.Decision), 14)),
        [ReportType.MealEntitlement] = Def("Yemek Hakediş Raporu", C("Tarih", ColumnKind.Date, Date, 14), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Sınıf", ColumnKind.Text, x => x.Class, 12), C("Öğün", ColumnKind.Text, x => x.MealType, 14), C("Adet", ColumnKind.Integer, x => x.MealCount, 10), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16)),
        [ReportType.StudentMealUsage] = Def("Öğrenci Yemek Kullanım Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Sınıf", ColumnKind.Text, x => x.Class, 12), C("Öğün", ColumnKind.Text, x => x.MealType, 14), C("Kart", ColumnKind.Text, x => x.CardNo, 16), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16)),
        [ReportType.ClassMeal] = Def("Sınıf Yemek Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Sınıf", ColumnKind.Text, x => x.Class, 12), C("Şube", ColumnKind.Text, x => x.Section, 12), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Öğün", ColumnKind.Text, x => x.MealType, 14), C("Adet", ColumnKind.Integer, x => x.MealCount, 10)),
        // Gunluk Kasa gun x gelir turu kirilimidir (EfReportRepository.DailyCash); ogrenci sutunlari bos gelir.
        [ReportType.DailyCash] = Def("Günlük Kasa Raporu", C("Tarih", ColumnKind.Date, Date, 14), C("Gelir Türü", ColumnKind.Text, x => ReportText.Description(x), 32), C("İşlem", ColumnKind.Integer, x => x.MealCount, 10), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16), C("Tutar", ColumnKind.Decimal, x => x.Amount, 14)),
        [ReportType.Income] = Def("Gelir Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Kart", ColumnKind.Text, x => x.CardNo, 16), C("Açıklama", ColumnKind.Text, x => ReportText.Description(x), 32), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16), C("Tutar", ColumnKind.Decimal, x => x.Amount, 14)),
        [ReportType.Sms] = Def("SMS Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Açıklama", ColumnKind.Text, x => ReportText.Description(x), 40), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16)),
        [ReportType.Turnstile] = Def("Turnike Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Kart", ColumnKind.Text, x => x.CardNo, 16), C("Cihaz", ColumnKind.Text, x => x.Device, 20), C("Karar", ColumnKind.Text, x => ReportText.Decision(x.Decision), 14), C("Sonuç", ColumnKind.Text, x => ReportText.Status(x), 16), C("Açıklama", ColumnKind.Text, x => ReportText.Description(x), 28)),
        [ReportType.DeniedAccess] = Def("Reddedilen Geçişler Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Kart", ColumnKind.Text, x => x.CardNo, 16), C("Öğün", ColumnKind.Text, x => x.MealType, 14), C("Cihaz", ColumnKind.Text, x => x.Device, 20), C("Neden", ColumnKind.Text, x => ReportText.Status(x), 24)),
        [ReportType.CardMovements] = Def("Kart Hareketleri Raporu", C("Tarih", ColumnKind.Date, Date, 20), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Sınıf", ColumnKind.Text, x => x.Class, 12), C("Kart", ColumnKind.Text, x => x.CardNo, 16), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16), C("Açıklama", ColumnKind.Text, x => ReportText.Description(x), 28)),
        [ReportType.HolidayTransfer] = Def("Tatil ve Aktarım Raporu", C("Tarih", ColumnKind.Date, Date, 14), C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad Soyad", ColumnKind.Text, Name, 24), C("Öğün", ColumnKind.Text, x => x.MealType, 14), C("Adet", ColumnKind.Integer, x => x.MealCount, 10), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 16), C("Açıklama", ColumnKind.Text, x => ReportText.Description(x), 32)),
        // Sicil Listesi: eski programin disa aktarimiyla ayni sira, ad/soyad ayri; TC en sonda (DefinitionFor yetkisizde kaldirir).
        [ReportType.StudentList] = Def(StudentListTitle, C("Öğrenci No", ColumnKind.Text, x => x.StudentNo, 14), C("Ad", ColumnKind.Text, x => x.FirstName, 18), C("Soyad", ColumnKind.Text, x => x.LastName, 18), C("Sınıf", ColumnKind.Text, x => x.Class, 10), C("Şube", ColumnKind.Text, x => x.Section, 10), C("Bölüm", ColumnKind.Text, x => x.Department, 16), C("Görev", ColumnKind.Text, x => x.Job, 14), C("Kart No", ColumnKind.Text, x => x.CardNo, 16), C("Veli", ColumnKind.Text, x => x.ParentName, 24), C("Veli Telefonu", ColumnKind.Text, x => x.ParentPhone, 16), C("Durum", ColumnKind.Text, x => ReportText.Status(x), 10), C("Kayıt Tarihi", ColumnKind.Date, x => (object?)x.ReportDate, 14), C(ReportCsvService.NationalIdHeader, ColumnKind.Text, x => x.NationalId, 16))
    };

    private sealed record ReportDefinition(string Title, IReadOnlyList<ReportColumn> Columns);
    private sealed record ReportColumn(string Title, ColumnKind Kind, Func<ReportRow, object?> Value, double Width);
    private enum ColumnKind { Text, Date, Integer, Decimal }

    private sealed class SheetWriter : IDisposable
    {
        private readonly OpenXmlWriter writer;
        private readonly IReadOnlyList<ReportColumn> columns;
        private readonly bool isStudentList;
        private bool disposed;
        private long meals;
        private decimal amount;
        private int passed;
        private int denied;

        public SheetWriter(WorksheetPart part, OpenXmlWriter writer, IReadOnlyList<ReportColumn> columns,
            bool isStudentList = false)
        {
            Part = part;
            this.writer = writer;
            this.columns = columns;
            this.isStudentList = isStudentList;
        }

        public WorksheetPart Part { get; }
        public int DataRows { get; private set; }

        public void Write(ReportRow row, IReadOnlyList<ReportColumn> columns)
        {
            var rowIndex = FirstDataRow + (uint)DataRows;
            WriteRow(writer, rowIndex, columns.Select((column, index) => CellFor(column, row, index, rowIndex)));
            DataRows++;
            meals += row.MealCount;
            amount += row.Amount;
            if (row.Decision == "ALLOW") passed++;
            if (row.Decision == "DENY") denied++;
        }

        public void Dispose()
        {
            if (disposed) return;
            var totalRow = FirstDataRow + (uint)DataRows;
            var cells = new List<Cell> { TextCell($"A{totalRow}", "Toplam", 7) };
            if (columns.Count > 1) cells.Add(NumberCell($"B{totalRow}", DataRows, 7));
            // Sicil Listesi'nde gecis sayisi yoktur; MealCount aktif ogrenciyi (1/0) tasir.
            if (columns.Count > 2)
                cells.Add(TextCell($"C{totalRow}", isStudentList
                    ? $"Aktif: {meals} | Pasif: {DataRows - meals}"
                    : $"Geçen: {passed} | Reddedilen: {denied}", 7));
            for (var index = 3; index < columns.Count; index++)
            {
                if (columns[index].Kind == ColumnKind.Integer)
                    cells.Add(NumberCell(Reference(index, totalRow), meals, 7));
                else if (columns[index].Kind == ColumnKind.Decimal)
                    cells.Add(NumberCell(Reference(index, totalRow), amount, 6));
            }
            writer.WriteStartElement(new Row { RowIndex = totalRow });
            foreach (var cell in cells) writer.WriteElement(cell);
            writer.WriteEndElement();
            writer.WriteEndElement();
            var lastDataRow = DataRows == 0 ? HeaderRow : FirstDataRow + (uint)DataRows - 1;
            writer.WriteElement(new AutoFilter { Reference = $"A{HeaderRow}:{ColumnName(columns.Count - 1)}{lastDataRow}" });
            writer.WriteEndElement();
            writer.Close();
            disposed = true;
        }

        private static Cell CellFor(ReportColumn column, ReportRow row, int index, uint rowIndex)
        {
            var reference = Reference(index, rowIndex);
            var value = column.Value(row);
            return column.Kind switch
            {
                ColumnKind.Integer => NumberCell(reference, Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
                ColumnKind.Decimal => NumberCell(reference, Convert.ToDecimal(value, CultureInfo.InvariantCulture), 6),
                ColumnKind.Date when value is DateTimeOffset timestamp =>
                    NumberCell(reference, (decimal)TimeZoneInfo.ConvertTime(timestamp, Istanbul).DateTime.ToOADate(), 5),
                ColumnKind.Date when value is DateOnly date =>
                    NumberCell(reference, (decimal)date.ToDateTime(TimeOnly.MinValue).ToOADate(), 4),
                _ => TextCell(reference, Convert.ToString(value, Turkish))
            };
        }
    }
}
