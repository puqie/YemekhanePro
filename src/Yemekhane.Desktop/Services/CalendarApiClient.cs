using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Calendar;

namespace Yemekhane.Desktop.Services;

public interface ICalendarApiClient
{
    Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, CancellationToken cancellationToken = default);
    Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default);
    Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default);
    Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default);
}

public sealed class CalendarApiClient(HttpClient client, IJwtSession session) : ICalendarApiClient
{
    public Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, CancellationToken cancellationToken = default) =>
        GetAsync<MonthlyCalendar>($"api/calendar/month?month={month:yyyy-MM}{ScopeQuery(scope)}", cancellationToken);
    public Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, CancellationToken cancellationToken = default) =>
        GetAsync<CalendarDayDetails>($"api/calendar/day/{calendarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}?{ScopeQuery(scope).TrimStart('&')}", cancellationToken);
    public Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyCollection<CalendarScopeOption>>("api/calendar/scopes", cancellationToken);
    public Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CreateHolidayRequest, HolidayDetails>("api/holidays", request, cancellationToken);
    public Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CreateScheduleExceptionRequest, CalendarExceptionItem>("api/calendar/exceptions", request, cancellationToken);

    private static string ScopeQuery(CalendarScopeOption? scope) => scope is null || scope.ScopeType == "AllSchool" ? "" :
        $"&scopeType={Uri.EscapeDataString(scope.ScopeType)}&scopeId={scope.ScopeId:D}";
    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, url); return await SendAsync<T>(request, cancellationToken);
    }
    private async Task<TOut> PostAsync<TIn, TOut>(string url, TIn value, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, url); request.Content = JsonContent.Create(value);
        return await SendAsync<TOut>(request, cancellationToken);
    }
    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken); return request;
    }
    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync(cancellationToken), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Takvim API yanıtı boş döndü.");
    }
}
