using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;

namespace Yemekhane.Desktop.Services;

public interface ICashApiClient
{
    Task<CashSummary> SummaryAsync(CashSummaryPeriod period, DateOnly? anchorDate = null, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default);
    Task<PagedResult<IncomeTransactionDetails>> TransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IncomeTypeDetails>> TypesAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<IncomeTransactionDetails> AddAsync(CreateIncomeTransactionRequest request, CancellationToken cancellationToken = default);
    Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, CancellationToken cancellationToken = default);
    Task<IncomeTypeDetails> SaveTypeAsync(Guid? id, SaveIncomeTypeRequest request, CancellationToken cancellationToken = default);
    Task DeactivateTypeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<StudentListItem>> FindStudentAsync(string? studentNumber, string? cardNumber, CancellationToken cancellationToken = default);
}

public sealed class CashApiClient(HttpClient client, IJwtSession session) : ICashApiClient
{
    public Task<CashSummary> SummaryAsync(CashSummaryPeriod period, DateOnly? anchorDate = null, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["period"] = period.ToString(), ["date"] = anchorDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["from"] = startDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["to"] = endDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        return GetAsync<CashSummary>("api/cash/summary?" + Query(values), cancellationToken);
    }

    public Task<PagedResult<IncomeTransactionDetails>> TransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["from"] = filter.From?.ToString("O"), ["to"] = filter.To?.ToString("O"),
            ["incomeTypeId"] = filter.IncomeTypeId?.ToString("D"), ["studentId"] = filter.StudentId?.ToString("D"),
            ["cardNumber"] = filter.CardNumber, ["isVoided"] = filter.IsVoided?.ToString().ToLowerInvariant(),
            ["page"] = filter.Page.ToString(CultureInfo.InvariantCulture), ["pageSize"] = filter.PageSize.ToString(CultureInfo.InvariantCulture)
        };
        return GetAsync<PagedResult<IncomeTransactionDetails>>("api/income/transactions?" + Query(values), cancellationToken);
    }

    public Task<IReadOnlyList<IncomeTypeDetails>> TypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<IncomeTypeDetails>>("api/income/types?includeInactive=" + includeInactive.ToString().ToLowerInvariant(), cancellationToken);

    public Task<IncomeTransactionDetails> AddAsync(CreateIncomeTransactionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<IncomeTransactionDetails>(HttpMethod.Post, "api/income/transactions", request, cancellationToken);
    public Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
        SendAsync<IncomeTransactionDetails>(HttpMethod.Post, $"api/income/transactions/{id:D}/void", new VoidIncomeTransactionRequest(reason), cancellationToken);
    public Task<IncomeTypeDetails> SaveTypeAsync(Guid? id, SaveIncomeTypeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<IncomeTypeDetails>(id.HasValue ? HttpMethod.Put : HttpMethod.Post,
            id.HasValue ? $"api/income/types/{id:D}" : "api/income/types", request, cancellationToken);
    public Task DeactivateTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/income/types/{id:D}", cancellationToken);

    public Task<PagedResult<StudentListItem>> FindStudentAsync(string? studentNumber, string? cardNumber, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["studentNo"] = studentNumber, ["cardNumber"] = cardNumber, ["isActive"] = "true", ["page"] = "1", ["pageSize"] = "2"
        };
        return GetAsync<PagedResult<StudentListItem>>("api/students?" + Query(values), cancellationToken);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, cancellationToken); Ensure(response);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Kasa API yanıtı boş döndü.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url); request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken); Ensure(response);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Kasa API yanıtı boş döndü.");
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url);
        using var response = await client.SendAsync(request, cancellationToken); Ensure(response);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private static string Query(IEnumerable<KeyValuePair<string, string?>> values) => string.Join("&", values
        .Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}"));
    private static void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
    }
}
