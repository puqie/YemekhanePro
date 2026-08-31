using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Sms;
using Yemekhane.Application.Common;

namespace Yemekhane.Infrastructure.Sms;

public sealed class HttpSmsProvider(
    HttpClient httpClient,
    IOptions<SmsProviderOptions> options,
    ISmsResponseParser responseParser) : ISmsProvider
{
    public async Task<SmsSendResult> SendAsync(
        SmsSendRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuration = options.Value;
        var endpoint = await OutboundEndpointPolicy.ValidateAsync(configuration.Endpoint, configuration.AllowHttp,
            configuration.AllowPrivateNetworks, cancellationToken).ConfigureAwait(false);
        using var httpRequest = new HttpRequestMessage(new HttpMethod(configuration.Method), endpoint);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [configuration.RecipientProperty] = request.Phone,
            [configuration.MessageProperty] = request.Message
        };
        foreach (var property in configuration.AdditionalJsonProperties) payload[property.Key] = property.Value;
        if (!string.IsNullOrWhiteSpace(configuration.Sender)) payload["sender"] = configuration.Sender;
        httpRequest.Content = JsonContent.Create(payload);

        if (configuration.AuthType.Equals("Basic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration.Secret))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{configuration.Username}:{configuration.Secret}")));
        else if (configuration.AuthType.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration.Secret))
            httpRequest.Headers.TryAddWithoutValidation("X-Api-Key", configuration.Secret);
        else if (configuration.AuthType.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration.Secret))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Secret);
        else if (!string.IsNullOrWhiteSpace(configuration.BearerToken))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.BearerToken);
        foreach (var header in configuration.Headers)
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(SmsSendOutcome.TransientFailure, SmsErrorCategory.Timeout, "timeout");
        }
        catch (HttpRequestException)
        {
            return Failure(SmsSendOutcome.TransientFailure, SmsErrorCategory.Transport, "transport_error");
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    statusCode >= 500;
                var category = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SmsErrorCategory.Authentication,
                    HttpStatusCode.TooManyRequests => SmsErrorCategory.RateLimited,
                    HttpStatusCode.RequestTimeout => SmsErrorCategory.Timeout,
                    _ when statusCode >= 500 => SmsErrorCategory.ProviderUnavailable,
                    _ => SmsErrorCategory.ProviderRejected
                };
                return Failure(transient ? SmsSendOutcome.TransientFailure : SmsSendOutcome.PermanentFailure,
                    category, $"http_{statusCode}", statusCode);
            }

            string body;
            try
            {
                await response.Content.LoadIntoBufferAsync(1_048_576, linkedCancellation.Token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failure(SmsSendOutcome.TransientFailure, SmsErrorCategory.Timeout, "timeout");
            }
            catch (HttpRequestException)
            {
                return Failure(SmsSendOutcome.PermanentFailure, SmsErrorCategory.InvalidResponse, "response_too_large");
            }
            try
            {
                return new SmsSendResult(SmsSendOutcome.Success,
                    responseParser.ParseProviderMessageId(body), HttpStatusCode: statusCode);
            }
            catch (JsonException)
            {
                return Failure(SmsSendOutcome.PermanentFailure, SmsErrorCategory.InvalidResponse,
                    "invalid_response", statusCode);
            }
        }
    }

    private static SmsSendResult Failure(SmsSendOutcome outcome, SmsErrorCategory category,
        string code, int? statusCode = null) =>
        new(outcome, ErrorCategory: category, ErrorCode: code, HttpStatusCode: statusCode);
}
