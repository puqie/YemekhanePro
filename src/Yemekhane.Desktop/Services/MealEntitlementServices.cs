using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Students;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Common;

namespace Yemekhane.Desktop.Services;

public interface IMealEntitlementApiClient
{
    Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default);
    /// <summary>
    /// Hizli Hakedis ogrenci secimi: ad, soyad, numara, kart ya da SINIF adiyla arar.
    /// Her ogrenci tek satirdir (hakedis listesi ogrenci-gun satiridir, oradan secilemez).
    /// </summary>
    Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bu istemci öğrenci aramayı desteklemiyor.");
    Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default);
    Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default);
    Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default);
    Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ogrencinin ogun bazinda kalan hakki, bitis gunu ve yenileme gunu. Veli telefondayken
    /// bakilacak ozet. Varsayilan govde BOS liste doner: eski sahte istemciler kirilmasin.
    /// </summary>
    Task<IReadOnlyList<EntitlementPeriodSummary>> PeriodsAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntitlementPeriodSummary>>([]);

    /// <summary>Hakedisi bitmek uzere olan (ve bitmis) ogrenciler.</summary>
    Task<IReadOnlyList<EntitlementPeriodSummary>> ExpiringAsync(ExpiringEntitlementQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntitlementPeriodSummary>>([]);
}

public sealed class MealEntitlementApiClient(HttpClient client, IJwtSession session) : IMealEntitlementApiClient
{
    public Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default)
    {
        // Sinif adiyla da bulunabilsin diye hem serbest arama hem sinif adi gonderilir;
        // sunucu ikisini AND'ler, bu yuzden once serbest arama denenir.
        var values = new Dictionary<string, string?>
        {
            ["search"] = term, ["isActive"] = "true", ["page"] = "1", ["pageSize"] = "100"
        };
        return GetAsync<PagedResult<StudentListItem>>("api/students?" + string.Join("&", values
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")), cancellationToken);
    }

    public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["startsOn"] = query.StartsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endsOn"] = query.EndsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["studentNo"] = query.StudentNo, ["cardNumber"] = query.CardNumber, ["name"] = query.Name,
            ["className"] = query.ClassName, ["groupId"] = query.GroupId?.ToString(), ["mealTypeId"] = query.MealTypeId?.ToString(),
            // "search" EKSIKTI: ekranin tek arama kutusu doldurulup Filtrele'ye basildiginda
            // metin sunucuya hic gitmiyor, liste filtresiz geliyordu. Kullanici aradigi ismi
            // yazdigi halde butun satirlarin kalmasinin nedeni buydu.
            ["search"] = query.Search,
            ["status"] = query.Status, ["page"] = query.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture), ["sortBy"] = query.SortBy,
            ["descending"] = query.Descending.ToString(CultureInfo.InvariantCulture)
        };
        return GetAsync<MealEntitlementPage>("api/meal-entitlements?" + string.Join("&", values
            .Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")), cancellationToken);
    }

    public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<MealTypeDetails>>("api/meal-types?includeInactive=false", cancellationToken);
    public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ClassRecord>>("api/organization/classes", cancellationToken);
    public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<GroupRecord>>("api/organization/groups", cancellationToken);
    public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<EntitlementGrantRequest, EntitlementPreview>("api/meal-entitlements/preview", request, cancellationToken);
    public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ApplyEntitlementGrantRequest, BulkEntitlementResult>("api/meal-entitlements/apply", request, cancellationToken);
    public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<CancelEntitlementsRequest, CancelEntitlementsResult>("api/meal-entitlements/cancel", request, cancellationToken);

    public Task<IReadOnlyList<EntitlementPeriodSummary>> PeriodsAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<EntitlementPeriodSummary>>($"api/meal-entitlements/student/{studentId}/periods", cancellationToken);

    public Task<IReadOnlyList<EntitlementPeriodSummary>> ExpiringAsync(ExpiringEntitlementQuery query, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["withinDays"] = query.WithinDays.ToString(CultureInfo.InvariantCulture),
            ["mealTypeId"] = query.MealTypeId?.ToString(),
            ["classKind"] = query.ClassKind,
            ["search"] = query.Search
        };
        return GetAsync<IReadOnlyList<EntitlementPeriodSummary>>("api/meal-entitlements/expiring?" + string.Join("&", values
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")), cancellationToken);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, url);
        return await SendAsync<T>(request, cancellationToken);
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        // Sunucunun Turkce dogrulama mesaji ("Gunluk ogun hakki 1-10 arasinda olmalidir.")
        // ProblemDetails govdesindedir. Onceden HttpRequestException firlatiliyor, ViewModel
        // bunu "cevrimdisi" sanip yalnizca "Onizleme olusturulamadi." diyordu -- kullanici
        // neyi duzelteceğini bilemiyordu. ApiRequestException mesaji oldugu gibi tasir.
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Hakediş API yanıtı boş döndü.");
    }
}
