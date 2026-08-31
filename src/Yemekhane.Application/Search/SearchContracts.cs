namespace Yemekhane.Application.Search;

public sealed record SearchResultItem(
    string Type,
    string Title,
    string Subtitle,
    string Route,
    IReadOnlyDictionary<string, string> RouteParameters,
    string Icon);

public sealed record SearchResultGroup(string Type, string Title, IReadOnlyList<SearchResultItem> Items);

public sealed record GlobalSearchResponse(string Query, IReadOnlyList<SearchResultGroup> Groups);

public interface IGlobalSearchRepository
{
    Task<GlobalSearchResponse> SearchAsync(string query, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken);
}
