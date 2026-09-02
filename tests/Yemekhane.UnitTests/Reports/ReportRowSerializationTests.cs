using System.Text.Json;
using Yemekhane.Application.Reports;

namespace Yemekhane.UnitTests.Reports;

public sealed class ReportRowSerializationTests
{
    // Masaustu istemci raporlari API'den JSON olarak okuyor; tutar gidis-donus
    // hayatta kalmazsa Gunluk Kasa/Gelir ekranlarinda her satir 0,00 TL gorunur.
    [Fact]
    public void AmountSurvivesJsonRoundTrip()
    {
        var row = new ReportRow
        {
            Id = Guid.NewGuid(),
            Type = ReportType.DailyCash,
            AmountCents = 50_000L
        };

        var json = JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<ReportRow>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Contains("\"amount\"", json);
        Assert.Equal(500.00m, restored.Amount);
        Assert.Equal(50_000L, restored.AmountCents);
    }
}
