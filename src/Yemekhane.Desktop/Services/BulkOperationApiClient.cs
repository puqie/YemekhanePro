using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Meals;

namespace Yemekhane.Desktop.Services;

public interface IBulkOperationApiClient
{
    Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default);
    Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken = default);
    Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest request, CancellationToken cancellationToken = default);
    Task<BulkOperationHistoryPage> HistoryAsync(CancellationToken cancellationToken = default);
    Task<UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class BulkOperationApiClient(HttpClient client, IJwtSession session) : IBulkOperationApiClient
{
    public Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyCollection<CalendarScopeOption>>("api/calendar/scopes", cancellationToken);
    public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<MealTypeDetails>>("api/meal-types?includeInactive=false", cancellationToken);
    public Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BulkOperationPreview>(HttpMethod.Post, "api/bulk-operations/preview", request, cancellationToken);
    public Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BulkOperationResult>(HttpMethod.Post, "api/bulk-operations/apply", request, cancellationToken);
    public Task<BulkOperationHistoryPage> HistoryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<BulkOperationHistoryPage>("api/bulk-operations?page=1&pageSize=30", cancellationToken);
    public Task<UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<UndoBulkOperationResult>(HttpMethod.Post, $"api/bulk-operations/{id:D}/undo", new { }, cancellationToken);

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, url); return await ReadAsync<T>(request, cancellationToken);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string url, object body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url); request.Content = JsonContent.Create(body); return await ReadAsync<T>(request, cancellationToken);
    }
    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken); return request;
    }
    private async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Toplu işlem API yanıtı boş döndü.");
    }
}
