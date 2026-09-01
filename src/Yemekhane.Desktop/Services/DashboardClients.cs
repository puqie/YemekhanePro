using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.IO;
using Microsoft.AspNetCore.SignalR.Client;
using Yemekhane.Application.Dashboard;
using Yemekhane.Application.Realtime;

namespace Yemekhane.Desktop.Services;

public interface IJwtSession
{
    string? AccessToken { get; }
    bool IsAuthenticated { get; }
}

public sealed class EnvironmentJwtSession : IJwtSession
{
    public string? AccessToken { get; } = Environment.GetEnvironmentVariable("YEMEKHANE_ACCESS_TOKEN");
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed class LoginRequiredException : Exception;

/// <summary>
/// Sunucunun reddettigi bir istek. Mesaj API'nin ProblemDetails govdesinden gelir
/// ("Bu ogrenci numarasi zaten kullaniliyor." gibi) ve DOGRUDAN kullaniciya gosterilir.
///
/// EnsureSuccessStatusCode bu metni atar; kullanici yalnizca "bir hata olustu" gorur
/// ve hangi alani duzeltecegini bilemez. Bu tip metni tasir.
/// </summary>
public sealed class ApiRequestException(string message, System.Net.HttpStatusCode statusCode)
    : Exception(message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Sunucu hatalarini kullaniciya gosterilebilir mesaja cevirir.</summary>
public static class ApiErrors
{
    /// <summary>
    /// Basarisiz yanittan ProblemDetails basligini okur. Govde okunamazsa
    /// duruma gore anlasilir bir yedek mesaj uretilir.
    /// </summary>
    public static async Task<ApiRequestException> ReadAsync(
        HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        string? title = null;
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<ProblemBody>(cancellationToken: cancellationToken);
            title = problem?.Title;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // Govde ProblemDetails degil; yedek mesaja dusulur.
        }

        return new ApiRequestException(
            string.IsNullOrWhiteSpace(title) ? Fallback(response.StatusCode) : title!,
            response.StatusCode);
    }

    private static string Fallback(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Conflict => "Bu kayıt zaten mevcut.",
        System.Net.HttpStatusCode.BadRequest => "Girilen bilgiler geçersiz.",
        System.Net.HttpStatusCode.NotFound => "Kayıt bulunamadı.",
        _ => "İstek işlenemedi. Lütfen tekrar deneyin."
    };

    private sealed record ProblemBody(string? Title, string? Detail);
}

public interface IDashboardApiClient
{
    Task<DashboardSnapshot> GetAsync(CancellationToken cancellationToken = default);
    Task<ConnectivityStatus> GetConnectivityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectivityStatus("Available", "Ready"));
}

public sealed record ConnectivityStatus(string LocalApi, string Cloud);

public sealed class DashboardApiClient(HttpClient client, IJwtSession session) : IDashboardApiClient
{
    public async Task<DashboardSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/dashboard");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardSnapshot>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Dashboard yanıtı boş döndü.");
    }

    public async Task<ConnectivityStatus> GetConnectivityAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("health", cancellationToken);
        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Sağlık yanıtı boş döndü.");
        return new ConnectivityStatus(health.LocalApi, health.Cloud);
    }

    private sealed record HealthResponse(string LocalApi, string Cloud);
}

public enum RealtimeConnectionState { Disconnected, Connecting, Connected, Reconnecting }

public interface IDashboardRealtimeClient
{
    event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
    event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
    event EventHandler<RealtimeConnectionState>? StateChanged;
    Task ConnectAsync(CancellationToken cancellationToken = default);
}

public interface INotificationRealtimeClient
{
    event EventHandler<NotificationEvent>? NotificationReceived;
    event EventHandler<RealtimeConnectionState>? StateChanged;
}

public sealed class DashboardRealtimeClient : IDashboardRealtimeClient, INotificationRealtimeClient, IAsyncDisposable
{
    private readonly HubConnection connection;
    public event EventHandler<AccessDecisionCommittedEvent>? AccessReceived;
    public event EventHandler<DeviceStatusChangedEvent>? DeviceStatusChanged;
    public event EventHandler<NotificationEvent>? NotificationReceived;
    public event EventHandler<RealtimeConnectionState>? StateChanged;

    public DashboardRealtimeClient(Uri baseUri, IJwtSession session)
    {
        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/realtime"), options =>
                options.AccessTokenProvider = () => Task.FromResult(session.AccessToken))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)])
            .Build();
        connection.On<AccessDecisionCommittedEvent>("AccessDecisionCommitted", value => AccessReceived?.Invoke(this, value));
        connection.On<DeviceStatusChangedEvent>("DeviceStatusChanged", value => DeviceStatusChanged?.Invoke(this, value));
        connection.On<NotificationEvent>("Notification", value => NotificationReceived?.Invoke(this, value));
        connection.Reconnecting += _ => { StateChanged?.Invoke(this, RealtimeConnectionState.Reconnecting); return Task.CompletedTask; };
        connection.Reconnected += async _ => { await SubscribeAsync(); StateChanged?.Invoke(this, RealtimeConnectionState.Connected); };
        connection.Closed += _ => { StateChanged?.Invoke(this, RealtimeConnectionState.Disconnected); return Task.CompletedTask; };
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        StateChanged?.Invoke(this, RealtimeConnectionState.Connecting);
        try
        {
            await connection.StartAsync(cancellationToken);
            await SubscribeAsync(cancellationToken);
            StateChanged?.Invoke(this, RealtimeConnectionState.Connected);
        }
        catch
        {
            StateChanged?.Invoke(this, RealtimeConnectionState.Disconnected);
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken = default)
    {
        await connection.InvokeAsync("Subscribe", RealtimeChannels.AccessDecisions, cancellationToken);
        await connection.InvokeAsync("Subscribe", RealtimeChannels.DeviceStatuses, cancellationToken);
        await connection.InvokeAsync("Subscribe", RealtimeChannels.Notifications, cancellationToken);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
