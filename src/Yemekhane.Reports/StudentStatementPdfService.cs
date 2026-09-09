using System.Globalization;
using Microsoft.Extensions.Options;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Yemekhane.Application.Common;
using Yemekhane.Application.Reports;
using Yemekhane.Application.Statements;

namespace Yemekhane.Reports;

/// <summary>
/// Ogrenci ekstresinin PDF ciktisi: veli "gecen yil ne odedim" diye sordugunda eline
/// verilecek belge. Rapor PDF'leriyle ayni yazi tipini (Turkce karakterli Noto Sans) ve
/// ayni okul basligini kullanir; bolum bolum (odemeler, taksitler, bakiye, yemek) yazar.
/// </summary>
public sealed class StudentStatementPdfService : IStudentStatementPdfService
{
    private const double Margin = 32;
    private const double RowHeight = 17;
    private const double FooterHeight = 24;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindIstanbul();
    private static readonly object FontLock = new();
    private readonly ReportPdfOptions options;
    private readonly TimeProvider timeProvider;
    private readonly IReportBrandingProvider? branding;

    public StudentStatementPdfService(IOptions<ReportPdfOptions> options, TimeProvider timeProvider)
        : this(options, timeProvider, null) { }

    public StudentStatementPdfService(IOptions<ReportPdfOptions> options, TimeProvider timeProvider,
        IReportBrandingProvider? branding)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.branding = branding;
        if (string.IsNullOrWhiteSpace(this.options.SchoolName))
            throw new RequestValidationException("PDF okul adı boş olamaz.");
        lock (FontLock) GlobalFontSettings.FontResolver ??= new NotoSansFontResolver();
    }

    public async Task GenerateAsync(StudentStatement statement, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("PDF çıktı akışı yazılabilir olmalıdır.", nameof(output));

        var schoolName = await ResolveSchoolNameAsync(cancellationToken);
        var generatedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), Istanbul);
        using var document = new PdfDocument();
        document.Info.Title = $"{schoolName} - Öğrenci Ekstresi - {statement.StudentName}";
        document.Info.Author = schoolName;
        document.Info.Subject = $"{statement.From:dd.MM.yyyy} - {statement.To:dd.MM.yyyy}";

        PdfPage? page = null;
        XGraphics? graphics = null;
        var y = 0d;

        void NewPage(bool first)
        {
            graphics?.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            page.Orientation = PdfSharp.PageOrientation.Portrait;
            graphics = XGraphics.FromPdfPage(page);
            y = DrawHeading(graphics, page, statement, schoolName, first);
        }

        void EnsureRoom(double needed)
        {
            if (y + needed > page!.Height.Point - Margin - FooterHeight) NewPage(false);
        }

        NewPage(true);
        foreach (var section in statement.Sections)
        {
            var rows = statement.Lines.Where(x => x.Section == section.Section).ToList();
            if (rows.Count == 0) continue;

            EnsureRoom(RowHeight * 3);
            y += 6;
            graphics!.DrawString(section.Label, Font(11, true), XBrushes.Black, new XPoint(Margin, y + 11));
            var totalText = section.Section == StatementSections.Meal
                ? $"{section.Count} kayıt · {section.Total:N0} öğün"
                : $"{section.Count} kayıt · {section.Total.ToString("N2", Turkish)} ₺";
            graphics.DrawString(totalText, Font(9), XBrushes.Gray,
                new XRect(Margin, y, page!.Width.Point - (2 * Margin), RowHeight), XStringFormats.TopRight);
            y += RowHeight;
            DrawSeparator(graphics, page, y);
            y += 4;

            foreach (var line in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRoom(RowHeight);
                DrawLine(graphics!, page!, line, y);
                y += RowHeight;
            }
        }

        EnsureRoom(RowHeight * 8);
        y += 8;
        DrawSummary(graphics!, page!, statement, y);
        graphics?.Dispose();
        DrawFooters(document, generatedAt);
        await SaveAsync(document, output, cancellationToken);
    }

    private static double DrawHeading(XGraphics graphics, PdfPage page, StudentStatement statement, string schoolName, bool first)
    {
        var width = page.Width.Point - (2 * Margin);
        var y = Margin;
        graphics.DrawString(schoolName, Font(14, true), XBrushes.Black,
            new XRect(Margin, y, width, 20), XStringFormats.TopCenter);
        y += 20;
        graphics.DrawString("Öğrenci Ekstresi", Font(12, true), XBrushes.Black,
            new XRect(Margin, y, width, 18), XStringFormats.TopCenter);
        y += 22;
        if (!first) return y;

        var identity = $"{statement.StudentName} · No {statement.StudentNo}";
        if (!string.IsNullOrWhiteSpace(statement.ClassName))
            identity += $" · {statement.ClassName}{(string.IsNullOrWhiteSpace(statement.SectionName) ? "" : "/" + statement.SectionName)}";
        graphics.DrawString(identity, Font(10, true), XBrushes.Black, new XPoint(Margin, y + 10));
        y += RowHeight;
        graphics.DrawString($"Tarih aralığı: {statement.From:dd.MM.yyyy} - {statement.To:dd.MM.yyyy}", Font(9), XBrushes.Gray,
            new XPoint(Margin, y + 9));
        y += RowHeight - 2;
        if (!string.IsNullOrWhiteSpace(statement.ParentName))
        {
            graphics.DrawString($"Veli: {statement.ParentName}{(string.IsNullOrWhiteSpace(statement.ParentPhone) ? "" : " · " + statement.ParentPhone)}",
                Font(9), XBrushes.Gray, new XPoint(Margin, y + 9));
            y += RowHeight - 2;
        }
        DrawSeparator(graphics, page, y + 4);
        return y + 10;
    }

    private static void DrawLine(XGraphics graphics, PdfPage page, StatementLine line, double y)
    {
        var width = page.Width.Point - (2 * Margin);
        var date = TimeZoneInfo.ConvertTime(line.OccurredAt, Istanbul).ToString("dd.MM.yyyy", Turkish);
        graphics.DrawString(date, Font(9), XBrushes.Black, new XPoint(Margin, y + 11));

        var title = line.Title;
        if (!string.IsNullOrWhiteSpace(line.Detail)) title += " · " + line.Detail;
        graphics.DrawString(Truncate(graphics, title, width - 250), Font(9), XBrushes.Black, new XPoint(Margin + 62, y + 11));

        var value = line.Section == StatementSections.Meal ? "1 öğün" : line.Amount.ToString("N2", Turkish) + " ₺";
        // Iptal edilen satir gri yazilir: veli "bu tahsilat iptal edilmis" oldugunu gormeli.
        var brush = line.IsCancelled ? XBrushes.Gray : XBrushes.Black;
        graphics.DrawString(value, Font(9, !line.IsCancelled), brush,
            new XRect(Margin, y, width - 78, RowHeight), XStringFormats.TopRight);
        if (!string.IsNullOrWhiteSpace(line.Status))
            graphics.DrawString(line.Status, Font(8), XBrushes.Gray,
                new XRect(Margin, y + 1, width, RowHeight), XStringFormats.TopRight);
    }

    private static void DrawSummary(XGraphics graphics, PdfPage page, StudentStatement statement, double y)
    {
        var width = page.Width.Point - (2 * Margin);
        DrawSeparator(graphics, page, y);
        y += 8;
        graphics.DrawString("Özet", Font(11, true), XBrushes.Black, new XPoint(Margin, y + 11));
        y += RowHeight;

        foreach (var (label, value) in Summary(statement))
        {
            graphics.DrawString(label, Font(9), XBrushes.Black, new XPoint(Margin, y + 10));
            graphics.DrawString(value, Font(9, true), XBrushes.Black,
                new XRect(Margin, y, width, RowHeight), XStringFormats.TopRight);
            y += RowHeight - 2;
        }
    }

    /// <summary>Ozet satirlari; sifir olan bolumler yazilmaz ki belge kalabaliklasmasin.</summary>
    private static IEnumerable<(string Label, string Value)> Summary(StudentStatement statement)
    {
        yield return ("Tahsil edilen", Money(statement.TotalPaid));
        if (statement.TotalVoided > 0) yield return ("İptal edilen", Money(statement.TotalVoided));
        if (statement.BalanceTopUps != 0) yield return ("Bakiye yüklemesi", Money(statement.BalanceTopUps));
        if (statement.BalanceSpent != 0) yield return ("Bakiyeden harcanan", Money(statement.BalanceSpent));
        yield return ("Güncel bakiye", Money(statement.CurrentBalance));
        if (statement.TuitionDue > 0)
        {
            yield return ("Ücret planı toplamı", Money(statement.TuitionDue));
            yield return ("Ücret ödemesi", Money(statement.TuitionPaid));
            yield return ("Kalan borç", Money(statement.TuitionOutstanding));
            if (statement.TuitionOverdue > 0) yield return ("Gecikmiş borç", Money(statement.TuitionOverdue));
        }
        yield return ("Yenen öğün", statement.MealsUsed.ToString("N0", Turkish));
    }

    private static string Money(decimal value) => value.ToString("N2", Turkish) + " ₺";

    private static void DrawSeparator(XGraphics graphics, PdfPage page, double y) =>
        graphics.DrawLine(new XPen(XColors.LightGray, 0.6), Margin, y, page.Width.Point - Margin, y);

    private static string Truncate(XGraphics graphics, string text, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var font = Font(9);
        if (graphics.MeasureString(text, font).Width <= maxWidth) return text;
        var value = text;
        while (value.Length > 1 && graphics.MeasureString(value + "…", font).Width > maxWidth)
            value = value[..^1];
        return value + "…";
    }

    private static void DrawFooters(PdfDocument document, DateTimeOffset generatedAt)
    {
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            using var graphics = XGraphics.FromPdfPage(page);
            var y = page.Height.Point - Margin;
            graphics.DrawString($"Oluşturulma: {generatedAt:dd.MM.yyyy HH:mm} Europe/Istanbul", Font(8), XBrushes.Gray,
                new XPoint(Margin, y));
            graphics.DrawString($"Sayfa {index + 1}/{document.PageCount}", Font(8), XBrushes.Gray,
                new XRect(Margin, y - 10, page.Width.Point - (2 * Margin), 12), XStringFormats.TopRight);
        }
    }

    private static async Task SaveAsync(PdfDocument document, Stream output, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        document.Save(buffer, false);
        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken);
    }

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

    private static XFont Font(double size, bool bold = false) =>
        new(NotoSansFontResolver.FamilyName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static TimeZoneInfo FindIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
