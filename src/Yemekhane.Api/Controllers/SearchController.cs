using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Search;
using Microsoft.AspNetCore.RateLimiting;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(IGlobalSearchRepository repository) : ControllerBase
{
    [HttpGet]
    [EnableRateLimiting("search")]
    public Task<GlobalSearchResponse> Search([FromQuery] string q, CancellationToken cancellationToken)
    {
        var permissions = User.FindAll(Permissions.ClaimType).Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        return repository.SearchAsync(q ?? string.Empty, permissions, cancellationToken);
    }
}
