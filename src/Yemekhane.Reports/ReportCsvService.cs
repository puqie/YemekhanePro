using System.Globalization;
using System.Text;
using Yemekhane.Application.Reports;

namespace Yemekhane.Reports;

public sealed class ReportCsvService(ReportService reportService) : ICsvService
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();
    private static readonly string[] Headers =
    [
        "Tarih", "Öğrenci No", "Kart No", "Ad", "Soyad", "Sınıf", "Şube", "Bölüm", "Görev",
        "Öğün", "Cihaz", "Karar", "Durum", "Açıklama", "Yemek Adedi", "Tutar"
    ];

    public async Task GenerateAsync(ReportType type, ReportQuery query, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(Encoding.UTF8.GetPreamble(), cancellationToken);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, true);
        await writer.WriteLineAsync(string.Join(';', Headers.Select(Escape)));
        await foreach (var batch in reportService.StreamBatchesAsync(type, query, cancellationToken: cancellationToken))
        {
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = row.Timestamp.HasValue
                    // Ekran ve PDF ile ayni bicim: milisaniye bir yemekhane gecisi icin gurultudur.
                    ? TimeZoneInfo.ConvertTime(row.Timestamp.Value, Istanbul).ToString("dd.MM.yyyy HH:mm:ss", Turkish)
                    : row.ReportDate?.ToString("dd.MM.yyyy", Turkish);
                string?[] values = [date, row.StudentNo, row.CardNo, row.FirstName, row.LastName, row.Class,
                    row.Section, row.Department, row.Job, row.MealType, row.Device,
                    // Dosyada ham kod ("ALLOW", "VOIDED") degil Turkce metin; ekranla ayni sozluk.
                    ReportText.Decision(row.Decision), ReportText.Status(row), ReportText.Description(row),
                    row.MealCount.ToString(Turkish), row.Amount.ToString("N2", Turkish)];
                await writer.WriteLineAsync(string.Join(';', values.Select(Escape)));
            }
            await writer.FlushAsync(cancellationToken);
        }
    }

    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
