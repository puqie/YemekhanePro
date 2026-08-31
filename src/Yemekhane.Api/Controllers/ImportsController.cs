using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
using Yemekhane.Application.StudentImports;
using Yemekhane.Api.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Yemekhane.Api.Controllers;

[ApiController]
[PermissionAuthorize(Permissions.StudentsWrite)]
[Route("api/imports/students")]
public sealed class ImportsController(IStudentImportService service) : ControllerBase
{
    [HttpPost("preview")]
    [EnableRateLimiting("expensive")]
    [RequestSizeLimit(10_500_000)]
    public async Task<ImportPreviewResult> Preview(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 10_000_000)
            throw new RequestValidationException("Dosya boyutu 1-10.000.000 bayt arasında olmalıdır.");
        await using var stream = file.OpenReadStream();
        return await service.PreviewAsync(stream, file.FileName, ActorId(), cancellationToken);
    }

    [HttpPost("apply")]
    public Task<ImportApplyResult> Apply(ApplyStudentImportRequest request, CancellationToken cancellationToken) =>
        service.ApplyAsync(request, ActorId(), cancellationToken);

    [HttpGet("{token}/errors.csv")]
    public IActionResult ErrorReport(string token)
    {
        var report = service.GetErrorReport(token, ActorId());
        return File(report.Content, "text/csv; charset=utf-8", report.FileName);
    }

    private Guid ActorId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : throw new RequestValidationException("Kimliği doğrulanmış kullanıcı bilgisi geçersiz.");
}
