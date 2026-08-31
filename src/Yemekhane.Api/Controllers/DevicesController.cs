using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Api.Devices;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/devices")]
public sealed class DevicesController(DeviceAdministrationService service) : ControllerBase
{
    [HttpGet("capabilities")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public object Capabilities() => new { SimulatorAllowed = service.IsSimulatorAllowed };

    [HttpGet]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<IReadOnlyList<DeviceDto>> List(CancellationToken cancellationToken) => service.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<DeviceDto> Get(Guid id, CancellationToken cancellationToken) => service.GetAsync(id, cancellationToken);

    [HttpPost]
    [PermissionAuthorize(Permissions.DevicesManage)]
    public Task<DeviceDto> Create(DeviceWriteRequest request, CancellationToken cancellationToken) => service.CreateAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    [PermissionAuthorize(Permissions.DevicesManage)]
    public Task<DeviceDto> Update(Guid id, DeviceWriteRequest request, CancellationToken cancellationToken) => service.UpdateAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    [PermissionAuthorize(Permissions.DevicesManage)]
    public Task<DeviceDto> Deactivate(Guid id, CancellationToken cancellationToken) => service.DeactivateAsync(id, cancellationToken);

    [HttpPost("{id:guid}/{operation:regex(^(connect|disconnect|test|reconnect|status)$)}")]
    [PermissionAuthorize(Permissions.DevicesManage)]
    public Task<DeviceActionResult> Action(Guid id, string operation, CancellationToken cancellationToken) =>
        service.ExecuteAsync(id, operation.ToLowerInvariant(), cancellationToken);

    [HttpGet("{id:guid}/logs")]
    [PermissionAuthorize(Permissions.DevicesRead)]
    public Task<IReadOnlyList<DeviceLogDto>> Logs(Guid id, int take = 100, CancellationToken cancellationToken = default) =>
        service.LogsAsync(id, take, cancellationToken);
}
