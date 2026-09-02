using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;

namespace Yemekhane.Desktop.Services;

public interface ISmsApiClient
{
    Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default);
    Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default);
    Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmsTemplateDetails>> TemplatesAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<SmsTemplateDetails> SaveTemplateAsync(Guid? id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default);
    Task DeactivateTemplateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<SmsLogDetails>> HistoryAsync(SmsHistoryFilter filter, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class SmsApiClient(HttpClient client, IJwtSession session) : ISmsApiClient
{
    public Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default) =>
        GetAsync<SmsTargetOptions>("api/sms/targets" + (string.IsNullOrWhiteSpace(search) ? "" : "?search=" + Uri.EscapeDataString(search.Trim())), cancellationToken);
    public Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BulkSmsPreview>(HttpMethod.Post, "api/sms/bulk/preview", request, cancellationToken);
    public Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BulkSmsEnqueueResult>(HttpMethod.Post, "api/sms/bulk/apply", request, cancellationToken);
    public Task<IReadOnlyList<SmsTemplateDetails>> TemplatesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<SmsTemplateDetails>>(includeInactive ? "api/sms-templates?includeInactive=true" : "api/sms/templates", cancellationToken);
    public Task<SmsTemplateDetails> SaveTemplateAsync(Guid? id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SmsTemplateDetails>(id.HasValue ? HttpMethod.Put : HttpMethod.Post,
            id.HasValue ? $"api/sms-templates/{id:D}" : "api/sms-templates", request, cancellationToken);
    public Task DeactivateTemplateAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/sms-templates/{id:D}", null, cancellationToken);
    public Task<PagedResult<SmsLogDetails>> HistoryAsync(SmsHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["status"] = filter.Status, ["phone"] = filter.Phone, ["provider"] = filter.Provider,
            ["student"] = filter.Student, ["from"] = filter.From?.ToString("O"), ["to"] = filter.To?.ToString("O"),
            ["page"] = filter.Page.ToString(CultureInfo.InvariantCulture), ["pageSize"] = filter.PageSize.ToString(CultureInfo.InvariantCulture),
            ["studentId"] = filter.StudentId?.ToString("D")
        };
        return GetAsync<PagedResult<SmsLogDetails>>("api/sms?" + string.Join("&", values.Where(x => x.Value is not null)
            .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}")), cancellationToken);
    }
    public Task RetryAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/sms/{id:D}/retry", null, cancellationToken);

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("SMS API yanıtı boş döndü.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url); request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken); await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("SMS API yanıtı boş döndü.");
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url); if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken); await EnsureAsync(response, cancellationToken);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }
    /// <summary>
    /// Basarisiz yanitta sunucunun ProblemDetails mesajini tasiyan ApiRequestException firlatir.
    /// Onceki EnsureSuccessStatusCode, "Mesaj veya şablondan yalnız biri seçilmelidir." gibi
    /// dogrulama mesajlarini HttpRequestException'a cevirip atiyordu; ViewModel bunu
    /// "SMS servisine ulaşılamadı" + Cevrimdisi rozeti olarak gosteriyordu -- kullanici
    /// ne yanlis yaptigini ogrenemiyordu.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
    }
}
