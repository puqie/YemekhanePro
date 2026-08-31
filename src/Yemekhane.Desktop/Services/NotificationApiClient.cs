using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Desktop.Services;

public interface INotificationApiClient
{
    Task<NotificationPage> ListAsync(int pageSize = 30, string? cursor = null, CancellationToken cancellationToken = default);
    Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkAllReadAsync(CancellationToken cancellationToken = default);
}

public sealed class NotificationApiClient(HttpClient client, IJwtSession session) : INotificationApiClient
{
    public async Task<NotificationPage> ListAsync(int pageSize = 30, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var path = $"api/notifications?pageSize={pageSize}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        using var response = await client.SendAsync(Request(HttpMethod.Get, path), cancellationToken);
        Ensure(response);
        return await response.Content.ReadFromJsonAsync<NotificationPage>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Bildirim yanıtı boş döndü.");
    }

    public async Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default)
    { using var response = await client.SendAsync(Request(HttpMethod.Post, $"api/notifications/{id:D}/read"), cancellationToken); Ensure(response); }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    { using var response = await client.SendAsync(Request(HttpMethod.Post, "api/notifications/read-all"), cancellationToken); Ensure(response); }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
    }
}
