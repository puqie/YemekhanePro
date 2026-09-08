using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;

namespace Yemekhane.Desktop.Services;

/// <summary>
/// Tanimlar ekraninin sunucu istemcisi: ogunler (api/meal-types) ve
/// sinif/sube/bolum/gorev tanimlari (api/organization/{kind}).
/// </summary>
public interface IDefinitionsApiClient
{
    Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<MealTypeDetails> CreateMealTypeAsync(SaveMealTypeRequest request, CancellationToken cancellationToken = default);
    Task<MealTypeDetails> UpdateMealTypeAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken = default);
    Task DeactivateMealTypeAsync(Guid id, CancellationToken cancellationToken = default);
    /// <param name="kind">classes | sections | departments | jobs</param>
    Task<IReadOnlyList<LookupRecord>> LookupsAsync(string kind, CancellationToken cancellationToken = default);
    /// <param name="classKind">Yalnizca classes icin: Normal | Anasinifi (bos → Normal).</param>
    Task<LookupRecord> CreateLookupAsync(string kind, string name, string? classKind = null, CancellationToken cancellationToken = default);
    /// <param name="classKind">Yalnizca classes icin; bos birakilirsa mevcut tur korunur.</param>
    Task<LookupRecord> RenameLookupAsync(string kind, Guid id, string name, string? classKind = null, CancellationToken cancellationToken = default);
    Task DeleteLookupAsync(string kind, Guid id, CancellationToken cancellationToken = default);
}

public sealed class DefinitionsApiClient(HttpClient client, IJwtSession session) : IDefinitionsApiClient
{
    public const string Classes = "classes";
    public const string Sections = "sections";
    public const string Departments = "departments";
    public const string Jobs = "jobs";

    public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<MealTypeDetails>>(Authorized(HttpMethod.Get, $"api/meal-types?includeInactive={(includeInactive ? "true" : "false")}"), cancellationToken);

    public Task<MealTypeDetails> CreateMealTypeAsync(SaveMealTypeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<MealTypeDetails>(Authorized(HttpMethod.Post, "api/meal-types", JsonContent.Create(request)), cancellationToken);

    public Task<MealTypeDetails> UpdateMealTypeAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<MealTypeDetails>(Authorized(HttpMethod.Put, $"api/meal-types/{id:D}", JsonContent.Create(request)), cancellationToken);

    public Task DeactivateMealTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(Authorized(HttpMethod.Delete, $"api/meal-types/{id:D}"), cancellationToken);

    public Task<IReadOnlyList<LookupRecord>> LookupsAsync(string kind, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<LookupRecord>>(Authorized(HttpMethod.Get, $"api/organization/{kind}/lookups"), cancellationToken);

    public Task<LookupRecord> CreateLookupAsync(string kind, string name, string? classKind = null, CancellationToken cancellationToken = default) =>
        // Sinif turu (Normal / Anasinifi) yalnizca Tanimlar ekranindan secilir; bu yuzden sinif
        // "classes/lookups" ucuna {"name","kind"} govdesiyle gider. Eski POST classes ucu
        // (duz JSON dizge, ClassRecord yaniti) ogrenci formundaki hizli ekleme icin durur.
        SendAsync<LookupRecord>(Authorized(HttpMethod.Post, kind == Classes ? "api/organization/classes/lookups" : $"api/organization/{kind}",
            JsonContent.Create(new SaveLookupRequest(name, kind == Classes ? classKind : null))), cancellationToken);

    public Task<LookupRecord> RenameLookupAsync(string kind, Guid id, string name, string? classKind = null, CancellationToken cancellationToken = default) =>
        SendAsync<LookupRecord>(Authorized(HttpMethod.Put, $"api/organization/{kind}/{id:D}", JsonContent.Create(new SaveLookupRequest(name, kind == Classes ? classKind : null))), cancellationToken);

    public Task DeleteLookupAsync(string kind, Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(Authorized(HttpMethod.Delete, $"api/organization/{kind}/{id:D}"), cancellationToken);

    private HttpRequestMessage Authorized(HttpMethod method, string url, HttpContent? content = null)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Tanım API yanıtı boş döndü.");
        }
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        // 409 "Sınıf 12 öğrencide kullanılıyor; önce öğrencileri başka bir tanıma taşıyın." gibi
        // sunucu mesajlari ProblemDetails basligindadir; ApiRequestException bunu oldugu gibi
        // tasir ve ekranda AYNEN gosterilir. Genel "istek basarisiz" mesaji kullaniciya neyi
        // duzelteceğini soylemez.
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
    }
}
