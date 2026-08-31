using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Yemekhane.Desktop.Services;

public sealed record DeviceItem(Guid Id, string Name, string DeviceType, string ConnectionType,
    string Endpoint, string? IpAddress, int? Port, string? ComPort, int? BaudRate, bool IsActive,
    bool AutoConnect, bool HasTurnstile, string? Location, string Direction, string Status,
    DateTimeOffset? LastConnectedAt, DateTimeOffset? LastStatusAt, string? Model, string? SerialNumber,
    string? Firmware, bool IsSimulator);
public sealed record DeviceWriteModel(string Name, string DeviceType, string ConnectionType,
    string? IpAddress, int? Port, string? ComPort, int? BaudRate, bool IsActive, bool AutoConnect,
    bool HasTurnstile, string? Location, string Direction);
public sealed record DeviceActionResponse(bool Succeeded, string Status, string Message,
    string? ErrorCode, DeviceItem? Device);
public sealed record DeviceLogItem(Guid Id, DateTimeOffset Timestamp, string EventType, string Severity,
    string Message, string? PayloadJson);
public sealed record DeviceCapabilities(bool SimulatorAllowed);

public interface IDeviceApiClient
{
    Task<IReadOnlyList<DeviceItem>> ListAsync(CancellationToken cancellationToken = default);
    Task<DeviceCapabilities> CapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<DeviceItem> CreateAsync(DeviceWriteModel model, CancellationToken cancellationToken = default);
    Task<DeviceItem> UpdateAsync(Guid id, DeviceWriteModel model, CancellationToken cancellationToken = default);
    Task<DeviceItem> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DeviceActionResponse> ActionAsync(Guid id, string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceLogItem>> LogsAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class DeviceApiClient(HttpClient client, IJwtSession session) : IDeviceApiClient
{
    public Task<IReadOnlyList<DeviceItem>> ListAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<DeviceItem>>(HttpMethod.Get, "api/devices", null, cancellationToken);
    public Task<DeviceCapabilities> CapabilitiesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<DeviceCapabilities>(HttpMethod.Get, "api/devices/capabilities", null, cancellationToken);
    public Task<DeviceItem> CreateAsync(DeviceWriteModel model, CancellationToken cancellationToken = default) =>
        SendAsync<DeviceItem>(HttpMethod.Post, "api/devices", model, cancellationToken);
    public Task<DeviceItem> UpdateAsync(Guid id, DeviceWriteModel model, CancellationToken cancellationToken = default) =>
        SendAsync<DeviceItem>(HttpMethod.Put, $"api/devices/{id}", model, cancellationToken);
    public Task<DeviceItem> DeactivateAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<DeviceItem>(HttpMethod.Delete, $"api/devices/{id}", null, cancellationToken);
    public Task<DeviceActionResponse> ActionAsync(Guid id, string action, CancellationToken cancellationToken = default) =>
        SendAsync<DeviceActionResponse>(HttpMethod.Post, $"api/devices/{id}/{action}", null, cancellationToken);
    public Task<IReadOnlyList<DeviceLogItem>> LogsAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<DeviceLogItem>>(HttpMethod.Get, $"api/devices/{id}/logs?take=200", null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken token)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(token), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidDataException("Cihaz API yanıtı boş döndü.");
    }
}
