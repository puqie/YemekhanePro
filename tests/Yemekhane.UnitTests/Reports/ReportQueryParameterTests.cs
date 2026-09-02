using System.Net;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Reports;

/// <summary>
/// <c>GET /api/reports/CardMovements?startDate=2026-09-02&amp;endDate=2026-09-02</c> istegine
/// 2026-08-03 tarihli kayitlar donuyordu: parametre adlari yanlisti (start/end beklenir) ve
/// ASP.NET bilinmeyen sorgu parametrelerini SESSIZCE yok sayar. Bir rapor ucunda "filtre
/// uygulanmadi" ile "filtre uygulandi, kayit yok" ayirt edilemezse rapor guvenilmez;
/// bilinmeyen parametre 400 ile geri cevrilir.
/// </summary>
public sealed class ReportQueryParameterTests(YemekhaneApiFactory factory) : IClassFixture<YemekhaneApiFactory>
{
    [Theory]
    [InlineData("/api/reports/CardMovements?startDate=2026-09-02&endDate=2026-09-02")]
    [InlineData("/api/reports/Income/csv?startDate=2026-09-02")]
    [InlineData("/api/reports/DailyAccess?start=2026-09-02&student=5001")]
    public async Task UnknownQueryParametersAreRejectedInsteadOfIgnored(string path)
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.GetAsync(path);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Bilinmeyen sorgu parametresi", body);
        Assert.Contains("start", body);
    }

    [Fact]
    public async Task KnownQueryParametersStillWork()
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.GetAsync(
            "/api/reports/CardMovements?start=2026-09-02T00:00:00%2B03:00&end=2026-09-02T23:59:59%2B03:00" +
            "&studentNo=5001&class=5A&sortBy=timestamp&descending=true&page=1&pageSize=10");

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
    }
}
