using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;

namespace Yemekhane.Sync;

public sealed class HttpSyncTransport(HttpClient httpClient) : ISyncTransport
{
    public const string IdempotencyHeaderName = "Idempotency-Key";

    public async Task<SyncTransportResult> SendAsync(SyncRequestOperation operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "sync/operations")
        {
            Content = JsonContent.Create(operation)
        };
        request.Headers.TryAddWithoutValidation(IdempotencyHeaderName, operation.OperationId.ToString("D"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new SyncTransportResult(SyncTransportOutcome.TransientFailure,
                "transport_error", "Sync servisine ulaşılamadı.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SyncTransportResult(SyncTransportOutcome.TransientFailure,
                "transport_timeout", "Sync servisi zaman aşımına uğradı.");
        }

        using (response)
        {
            var server = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.AlreadyReported)
                return new SyncTransportResult(SyncTransportOutcome.Duplicate, server?.ErrorCode, server?.Message);
            if (response.StatusCode == HttpStatusCode.Conflict)
                return new SyncTransportResult(SyncTransportOutcome.Conflict, server?.ErrorCode,
                    server?.Message, server?.Conflict);
            if (response.IsSuccessStatusCode)
                return server is null
                    ? new SyncTransportResult(SyncTransportOutcome.Success)
                    : new SyncTransportResult(server.Outcome, server.ErrorCode, server.Message, server.Conflict);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
                return new SyncTransportResult(SyncTransportOutcome.TransientFailure,
                    server?.ErrorCode ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), server?.Message);

            return new SyncTransportResult(SyncTransportOutcome.PermanentFailure,
                server?.ErrorCode ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), server?.Message);
        }
    }

    private static async Task<SyncServerResponse?> ReadResponseAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0) return null;
        try
        {
            await response.Content.LoadIntoBufferAsync(1_048_576, cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<SyncServerResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException)
        {
            return null;
        }
    }
}
