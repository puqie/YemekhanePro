using System.Text;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// Disa aktarilan dosyalar ham kod tasiyordu ("ALLOW", "VOIDED", "OK", "OPEN / "); ekran
/// Turkce gosterirken memurun eline gecen belge İngilizce kaliyordu. Ayni "OK" kodu gecis
/// raporunda neden, turnike raporunda sonuc oldugu icin ceviri rapor turune gore secilir.
/// </summary>
public sealed class ReportTextTests
{
    [Fact]
    public void KararVeDurumKodlariTurkcelesir()
    {
        Assert.Equal("İzin Verildi", ReportText.Decision("ALLOW"));
        Assert.Equal("Reddedildi", ReportText.Decision("deny"));
        Assert.Equal("İptal", ReportText.Status(Row(ReportType.Income, status: "VOIDED")));
        Assert.Equal("Aktif", ReportText.Status(Row(ReportType.MealEntitlement, status: "Active")));
        Assert.Equal("Kullanıldı", ReportText.Status(Row(ReportType.StudentMealUsage, status: "USED")));
    }

    [Fact]
    public void AyniKodRaporTurunGoreFarkliCevrilir()
    {
        Assert.Equal("Geçiş onaylandı", ReportText.Status(Row(ReportType.DailyAccess, status: "OK")));
        Assert.Equal("Başarılı", ReportText.Status(Row(ReportType.Turnstile, status: "OK")));
        Assert.Equal("Zaman aşımı", ReportText.Status(Row(ReportType.Turnstile, status: "TIMEOUT")));
        Assert.Equal("Kart pasif", ReportText.Status(Row(ReportType.DeniedAccess, status: "Kart pasif")));
    }

    [Fact]
    public void TaninmayanKodAynenKalirBosDegerBosKalir()
    {
        Assert.Equal("YENI_KOD", ReportText.Decision("YENI_KOD"));
        Assert.Equal("", ReportText.Decision(null));
        Assert.Equal("", ReportText.Status(Row(ReportType.Sms, status: " ")));
    }

    [Fact]
    public void TurnikeAciklamasiKomutuCevirirBosHatayiAtar()
    {
        Assert.Equal("Aç", ReportText.Description(Row(ReportType.Turnstile, description: "OPEN")));
        Assert.Equal("Aç / Cihaz yanıt vermedi", ReportText.Description(Row(ReportType.Turnstile, description: "OPEN / Cihaz yanıt vermedi")));
        Assert.Equal("Günlük Yemek / Eylül", ReportText.Description(Row(ReportType.Income, description: "Günlük Yemek / Eylül")));
    }

    [Fact]
    public async Task CsvDosyasindaHamKodKalmaz()
    {
        var rows = new[] { Row(ReportType.DailyAccess, status: "OK", decision: "ALLOW", description: "Entry / Device") };
        var service = new ReportCsvService(new ReportService(new FixedRepository(rows)));
        await using var output = new MemoryStream();

        await service.GenerateAsync(ReportType.DailyAccess, new ReportQuery(), output);

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"İzin Verildi\"", text);
        Assert.Contains("\"Geçiş onaylandı\"", text);
        Assert.DoesNotContain("\"ALLOW\"", text);
        Assert.DoesNotContain("\"OK\"", text);
        Assert.Contains("31.08.2026 12:30:10\"", text);
    }

    private static ReportRow Row(ReportType type, string? status = null, string? decision = null, string? description = null) => new()
    {
        Id = Guid.NewGuid(), Type = type, Status = status, Decision = decision, Description = description,
        Timestamp = new DateTimeOffset(2026, 8, 31, 12, 30, 10, 123, TimeSpan.FromHours(3))
    };

    private sealed class FixedRepository(IReadOnlyList<ReportRow> rows) : IReportRepository
    {
        public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new ReportResult(rows, 1, rows.Count, new ReportSummary(rows.Count, 0, 0, 0, 0)));

        public async IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(ReportType type, ReportQuery query, int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return rows;
        }
    }
}
