using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using Yemekhane.Application.Devices;

namespace Yemekhane.Desktop.Services;

public interface IDeviceCardsApiClient
{
    Task<IReadOnlyList<DeviceCardSummary>> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId, CancellationToken cancellationToken = default);
    Task ResyncCardAsync(Guid cardId, CancellationToken cancellationToken = default);
    Task PushNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Cihazdaki kart listesi (yuklu / bekleyen / hatali; arama + sayfalama). Ayri arayuz: IDeviceCardsApiClient'in
/// sahteleri cihaz testlerinde yasiyor; oraya uye eklemek onlari kirardi. Gercek istemci ikisini de uygular.
/// </summary>
public interface IDeviceCardListApiClient
{
    Task<DeviceCardListResult> GetCardsAsync(Guid deviceId, string? search, int page, int pageSize,
        CancellationToken cancellationToken = default);
}

public sealed class DeviceCardsApiClient(HttpClient client, IJwtSession session) : IDeviceCardsApiClient, IDeviceCardListApiClient
{
    public Task<IReadOnlyList<DeviceCardSummary>> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DeviceCardSummary>("api/device-cards/summary", cancellationToken);

    public async Task<DeviceCardListResult> GetCardsAsync(Guid deviceId, string? search, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) query += "&search=" + Uri.EscapeDataString(search.Trim());
        using var response = await client.SendAsync(
            Request(HttpMethod.Get, $"api/device-cards/{deviceId:D}/cards?{query}"), cancellationToken);
        Ensure(response);
        return await response.Content.ReadFromJsonAsync<DeviceCardListResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Cihaz kart listesi yanıtı boş döndü.");
    }

    public Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit,
        CancellationToken cancellationToken = default) =>
        GetAsync<PendingDeviceCard>($"api/device-cards/{deviceId:D}/pending?limit={limit}", cancellationToken);

    public Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId,
        CancellationToken cancellationToken = default) =>
        GetAsync<DeviceCardStatusRow>($"api/device-cards/cards/{cardId:D}", cancellationToken);

    public async Task ResyncCardAsync(Guid cardId, CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(
            Request(HttpMethod.Post, $"api/device-cards/cards/{cardId:D}/resync"), cancellationToken);
        Ensure(response);
    }

    public async Task PushNowAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Post, "api/device-cards/push"), cancellationToken);
        Ensure(response);
    }

    private async Task<IReadOnlyList<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, path), cancellationToken);
        Ensure(response);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Kart durumu yanıtı boş döndü.");
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
    }
}
