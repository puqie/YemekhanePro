using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Cards;

namespace Yemekhane.Desktop.Services;

/// <summary>Kartlar ekraninin sunucu istemcisi: liste + pasiflestir + aktiflestir.</summary>
public interface ICardListApiClient
{
    Task<CardListResult> ListAsync(string? search, bool? isActive, int page, int pageSize,
        CancellationToken cancellationToken = default);
    Task DeactivateAsync(Guid cardId, string reason, CancellationToken cancellationToken = default);
    Task<CardDetails> ReactivateAsync(Guid cardId, CancellationToken cancellationToken = default);
}

public sealed class CardListApiClient(HttpClient client, IJwtSession session) : ICardListApiClient
{
    public async Task<CardListResult> ListAsync(string? search, bool? isActive, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) query += "&search=" + Uri.EscapeDataString(search.Trim());
        if (isActive is { } active) query += "&isActive=" + (active ? "true" : "false");
        using var response = await client.SendAsync(Request(HttpMethod.Get, "api/cards?" + query), cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CardListResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Kart listesi yanıtı boş döndü.");
    }

    public async Task DeactivateAsync(Guid cardId, string reason, CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(
            Request(HttpMethod.Delete, $"api/cards/{cardId:D}?reason=" + Uri.EscapeDataString(reason)), cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task<CardDetails> ReactivateAsync(Guid cardId, CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Post, $"api/cards/{cardId:D}/reactivate"), cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CardDetails>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Kart yanıtı boş döndü.");
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    /// <summary>
    /// Sunucunun mesaji KORUNUR ("Öğrencinin zaten aktif kartı var" gibi) ki ekranda aynen
    /// gosterilsin; yalnizca EnsureSuccessStatusCode kullanilsaydi kullanici sebebi gormezdi.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
    }
}
