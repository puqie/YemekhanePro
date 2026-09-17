using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Calendar;

using Yemekhane.Application.Leaves;
using Yemekhane.Application.Students;
using Yemekhane.Application.Common;
namespace Yemekhane.Desktop.Services;

public interface ICalendarApiClient
{
    /// <param name="classKind">Sinif turu suzgeci; bos = herkes, "Normal" = ilkokul, "Anasinifi" = anasinifi.</param>
    Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, string? classKind = null, CancellationToken cancellationToken = default);
    Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, string? classKind = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default);
    Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default);
    Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default);
    /// <summary>Tatil satirini, <paramref name="wholeRange"/> ile ayni araligin tum gunlerini siler (204).</summary>
    Task DeleteHolidayAsync(Guid id, bool wholeRange, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Ogrenciye ozel tatil (izin) icin ogrenci secimi: ad, soyad, numara, kart ya da sinif adiyla arar.</summary>
    Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Secili ogrencilere ayni tarih araliginda izin ("ogrenciye ozel tatil").</summary>
    Task<BulkLeaveResult> CreateLeavesAsync(CreateBulkLeaveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Tarih araligina degen izinler, ogrenci adiyla (gun cekmecesi).</summary>
    Task<IReadOnlyList<LeaveListRow>> LeavesInRangeAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LeaveListRow>>([]);
    Task DeleteLeaveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed class CalendarApiClient(HttpClient client, IJwtSession session) : ICalendarApiClient
{
    public Task<MonthlyCalendar> GetMonthAsync(DateOnly month, CalendarScopeOption? scope, string? classKind = null, CancellationToken cancellationToken = default) =>
        GetAsync<MonthlyCalendar>($"api/calendar/month?month={month:yyyy-MM}{ScopeQuery(scope)}{KindQuery(classKind)}", cancellationToken);
    public Task<CalendarDayDetails> GetDayAsync(DateOnly calendarDate, CalendarScopeOption? scope, string? classKind = null, CancellationToken cancellationToken = default) =>
        GetAsync<CalendarDayDetails>($"api/calendar/day/{calendarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}?{(ScopeQuery(scope) + KindQuery(classKind)).TrimStart('&')}", cancellationToken);

    private static string KindQuery(string? classKind) =>
        string.IsNullOrWhiteSpace(classKind) ? "" : "&classKind=" + Uri.EscapeDataString(classKind);
    public Task<IReadOnlyCollection<CalendarScopeOption>> GetScopesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyCollection<CalendarScopeOption>>("api/calendar/scopes", cancellationToken);
    public Task<HolidayDetails> CreateHolidayAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CreateHolidayRequest, HolidayDetails>("api/holidays", request, cancellationToken);
    public Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CreateScheduleExceptionRequest, CalendarExceptionItem>("api/calendar/exceptions", request, cancellationToken);

    public async Task DeleteHolidayAsync(Guid id, bool wholeRange, CancellationToken cancellationToken = default)
    {
        // 204 NoContent doner; govde okuyan SendAsync<T> kullanilamaz.
        using var request = Authorized(HttpMethod.Delete, $"api/holidays/{id:D}?wholeRange={(wholeRange ? "true" : "false")}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
    }

    public Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<StudentListItem>>($"api/students?search={Uri.EscapeDataString(term)}&isActive=true&page=1&pageSize=100", cancellationToken);

    public Task<BulkLeaveResult> CreateLeavesAsync(CreateBulkLeaveRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CreateBulkLeaveRequest, BulkLeaveResult>("api/leaves/bulk", request, cancellationToken);

    public Task<IReadOnlyList<LeaveListRow>> LeavesInRangeAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<LeaveListRow>>($"api/leaves?from={rangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&to={rangeEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", cancellationToken);

    public async Task DeleteLeaveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Delete, $"api/leaves/{id:D}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
    }

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
        // Sunucu mesaji ("Tatil adi 2-200 karakter olmalidir.") kullaniciya ulassin:
        // HttpRequestException "cevrimdisi" sayiliyor ve mesaj yutuluyordu.
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Takvim API yanıtı boş döndü.");
    }
}
