using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Mutlucell SMS ag gecidi (smsgw.mutlucell.com/smsgw-ws): REST/JSON degil, HTTP POST ile
/// XML. Gonderim <c>sndblkex</c>, kontor <c>gtcrdtex</c>. Basarili yanit "$" ile baslar ve
/// devami paket kimligidir ("$88512"); hata yaniti duz sayisal koddur (20-34). Turkce karakter
/// icin <c>charset="turkish"</c> (tek SMS 150 karakter). Bu sinif yalnizca XML uretir ve yaniti
/// cozer; HTTP tasima <see cref="MutlucellSmsProvider"/>'dadir. Saf kaldigi icin sahte cihazsiz
/// test edilir.
/// </summary>
public static class MutlucellGateway
{
    public const string DefaultSendEndpoint = "https://smsgw.mutlucell.com/smsgw-ws/sndblkex";
    public const string DefaultCreditEndpoint = "https://smsgw.mutlucell.com/smsgw-ws/gtcrdtex";
    public const string Charset = "turkish";
    public const string ContentType = "text/xml";
    private const int RawLimit = 500;

    public sealed record Credentials(string Username, string Password, string? Originator);

    public static string BuildSendXml(Credentials credentials, string phone, string message)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var pack = new XElement("smspack",
            new XAttribute("ka", credentials.Username),
            new XAttribute("pwd", credentials.Password),
            new XAttribute("charset", Charset));
        if (!string.IsNullOrWhiteSpace(credentials.Originator))
            pack.Add(new XAttribute("org", credentials.Originator.Trim()));
        pack.Add(new XElement("mesaj", new XElement("metin", message), new XElement("nums", NormalizePhone(phone))));
        return Serialize(pack);
    }

    public static string BuildCreditXml(Credentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return Serialize(new XElement("smskredi",
            new XAttribute("ka", credentials.Username),
            new XAttribute("pwd", credentials.Password)));
    }

    /// <summary>Mutlucell 905xx bicimini bekler; kuyruktaki numara 90 ile baslar ama elle girilen "05.." da gelebilir.</summary>
    public static string NormalizePhone(string phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits switch
        {
            { Length: 12 } when digits.StartsWith("90", StringComparison.Ordinal) => digits,
            { Length: 11 } when digits.StartsWith('0') => "9" + digits,
            { Length: 10 } => "90" + digits,
            _ => digits
        };
    }

    /// <summary>Gonderim yaniti: "$id" basari; sayisal kod hata; baska her sey gecersiz yanit.</summary>
    public static SmsSendResult ParseSendResponse(string? body, int? httpStatusCode = null)
    {
        var text = (body ?? string.Empty).Trim();
        var raw = Truncate(text);
        if (text.StartsWith('$'))
        {
            var id = text[1..].Trim();
            return new SmsSendResult(SmsSendOutcome.Success, id.Length == 0 ? null : id, HttpStatusCode: httpStatusCode, RawResponse: raw);
        }
        var code = new string(text.TakeWhile(char.IsDigit).ToArray());
        if (code.Length > 0 && code.Length == text.Length)
        {
            var (outcome, category, message) = MapError(code);
            return new SmsSendResult(outcome, ErrorCategory: category, ErrorCode: "mutlucell_" + code,
                ErrorMessage: message, HttpStatusCode: httpStatusCode, RawResponse: raw);
        }
        return new SmsSendResult(SmsSendOutcome.PermanentFailure, ErrorCategory: SmsErrorCategory.InvalidResponse,
            ErrorCode: "invalid_response", ErrorMessage: text.Length == 0 ? "Mutlucell boş yanıt döndürdü." : "Mutlucell beklenmeyen yanıt döndürdü: " + raw,
            HttpStatusCode: httpStatusCode, RawResponse: raw);
    }

    /// <summary>Kontor yaniti: "$15234" → 15234; sayisal kod → hata metni.</summary>
    public static SmsCreditResult ParseCreditResponse(string? body)
    {
        var text = (body ?? string.Empty).Trim();
        var raw = Truncate(text);
        if (text.StartsWith('$') &&
            decimal.TryParse(text[1..].Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var credit))
            return new SmsCreditResult(true, credit, $"Kalan kontör: {credit.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"))}", raw);
        var code = new string(text.TakeWhile(char.IsDigit).ToArray());
        if (code.Length > 0 && code.Length == text.Length)
            return new SmsCreditResult(false, null, MapError(code).Message, raw);
        return new SmsCreditResult(false, null, text.Length == 0 ? "Mutlucell boş yanıt döndürdü." : "Mutlucell beklenmeyen yanıt döndürdü: " + raw, raw);
    }

    /// <summary>
    /// Ortak hata kodlari (Mutlucell dokumani). 22/24/25 gecici sayilir: kontor yuklenince ya da
    /// SMSC donunce kuyruk yeniden dener; 23/30/34 kalicidir, tekrar denemek hesabi kilitleyebilir.
    /// </summary>
    public static (SmsSendOutcome Outcome, SmsErrorCategory Category, string Message) MapError(string code) => code switch
    {
        "20" => (SmsSendOutcome.PermanentFailure, SmsErrorCategory.ProviderRejected, "Mutlucell: XML eksik veya hatalı (20)."),
        "21" => (SmsSendOutcome.PermanentFailure, SmsErrorCategory.ProviderRejected, "Mutlucell: başlık (originatör) bu hesaba ait değil (21). Ayarlar → SMS → Başlık alanını onaylı başlıkla doldurun."),
        "22" => (SmsSendOutcome.TransientFailure, SmsErrorCategory.ProviderRejected, "Mutlucell: kontör yetersiz (22). Kontör yüklenince bekleyen SMS'ler yeniden denenir."),
        "23" => (SmsSendOutcome.PermanentFailure, SmsErrorCategory.Authentication, "Mutlucell: kullanıcı adı veya şifre hatalı (23)."),
        "24" => (SmsSendOutcome.TransientFailure, SmsErrorCategory.RateLimited, "Mutlucell: başka bir işlem aktif (24); yeniden denenecek."),
        "25" => (SmsSendOutcome.TransientFailure, SmsErrorCategory.ProviderUnavailable, "Mutlucell: SMSC durmuş (25); yeniden denenecek."),
        "30" => (SmsSendOutcome.PermanentFailure, SmsErrorCategory.Authentication, "Mutlucell: hesap aktive edilmemiş (30)."),
        "34" => (SmsSendOutcome.PermanentFailure, SmsErrorCategory.Authentication, "Mutlucell: API erişimi kapalı (34). Mutlucell panelinden API erişimini açtırın."),
        _ => (SmsSendOutcome.PermanentFailure, SmsErrorCategory.InvalidResponse, $"Mutlucell: bilinmeyen hata kodu ({code}).")
    };

    /// <summary>Gonderim adresinden kontor adresi: ".../sndblkex" → ".../gtcrdtex"; baska adres verildiyse varsayilan.</summary>
    public static string CreditEndpointFor(string? sendEndpoint)
    {
        var endpoint = sendEndpoint?.Trim() ?? string.Empty;
        return endpoint.EndsWith("/sndblkex", StringComparison.OrdinalIgnoreCase)
            ? endpoint[..^"sndblkex".Length] + "gtcrdtex"
            : DefaultCreditEndpoint;
    }

    public static string Truncate(string text) => text.Length <= RawLimit ? text : text[..RawLimit] + "…";

    private static string Serialize(XElement root)
    {
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var writer = new Utf8StringWriter();
        document.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    /// <summary>StringWriter varsayilan olarak utf-16 bildirir; Mutlucell UTF-8 bekler.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

/// <summary>
/// Mutlucell uzerinden gonderim: XML govde, <c>text/xml; charset=UTF-8</c>, yanit
/// <see cref="MutlucellGateway.ParseSendResponse"/>. Kimlik bilgileri <see cref="SmsProviderOptions"/>'tan
/// (Username=ka, Secret=pwd, Sender=org); adres bos ise varsayilan ag gecidi.
/// </summary>
public sealed class MutlucellSmsProvider(HttpClient httpClient, IOptions<SmsProviderOptions> options) : ISmsProvider
{
    public Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = options.Value;
        if (!TryCredentials(configuration, out var credentials))
            return Task.FromResult(new SmsSendResult(SmsSendOutcome.PermanentFailure, ErrorCategory: SmsErrorCategory.Configuration,
                ErrorCode: "credentials_missing", ErrorMessage: "Mutlucell kullanıcı adı ve API şifresi Ayarlar → SMS'te girilmemiş."));
        return PostAsync(SendEndpoint(configuration), MutlucellGateway.BuildSendXml(credentials, request.Phone, request.Message),
            configuration, (body, status) => MutlucellGateway.ParseSendResponse(body, status), failure => failure, cancellationToken);
    }

    public Task<SmsCreditResult> QueryCreditAsync(CancellationToken cancellationToken = default)
    {
        var configuration = options.Value;
        if (!TryCredentials(configuration, out var credentials))
            return Task.FromResult(new SmsCreditResult(false, null, "Mutlucell kullanıcı adı ve API şifresi Ayarlar → SMS'te girilmemiş.", null));
        return PostAsync(MutlucellGateway.CreditEndpointFor(SendEndpoint(configuration)), MutlucellGateway.BuildCreditXml(credentials),
            configuration, (body, _) => MutlucellGateway.ParseCreditResponse(body),
            failure => new SmsCreditResult(false, null, failure.ErrorMessage ?? failure.ErrorCode ?? "Mutlucell'e ulaşılamadı.", failure.RawResponse),
            cancellationToken);
    }

    public static string SendEndpoint(SmsProviderOptions configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration.Endpoint?.Trim();
        return string.IsNullOrEmpty(endpoint) || endpoint.Contains("sms.invalid", StringComparison.OrdinalIgnoreCase)
            ? MutlucellGateway.DefaultSendEndpoint : endpoint;
    }

    private static bool TryCredentials(SmsProviderOptions configuration, out MutlucellGateway.Credentials credentials)
    {
        credentials = new MutlucellGateway.Credentials(configuration.Username?.Trim() ?? string.Empty, configuration.Secret ?? string.Empty, configuration.Sender);
        return credentials.Username.Length > 0 && credentials.Password.Length > 0;
    }

    private async Task<T> PostAsync<T>(string endpoint, string xml, SmsProviderOptions configuration,
        Func<string, int, T> parse, Func<SmsSendResult, T> onFailure, CancellationToken cancellationToken)
    {
        Uri target;
        try
        {
            target = await OutboundEndpointPolicy.ValidateAsync(endpoint, configuration.AllowHttp,
                configuration.AllowPrivateNetworks, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestValidationException exception)
        {
            return onFailure(Failure(SmsSendOutcome.PermanentFailure, SmsErrorCategory.Configuration, "endpoint_invalid", exception.Message));
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(xml, Encoding.UTF8, MutlucellGateway.ContentType)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return onFailure(Failure(SmsSendOutcome.TransientFailure, SmsErrorCategory.Timeout, "timeout", "Mutlucell zaman aşımı; sunucu yanıt vermedi."));
        }
        catch (HttpRequestException exception)
        {
            return onFailure(Failure(SmsSendOutcome.TransientFailure, SmsErrorCategory.Transport, "transport_error", "Mutlucell'e bağlanılamadı: " + exception.Message));
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string body;
            try
            {
                await response.Content.LoadIntoBufferAsync(65_536, linked.Token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                return onFailure(Failure(SmsSendOutcome.TransientFailure, SmsErrorCategory.Timeout, "timeout", "Mutlucell yanıtı zamanında okunamadı.", status));
            }
            catch (HttpRequestException)
            {
                return onFailure(Failure(SmsSendOutcome.PermanentFailure, SmsErrorCategory.InvalidResponse, "response_too_large", "Mutlucell yanıtı beklenmedik biçimde büyük.", status));
            }

            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500;
                var category = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SmsErrorCategory.Authentication,
                    HttpStatusCode.TooManyRequests => SmsErrorCategory.RateLimited,
                    HttpStatusCode.RequestTimeout => SmsErrorCategory.Timeout,
                    _ when status >= 500 => SmsErrorCategory.ProviderUnavailable,
                    _ => SmsErrorCategory.ProviderRejected
                };
                return onFailure(Failure(transient ? SmsSendOutcome.TransientFailure : SmsSendOutcome.PermanentFailure, category,
                    $"http_{status}", $"Mutlucell HTTP {status} döndürdü.", status, MutlucellGateway.Truncate(body.Trim())));
            }
            return parse(body, status);
        }
    }

    private static SmsSendResult Failure(SmsSendOutcome outcome, SmsErrorCategory category, string code, string message,
        int? status = null, string? raw = null) =>
        new(outcome, ErrorCategory: category, ErrorCode: code, ErrorMessage: message, HttpStatusCode: status, RawResponse: raw);
}
