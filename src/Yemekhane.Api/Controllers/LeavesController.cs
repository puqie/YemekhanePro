using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Leaves;
using Yemekhane.Api.Authorization;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/leaves")]
public sealed class LeavesController(LeaveService service) : ControllerBase
{
    [HttpPost]
    [PermissionAuthorize(Permissions.StudentsWrite)]
    public Task<LeaveDetails> Create(CreateLeaveRequest request, CancellationToken cancellationToken) => service.CreateAsync(request, cancellationToken);
    [HttpGet("student/{studentId:guid}")]
    [PermissionAuthorize(Permissions.StudentsRead)]
    public Task<IReadOnlyList<LeaveDetails>> List(Guid studentId, CancellationToken cancellationToken) => service.ListAsync(studentId, cancellationToken);
}
