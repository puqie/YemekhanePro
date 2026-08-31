using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yemekhane.Application.Search;

namespace Yemekhane.Desktop.Services;

public interface IGlobalSearchApiClient
{
    Task<GlobalSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default);
}

public sealed class GlobalSearchApiClient(HttpClient client, IJwtSession session) : IGlobalSearchApiClient
{
    public async Task<GlobalSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/search?q=" + Uri.EscapeDataString(query.Trim()));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GlobalSearchResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Arama API yanıtı boş döndü.");
    }
}

public sealed record RecentSearchEntry(string Query, SearchResultItem Result);

public interface IRecentSearchStore
{
    IReadOnlyList<RecentSearchEntry> Load();
    void Add(RecentSearchEntry entry);
}

public sealed class FileRecentSearchStore : IRecentSearchStore
{
    public const int Limit = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path;

    public FileRecentSearchStore(string? path = null)
    {
        this.path = path ?? Path.Combine(Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve(), "recent-searches.json");
    }

    public IReadOnlyList<RecentSearchEntry> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RecentSearchEntry[]>(File.ReadAllText(path), JsonOptions)?.Take(Limit).ToArray() ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public void Add(RecentSearchEntry entry)
    {
        try
        {
            var values = Load().Where(value => value.Result.Route != entry.Result.Route
                    || !DictionaryEqual(value.Result.RouteParameters, entry.Result.RouteParameters))
                .Prepend(entry).Take(Limit).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(values, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
