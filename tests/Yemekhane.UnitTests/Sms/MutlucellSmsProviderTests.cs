using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// Mutlucell XML ag gecidi: govde yapisi (ka/pwd/org/charset, XML kacisi, 905xx numara),
/// "$paketId" basarisi, sayisal hata kodlarinin kalici/gecici esleme tablosu, kontor yaniti
/// ve HTTP tasima (icerik turu, adres, bos kimlik bilgisi, sunucu hatasi). Saglayici okulda
/// "sms gonderiminde sorun var" diye geldi; bu testler XML sozlesmesini dokumanla sabitler.
/// </summary>
public sealed class MutlucellSmsProviderTests
{
    private static readonly MutlucellGateway.Credentials Account = new("okul", "gizli&sifre", "OKULBASLIK");

    [Fact]
    public void SendXmlCarriesCredentialsCharsetOriginatorAndNormalizedNumber()
    {
        var xml = MutlucellGateway.BuildSendXml(Account, "0532 111 22 33", "Merhaba <veli> & \"öğrenci\" ğüşıöç");

        // Bildirim UTF-8 olmali (StringWriter varsayilani utf-16'dir); buyuk/kucuk harf XML'de serbesttir.
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.OrdinalIgnoreCase);
        var pack = XDocument.Parse(xml).Root!;
        Assert.Equal("smspack", pack.Name.LocalName);
        Assert.Equal("okul", (string?)pack.Attribute("ka"));
        Assert.Equal("gizli&sifre", (string?)pack.Attribute("pwd"));
        Assert.Equal("OKULBASLIK", (string?)pack.Attribute("org"));
        Assert.Equal("turkish", (string?)pack.Attribute("charset"));
        var mesaj = Assert.Single(pack.Elements("mesaj"));
        Assert.Equal("Merhaba <veli> & \"öğrenci\" ğüşıöç", (string?)mesaj.Element("metin"));
        Assert.Equal("905321112233", (string?)mesaj.Element("nums"));
        // Ham metinde kacis var: ayristirici olmayan bir gecit "&" gorse XML hatasi (20) verirdi.
        Assert.Contains("&amp;", xml, StringComparison.Ordinal);
        Assert.Contains("&lt;veli&gt;", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginatorIsOmittedWhenBlank()
    {
        var xml = MutlucellGateway.BuildSendXml(Account with { Originator = "  " }, "905321112233", "x");
        Assert.Null(XDocument.Parse(xml).Root!.Attribute("org"));
    }

    [Theory]
    [InlineData("905321112233", "905321112233")]
    [InlineData("05321112233", "905321112233")]
    [InlineData("5321112233", "905321112233")]
    [InlineData("+90 532 111 22 33", "905321112233")]
    public void PhoneIsNormalizedToCountryCodeForm(string input, string expected) =>
        Assert.Equal(expected, MutlucellGateway.NormalizePhone(input));

    [Fact]
    public void CreditXmlHasOnlyCredentials()
    {
        var root = XDocument.Parse(MutlucellGateway.BuildCreditXml(Account)).Root!;
        Assert.Equal("smskredi", root.Name.LocalName);
        Assert.Equal("okul", (string?)root.Attribute("ka"));
        Assert.Equal("gizli&sifre", (string?)root.Attribute("pwd"));
        Assert.Empty(root.Elements());
    }

    [Theory]
    [InlineData("$88512", "88512")]
    [InlineData("  $ 77 \n", "77")]
    [InlineData("$", null)]
    public void DollarPrefixedResponseIsSuccessWithPackageId(string body, string? expectedId)
    {
        var result = MutlucellGateway.ParseSendResponse(body, 200);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedId, result.ProviderMessageId);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(body.Trim(), result.RawResponse);
    }

    [Theory]
    [InlineData("20", SmsSendOutcome.PermanentFailure, SmsErrorCategory.ProviderRejected)]
    [InlineData("21", SmsSendOutcome.PermanentFailure, SmsErrorCategory.ProviderRejected)]
    [InlineData("22", SmsSendOutcome.TransientFailure, SmsErrorCategory.ProviderRejected)]
    [InlineData("23", SmsSendOutcome.PermanentFailure, SmsErrorCategory.Authentication)]
    [InlineData("24", SmsSendOutcome.TransientFailure, SmsErrorCategory.RateLimited)]
    [InlineData("25", SmsSendOutcome.TransientFailure, SmsErrorCategory.ProviderUnavailable)]
    [InlineData("30", SmsSendOutcome.PermanentFailure, SmsErrorCategory.Authentication)]
    [InlineData("34", SmsSendOutcome.PermanentFailure, SmsErrorCategory.Authentication)]
    [InlineData("99", SmsSendOutcome.PermanentFailure, SmsErrorCategory.InvalidResponse)]
    public void NumericCodesMapToDocumentedOutcomes(string code, SmsSendOutcome outcome, SmsErrorCategory category)
    {
        var result = MutlucellGateway.ParseSendResponse(code + "\r\n");

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(category, result.ErrorCategory);
        Assert.Equal("mutlucell_" + code, result.ErrorCode);
        Assert.Contains($"({code})", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(code, result.RawResponse);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad Gateway</html>")]
    [InlineData("12abc")]
    public void AnythingElseIsAnInvalidResponse(string body)
    {
        var result = MutlucellGateway.ParseSendResponse(body);

        Assert.Equal(SmsSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(SmsErrorCategory.InvalidResponse, result.ErrorCategory);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(body.Trim(), result.RawResponse);
    }

    [Fact]
    public void CreditResponseParsesNumberOrError()
    {
        var ok = MutlucellGateway.ParseCreditResponse("$15234");
        Assert.True(ok.Success);
        Assert.Equal(15234m, ok.Credit);
        Assert.Equal("Kalan kontör: 15.234", ok.Message);

        var denied = MutlucellGateway.ParseCreditResponse("23");
        Assert.False(denied.Success);
        Assert.Null(denied.Credit);
        Assert.Contains("(23)", denied.Message, StringComparison.Ordinal);

        Assert.False(MutlucellGateway.ParseCreditResponse("<html/>").Success);
    }

    [Fact]
    public void CreditEndpointDerivesFromSendEndpoint()
    {
        Assert.Equal(MutlucellGateway.DefaultCreditEndpoint, MutlucellGateway.CreditEndpointFor(MutlucellGateway.DefaultSendEndpoint));
        Assert.Equal("https://test.example/ws/gtcrdtex", MutlucellGateway.CreditEndpointFor("https://test.example/ws/sndblkex"));
        Assert.Equal(MutlucellGateway.DefaultCreditEndpoint, MutlucellGateway.CreditEndpointFor("https://test.example/other"));
        Assert.Equal(MutlucellGateway.DefaultCreditEndpoint, MutlucellGateway.CreditEndpointFor(null));
    }

    [Fact]
    public void RawResponseIsTruncatedToKeepTheScreenReadable()
    {
        var huge = new string('x', 2000);
        Assert.Equal(501, MutlucellGateway.Truncate(huge).Length);
        Assert.EndsWith("…", MutlucellGateway.Truncate(huge), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- HTTP tasima

    [Fact]
    public async Task SendPostsXmlToDefaultGatewayAndReflectsRawResponse()
    {
        HttpRequestMessage? captured = null; string? body = null;
        var provider = Provider(async (request, cancellationToken) =>
        {
            captured = request; body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Text(HttpStatusCode.OK, "$88512");
        }, Options());

        var result = await provider.SendAsync(new SmsSendRequest("905321112233", "Deneme"));

        Assert.True(result.IsSuccess);
        Assert.Equal("88512", result.ProviderMessageId);
        Assert.Equal("$88512", result.RawResponse);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(MutlucellGateway.DefaultSendEndpoint, captured.RequestUri!.ToString());
        Assert.Equal("text/xml", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", captured.Content.Headers.ContentType.CharSet);
        Assert.Contains("<smspack", body, StringComparison.Ordinal);
        Assert.Contains("ka=\"okul\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomEndpointIsUsedAndPlaceholderIsIgnored()
    {
        var seen = new List<string>();
        var handler = new StubHandler((request, _) => { seen.Add(request.RequestUri!.ToString()); return Task.FromResult(Text(HttpStatusCode.OK, "$1")); });
        await Provider(handler, Options(o => o.Endpoint = "https://test.example/ws/sndblkex")).SendAsync(new SmsSendRequest("905321112233", "x"));
        await Provider(handler, Options(o => o.Endpoint = "https://sms.invalid/")).SendAsync(new SmsSendRequest("905321112233", "x"));
        await Provider(handler, Options(o => o.Endpoint = "")).SendAsync(new SmsSendRequest("905321112233", "x"));

        Assert.Equal(["https://test.example/ws/sndblkex", MutlucellGateway.DefaultSendEndpoint, MutlucellGateway.DefaultSendEndpoint], seen);
    }

    [Fact]
    public async Task MissingCredentialsFailWithoutCallingTheGateway()
    {
        var calls = 0;
        var provider = Provider((_, _) => { calls++; return Task.FromResult(Text(HttpStatusCode.OK, "$1")); }, Options(o => o.Secret = null));

        var result = await provider.SendAsync(new SmsSendRequest("905321112233", "x"));

        Assert.Equal(0, calls);
        Assert.Equal(SmsSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(SmsErrorCategory.Configuration, result.ErrorCategory);
        Assert.Equal("credentials_missing", result.ErrorCode);
        Assert.Contains("Ayarlar → SMS", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorCodeBodyBecomesTypedFailureEvenWithHttp200()
    {
        var provider = Provider((_, _) => Task.FromResult(Text(HttpStatusCode.OK, "23")), Options());

        var result = await provider.SendAsync(new SmsSendRequest("905321112233", "x"));

        Assert.Equal(SmsSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(SmsErrorCategory.Authentication, result.ErrorCategory);
        Assert.Equal("mutlucell_23", result.ErrorCode);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public async Task ServerErrorIsTransientAndKeepsTheBody()
    {
        var provider = Provider((_, _) => Task.FromResult(Text(HttpStatusCode.BadGateway, "<html>502</html>")), Options());

        var result = await provider.SendAsync(new SmsSendRequest("905321112233", "x"));

        Assert.Equal(SmsSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(SmsErrorCategory.ProviderUnavailable, result.ErrorCategory);
        Assert.Equal("http_502", result.ErrorCode);
        Assert.Equal("<html>502</html>", result.RawResponse);
    }

    [Fact]
    public async Task TransportFailureIsTransient()
    {
        var provider = Provider((_, _) => throw new HttpRequestException("dns"), Options());

        var result = await provider.SendAsync(new SmsSendRequest("905321112233", "x"));

        Assert.Equal(SmsSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(SmsErrorCategory.Transport, result.ErrorCategory);
        Assert.Contains("bağlanılamadı", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreditQueryPostsToCreditEndpoint()
    {
        HttpRequestMessage? captured = null; string? body = null;
        var provider = Provider(async (request, cancellationToken) =>
        {
            captured = request; body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Text(HttpStatusCode.OK, "$420");
        }, Options());

        var result = await provider.QueryCreditAsync();

        Assert.True(result.Success);
        Assert.Equal(420m, result.Credit);
        Assert.Equal(MutlucellGateway.DefaultCreditEndpoint, captured!.RequestUri!.ToString());
        Assert.Contains("<smskredi", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreditQueryTransportFailureIsReported()
    {
        var provider = Provider((_, _) => throw new HttpRequestException("kopuk"), Options());

        var result = await provider.QueryCreditAsync();

        Assert.False(result.Success);
        Assert.Contains("bağlanılamadı", result.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- yardimcilar

    private static SmsProviderOptions Options(Action<SmsProviderOptions>? configure = null)
    {
        var options = new SmsProviderOptions
        {
            Provider = "Mutlucell", Endpoint = "", Username = "okul", Secret = "gizli", Sender = "OKUL",
            TimeoutSeconds = 5, AllowPrivateNetworks = true
        };
        configure?.Invoke(options);
        return options;
    }

    private static MutlucellSmsProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, SmsProviderOptions options) =>
        Provider(new StubHandler(send), options);

    private static MutlucellSmsProvider Provider(HttpMessageHandler handler, SmsProviderOptions options) =>
        new(new HttpClient(handler, disposeHandler: false), Microsoft.Extensions.Options.Options.Create(options));

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
