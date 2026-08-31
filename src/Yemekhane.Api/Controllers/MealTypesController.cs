using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Meals;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.EntitlementsManage)]
[Route("api/meal-types")]
public sealed class MealTypesController(MealTypeService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<MealTypeDetails>> List(bool includeInactive, CancellationToken cancellationToken) => service.ListAsync(includeInactive, cancellationToken);
    [HttpPost]
    public Task<MealTypeDetails> Create(SaveMealTypeRequest request, CancellationToken cancellationToken) => service.CreateAsync(request, cancellationToken);
    [HttpPut("{id:guid}")]
    public Task<MealTypeDetails> Update(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken) => service.UpdateAsync(id, request, cancellationToken);
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken) { await service.DeactivateAsync(id, cancellationToken); return NoContent(); }
}
