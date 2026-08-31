using System.Net;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Real-time hub, öğrenci adı ve geçiş kararlarını yayınlar; kimliksiz erişime kapalı olmalıdır.
/// </summary>
public sealed class RealtimeHubAuthorizationTests : IClassFixture<YemekhaneApiFactory>
{
    private const string NegotiatePath = "/hubs/realtime/negotiate?negotiateVersion=1";
    private readonly YemekhaneApiFactory factory;

    public RealtimeHubAuthorizationTests(YemekhaneApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task HubRejectsAnonymousNegotiation()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(NegotiatePath, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HubAcceptsAuthenticatedNegotiation()
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.PostAsync(NegotiatePath, content: null);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
