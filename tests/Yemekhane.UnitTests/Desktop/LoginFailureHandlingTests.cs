using System.Net;
using System.Net.Http;
using System.Text;
using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Giris hatalarinin kullaniciya ulasmasi.
///
/// LoginWindow.LoginClick bir "async void" isleyicidir ve yalnizca AuthenticationException
/// yakalar. Baska bir tur exception -- bozuk JSON yaniti, beklenmeyen bir hata -- yakalanmadan
/// kalirsa WPF uygulamayi HICBIR MESAJ GOSTERMEDEN aninda kapatir.
///
/// Kullanicinin gordugu: "Giris" tusuna basiyor, uygulama kayboluyor. Sahada gozlenen
/// belirti tam olarak budur. Bu yuzden istemci her basarisizligi AuthenticationException'a
/// cevirmelidir.
/// </summary>
public sealed class LoginFailureHandlingTests
{
    private static AuthenticationClient Client(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        var handler = new StubHandler(status, body, mediaType);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5555") };
        return new AuthenticationClient(http, new MutableJwtSession());
    }

    [Fact]
    public async Task MalformedJsonSurfacesAsAuthenticationErrorNotACrash()
    {
        // Govde JSON degil: ReadFromJsonAsync JsonException firlatir. Cevrilmezse
        // async void isleyicide uygulama sessizce kapanir.
        var client = Client(HttpStatusCode.OK, "<html>bu json degil</html>");

        var exception = await Record.ExceptionAsync(() => client.LoginAsync("admin", "parola"));

        Assert.IsType<AuthenticationException>(exception);
    }

    [Fact]
    public async Task TruncatedJsonSurfacesAsAuthenticationError()
    {
        var client = Client(HttpStatusCode.OK, "{\"accessToken\":\"abc\"");

        var exception = await Record.ExceptionAsync(() => client.LoginAsync("admin", "parola"));

        Assert.IsType<AuthenticationException>(exception);
    }

    [Fact]
    public async Task WrongContentTypeSurfacesAsAuthenticationError()
    {
        // Sunucu duz metin dondurdugunde ReadFromJsonAsync NotSupportedException firlatir.
        var client = Client(HttpStatusCode.OK, "tamam", "text/plain");

        var exception = await Record.ExceptionAsync(() => client.LoginAsync("admin", "parola"));

        Assert.IsType<AuthenticationException>(exception);
    }

    [Fact]
    public async Task NullJsonBodySurfacesAsAuthenticationError()
    {
        var client = Client(HttpStatusCode.OK, "null");

        var exception = await Record.ExceptionAsync(() => client.LoginAsync("admin", "parola"));

        Assert.IsType<AuthenticationException>(exception);
    }

    [Fact]
    public async Task EveryFailureCarriesATurkishMessageTheUserCanAct()
    {
        var client = Client(HttpStatusCode.OK, "bozuk");

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => client.LoginAsync("admin", "parola"));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    private sealed class StubHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
