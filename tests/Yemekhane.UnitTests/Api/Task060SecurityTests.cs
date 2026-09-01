using System.Net;
using System.Net.Http.Json;
using Yemekhane.Application.Common;

namespace Yemekhane.UnitTests.Api;

public sealed class Task060SecurityTests : IClassFixture<YemekhaneApiFactory>
{
    private readonly YemekhaneApiFactory factory;

    public Task060SecurityTests(YemekhaneApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData("/api/students")]
    [InlineData("/api/reports/DailyAccess")]
    [InlineData("/api/settings")]
    [InlineData("/api/admin/users")]
    [InlineData("/api/audit-logs")]
    public async Task ProtectedSurfacesRejectAnonymousCallers(string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/admin/users")]
    [InlineData("/api/audit-logs")]
    // Yonetim denetleyicilerinde sinif duzeyinde politika YOKTUR: her uc noktaya ayri ayri
    // yetki eklenmelidir. Unutulan tek bir metot butun sinirin acilmasi demektir.
    [InlineData("/api/settings/sync/status")]
    [InlineData("/api/settings/sync/conflicts")]
    [InlineData("/api/settings/logs")]
    public async Task OperatorCannotCrossAdministrativeRoleBoundary(string path)
    {
        using var response = await factory.CreateOperatorClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ApiAddsSecurityHeadersAndRejectsOversizedJson()
    {
        using var client = factory.CreateClient();
        using var normal = await client.GetAsync("/health");
        Assert.Equal("nosniff", normal.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", normal.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", normal.Headers.CacheControl?.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new ByteArrayContent(new byte[10_500_001])
        };
        request.Content.Headers.ContentType = new("application/json");
        using var oversized = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task LoginIsRateLimitedPerAccountAndAddress()
    {
        using var isolatedFactory = new YemekhaneApiFactory();
        using var client = isolatedFactory.CreateClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 21; i++)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync("/api/auth/login", new { Username = "rate-limit-target", Password = "invalid" });
        }
        using (last) Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Theory]
    [InlineData("http://sms.example.test/send")]
    [InlineData("https://127.0.0.1/send")]
    [InlineData("https://10.1.2.3/send")]
    [InlineData("https://[::1]/send")]
    [InlineData("https://user:password@example.test/send")]
    public void OutboundPolicyRejectsInsecureOrPrivateEndpoints(string endpoint) =>
        Assert.Throws<RequestValidationException>(() => OutboundEndpointPolicy.ValidateSyntax(endpoint));
}
