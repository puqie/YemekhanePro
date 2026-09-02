using System.Globalization;
using Microsoft.Extensions.Options;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Yemekhane.Application.Common;
using Yemekhane.Application.Reports;

namespace Yemekhane.Reports;

public sealed class ReportPdfService : IPdfService
{
    private const double Margin = 32;
    private const double RowHeight = 18;
    private const double FooterHeight = 24;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly object FontLock = new();
    private readonly ReportService reportService;
    private readonly ReportPdfOptions options;
    private readonly TimeProvider timeProvider;
    // Ayarlar > Okul'da kaydedilen ad; yoksa (veya saglayici verilmemisse) options.SchoolName kullanilir.
    private readonly IReportBrandingProvider? branding;

    public ReportPdfService(ReportService reportService, IOptions<ReportPdfOptions> options, TimeProvider timeProvider)
        : this(reportService, options, timeProvider, null)
    {
    }

    public ReportPdfService(ReportService reportService, IOptions<ReportPdfOptions> options, TimeProvider timeProvider,
        IReportBrandingProvider? branding)
    {
        this.reportService = reportService;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.branding = branding;
        if (this.options.BatchSize is < 1 or > ReportPdfOptions.MaximumBatchSize)
            throw new RequestValidationException(
                $"PDF batch boyutu 1-{ReportPdfOptions.MaximumBatchSize} aralığında olmalıdır.");
        if (string.IsNullOrWhiteSpace(this.options.SchoolName))
            throw new RequestValidationException("PDF okul adı boş olamaz.");
        EnsureFontResolver();
    }

    public async Task GenerateAsync(ReportType type, ReportQuery query, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("PDF çıktı akışı yazılabilir olmalıdır.", nameof(output));

        var definition = DefinitionFor(type, query);
        var summary = (await reportService.QueryAsync(type, query with { Page = 1, PageSize = 1 }, cancellationToken))
            .Summary;
        var generatedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), Istanbul);
        var schoolName = await ResolveSchoolNameAsync(cancellationToken);
        using var document = new PdfDocument();
        document.Info.Title = $"{schoolName} - {definition.Title}";
        document.Info.Author = schoolName;
        document.Info.Subject = FormatFilters(query);

        PdfPage? page = null;
        XGraphics? graphics = null;
        var y = 0d;

        void NewPage(bool firstPage)
        {
            graphics?.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            page.Orientation = definition.Columns.Length > 6
                ? PdfSharp.PageOrientation.Landscape
                : PdfSharp.PageOrientation.Portrait;
            graphics = XGraphics.FromPdfPage(page);
            y = DrawPageHeading(graphics, page, definition, query, summary, firstPage, schoolName);
            DrawTableHeader(graphics, page, definition.Columns, y);
            y += RowHeight;
        }

        NewPage(true);
        await foreach (var batch in reportService.StreamBatchesAsync(type, query, options.BatchSize, cancellationToken))
        {
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (y + RowHeight > page!.Height.Point - Margin - FooterHeight)
                    NewPage(false);
                DrawRow(graphics!, page!, definition.Columns, row, y);
                y += RowHeight;
            }
        }
        graphics?.Dispose();

        DrawFooters(document, generatedAt);
        await SaveAsync(document, output, cancellationToken);
    }

    /// <summary>
    /// Once Ayarlar'da kaydedilen okul adi denenir; satir yoksa yapilandirmadaki ad kullanilir.
    /// Ad okunamazsa rapor uretimi durdurulmaz: baslikta yedek ad ile devam eder.
    /// </summary>
    private async Task<string> ResolveSchoolNameAsync(CancellationToken cancellationToken)
    {
        if (branding is null) return options.SchoolName;
        try
        {
            var saved = await branding.SchoolNameAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(saved) ? options.SchoolName : saved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return options.SchoolName;
        }
    }

    private static double DrawPageHeading(XGraphics graphics, PdfPage page, ReportDefinition definition,
        ReportQuery query, ReportSummary summary, bool firstPage, string schoolName)
    {
        var width = page.Width.Point - 2 * Margin;
        var y = Margin;
        graphics.DrawString(schoolName, Font(14, true), XBrushes.Black,
            new XRect(Margin, y, width, 20), XStringFormats.TopCenter);
        y += 22;
        graphics.DrawString(definition.Title, Font(12, true), XBrushes.Black,
            new XRect(Margin, y, width, 18), XStringFormats.TopCenter);
        y += 24;

        if (firstPage)
        {
            y = DrawWrappedText(graphics, FormatDateRange(query), Font(8), y, width);
            y = DrawWrappedText(graphics, FormatFilters(query), Font(8), y, width);
            y = DrawWrappedText(graphics, FormatSummary(definition, summary), Font(8, true), y, width) + 5;
        }

        return y;
    }

    private static double DrawWrappedText(XGraphics graphics, string text, XFont font, double y, double width)
    {
        const double lineHeight = 13;
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length > 0 && graphics.MeasureString(candidate, font).Width > width)
            {
                graphics.DrawString(line, font, XBrushes.Black,
                    new XRect(Margin, y, width, lineHeight), XStringFormats.TopLeft);
                y += lineHeight;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        graphics.DrawString(line, font, XBrushes.Black,
            new XRect(Margin, y, width, lineHeight), XStringFormats.TopLeft);
        return y + lineHeight;
    }

    private static void DrawTableHeader(XGraphics graphics, PdfPage page, IReadOnlyList<ReportColumn> columns,
        double y)
    {
        var widths = ColumnWidths(page, columns);
        var x = Margin;
        for (var i = 0; i < columns.Count; i++)
        {
            graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(224, 231, 239)), x, y, widths[i], RowHeight);
            graphics.DrawRectangle(XPens.DarkSlateGray, x, y, widths[i], RowHeight);
            DrawCellText(graphics, columns[i].Title, Font(7, true), x, y, widths[i]);
            x += widths[i];
        }
    }

    private static void DrawRow(XGraphics graphics, PdfPage page, IReadOnlyList<ReportColumn> columns,
        ReportRow row, double y)
    {
        var widths = ColumnWidths(page, columns);
        var x = Margin;
        for (var i = 0; i < columns.Count; i++)
        {
            graphics.DrawRectangle(XPens.LightGray, x, y, widths[i], RowHeight);
            DrawCellText(graphics, columns[i].Value(row), Font(7), x, y, widths[i]);
            x += widths[i];
        }
    }

    private static void DrawCellText(XGraphics graphics, string? value, XFont font, double x, double y,
        double width)
    {
        var text = value ?? string.Empty;
        var available = Math.Max(0, width - 6);
        if (graphics.MeasureString(text, font).Width > available)
        {
            const string suffix = "…";
            while (text.Length > 0 && graphics.MeasureString(text + suffix, font).Width > available)
                text = text[..^1];
            text += suffix;
        }
        graphics.DrawString(text, font, XBrushes.Black,
            new XRect(x + 3, y + 3, available, RowHeight - 5), XStringFormats.TopLeft);
    }

    private static void DrawFooters(PdfDocument document, DateTimeOffset generatedAt)
    {
        var total = document.PageCount;
        for (var index = 0; index < total; index++)
        {
            var page = document.Pages[index];
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var y = page.Height.Point - Margin;
            graphics.DrawLine(XPens.LightGray, Margin, y - 5, page.Width.Point - Margin, y - 5);
            graphics.DrawString($"Oluşturulma: {generatedAt:dd.MM.yyyy HH:mm} Europe/Istanbul", Font(7),
                XBrushes.DimGray, new XRect(Margin, y, page.Width.Point / 2, 12), XStringFormats.TopLeft);
            graphics.DrawString($"Sayfa {index + 1}/{total}", Font(7, true), XBrushes.DimGray,
                new XRect(page.Width.Point / 2, y, page.Width.Point / 2 - Margin, 12), XStringFormats.TopRight);
        }
    }

    private static async Task SaveAsync(PdfDocument document, Stream output, CancellationToken cancellationToken)
    {
        if (output.CanSeek)
        {
            document.Save(output, false);
            await output.FlushAsync(cancellationToken);
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"yemekhane-report-{Guid.NewGuid():N}.pdf");
        await using var temporary = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        document.Save(temporary, false);
        temporary.Position = 0;
        await temporary.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static double[] ColumnWidths(PdfPage page, IReadOnlyList<ReportColumn> columns)
    {
        var available = page.Width.Point - 2 * Margin;
        var totalWeight = columns.Sum(x => x.Weight);
        return columns.Select(x => available * x.Weight / totalWeight).ToArray();
    }

    private static string FormatDateRange(ReportQuery query) =>
        $"Tarih aralığı: {FormatDate(query.Start)} - {FormatDate(query.End)}";

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToString("dd.MM.yyyy HH:mm", Turkish) ?? "Tümü";

    private static string FormatFilters(ReportQuery query)
    {
        var filters = new List<string>();
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
        return filters.Count == 0 ? "Aktif filtreler: Yok" : "Aktif filtreler: " + string.Join(" | ", filters);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) filters.Add($"{name}={value.Trim()}");
        }
    }

    private static string FormatSummary(ReportDefinition definition, ReportSummary summary) =>
        definition.Title == StudentListTitle
            // Sicil Listesi'nde gecis/ogun/tutar yoktur; TotalMeals aktif ogrenci sayisini tasir.
            ? $"Toplam öğrenci: {summary.TotalRecords:N0} | Aktif: {summary.TotalMeals:N0} | Pasif: {summary.TotalRecords - summary.TotalMeals:N0}"
            : $"Toplam kayıt: {summary.TotalRecords:N0} | Geçen: {summary.Passed:N0} | Reddedilen: {summary.Denied:N0} | " +
              $"Öğün: {summary.TotalMeals:N0} | Tutar: {summary.Amount.ToString("N2", Turkish)} TL";

    private const string StudentListTitle = "Sicil Listesi";

    /// <summary>TC sutunu yalnizca yetkili sorguda cizilir; yetkisizde bos sutun bile birakilmaz.</summary>
    private static ReportDefinition DefinitionFor(ReportType type, ReportQuery query)
    {
        var definition = Definitions[type];
        return type == ReportType.StudentList && !query.IncludeSensitive
            ? definition with { Columns = definition.Columns.Where(x => x.Title != ReportCsvService.NationalIdHeader).ToArray() }
            : definition;
    }

    private static XFont Font(double size, bool bold = false) =>
        new(NotoSansFontResolver.FamilyName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static void EnsureFontResolver()
    {
        lock (FontLock)
            GlobalFontSettings.FontResolver ??= new NotoSansFontResolver();
    }

    private static string Date(ReportRow row) =>
        row.Timestamp?.ToString("dd.MM.yyyy HH:mm:ss", Turkish)
        ?? row.ReportDate?.ToString("dd.MM.yyyy", Turkish)
        ?? string.Empty;

    private static string Name(ReportRow row) => $"{row.FirstName} {row.LastName}".Trim();
    private static string Amount(ReportRow row) => row.Amount.ToString("N2", Turkish);

    private static readonly Dictionary<ReportType, ReportDefinition> Definitions =
        new Dictionary<ReportType, ReportDefinition>
        {
            [ReportType.DailyAccess] = Def("Günlük Geçiş Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.4), C("Sınıf", x => x.Class), C("Kart", x => x.CardNo), C("Öğün", x => x.MealType), C("Cihaz", x => x.Device, 1.2), C("Karar", x => ReportText.Decision(x.Decision))),
            [ReportType.MealEntitlement] = Def("Yemek Hakediş Raporu", C("Tarih", Date), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.5), C("Sınıf", x => x.Class), C("Öğün", x => x.MealType), C("Adet", x => x.MealCount.ToString(Turkish), .6), C("Durum", x => ReportText.Status(x))),
            [ReportType.StudentMealUsage] = Def("Öğrenci Yemek Kullanım Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.5), C("Sınıf", x => x.Class), C("Öğün", x => x.MealType), C("Kart", x => x.CardNo), C("Durum", x => ReportText.Status(x))),
            [ReportType.ClassMeal] = Def("Sınıf Yemek Raporu", C("Tarih", Date, 1.3), C("Sınıf", x => x.Class), C("Şube", x => x.Section), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.5), C("Öğün", x => x.MealType), C("Adet", x => x.MealCount.ToString(Turkish), .6)),
            // Gunluk Kasa gun x gelir turu kirilimidir (EfReportRepository.DailyCash); ogrenci sutunlari bos gelir.
            [ReportType.DailyCash] = Def("Günlük Kasa Raporu", C("Tarih", Date), C("Gelir Türü", x => ReportText.Description(x), 2), C("İşlem", x => x.MealCount.ToString(Turkish), .7), C("Durum", x => ReportText.Status(x)), C("Tutar", Amount)),
            [ReportType.Income] = Def("Gelir Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.4), C("Kart", x => x.CardNo), C("Açıklama", x => ReportText.Description(x), 1.7), C("Durum", x => ReportText.Status(x)), C("Tutar", Amount)),
            [ReportType.Sms] = Def("SMS Raporu", C("Tarih", Date, 1.2), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.4), C("Açıklama", x => ReportText.Description(x), 2.2), C("Durum", x => ReportText.Status(x))),
            [ReportType.Turnstile] = Def("Turnike Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.4), C("Kart", x => x.CardNo), C("Cihaz", x => x.Device), C("Karar", x => ReportText.Decision(x.Decision)), C("Sonuç", x => ReportText.Status(x)), C("Açıklama", x => ReportText.Description(x), 1.5)),
            [ReportType.DeniedAccess] = Def("Reddedilen Geçişler Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.4), C("Kart", x => x.CardNo), C("Öğün", x => x.MealType), C("Cihaz", x => x.Device), C("Neden", x => ReportText.Status(x), 1.4)),
            [ReportType.CardMovements] = Def("Kart Hareketleri Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.5), C("Sınıf", x => x.Class), C("Kart", x => x.CardNo), C("Durum", x => ReportText.Status(x)), C("Açıklama", x => ReportText.Description(x), 1.4)),
            [ReportType.Balance] = Def("Bakiye Hareketleri Raporu", C("Tarih", Date, 1.3), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.5), C("Sınıf", x => x.Class, .6), C("Şube", x => x.Section, .6), C("Hareket", x => ReportText.Status(x)), C("Tutar", Amount, .9), C("Açıklama", x => x.Description, 1.4)),
            [ReportType.HolidayTransfer] = Def("Tatil ve Aktarım Raporu", C("Tarih", Date), C("Öğrenci No", x => x.StudentNo), C("Ad Soyad", Name, 1.5), C("Öğün", x => x.MealType), C("Adet", x => x.MealCount.ToString(Turkish), .6), C("Durum", x => ReportText.Status(x)), C("Açıklama", x => ReportText.Description(x), 1.8)),
            // Sicil Listesi 12-13 sutun: yatay A4. Agirliklar icerige gore (veli adi en uzun, sinif/sube en kisa).
            [ReportType.StudentList] = Def(StudentListTitle, C("Öğrenci No", x => x.StudentNo, .9), C("Ad", x => x.FirstName, 1.1), C("Soyad", x => x.LastName, 1.1), C("Sınıf", x => x.Class, .6), C("Şube", x => x.Section, .6), C("Bölüm", x => x.Department, .9), C("Görev", x => x.Job, .8), C("Kart No", x => x.CardNo, .9), C("Veli", x => x.ParentName, 1.5), C("Veli Telefonu", x => x.ParentPhone, 1.1), C("Durum", x => ReportText.Status(x), .7), C("Kayıt Tarihi", x => x.ReportDate?.ToString("dd.MM.yyyy", Turkish), .9), C(ReportCsvService.NationalIdHeader, x => x.NationalId, 1.1))
        };

    private static ReportDefinition Def(string title, params ReportColumn[] columns) => new(title, columns);
    private static ReportColumn C(string title, Func<ReportRow, string?> value, double weight = 1) =>
        new(title, value, weight);

    private sealed record ReportDefinition(string Title, ReportColumn[] Columns);
    private sealed record ReportColumn(string Title, Func<ReportRow, string?> Value, double Weight);
}
