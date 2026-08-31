using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;
using Yemekhane.Api.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController(ReportService service, IPdfService pdfService, IExcelService excelService,
    ICsvService csvService) : ControllerBase
{
    [HttpGet("{type}")]
    [PermissionAuthorize(Permissions.ReportsRead)]
    public Task<ReportResult> Get(
        ReportType type,
        [FromQuery] DateTimeOffset? start = null,
        [FromQuery] DateTimeOffset? end = null,
        [FromQuery] string? studentNo = null,
        [FromQuery] string? cardNo = null,
        [FromQuery] string? firstName = null,
        [FromQuery] string? lastName = null,
        [FromQuery] string? @class = null,
        [FromQuery] string? department = null,
        [FromQuery] string? section = null,
        [FromQuery] string? job = null,
        [FromQuery] string? mealType = null,
        [FromQuery] string? device = null,
        [FromQuery] string? decision = null,
        [FromQuery] string? status = null,
        [FromQuery] string sortBy = "timestamp",
        [FromQuery] bool descending = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        service.QueryAsync(type, new ReportQuery(start, end, studentNo, cardNo, firstName, lastName, @class,
            department, section, job, mealType, device, decision, status, sortBy, descending, page, pageSize),
            cancellationToken);

    [HttpGet("{type}/pdf")]
    [PermissionAuthorize(Permissions.ReportsExport)]
    [EnableRateLimiting("expensive")]
    public async Task GetPdf(
        ReportType type,
        [FromQuery] DateTimeOffset? start = null,
        [FromQuery] DateTimeOffset? end = null,
        [FromQuery] string? studentNo = null,
        [FromQuery] string? cardNo = null,
        [FromQuery] string? firstName = null,
        [FromQuery] string? lastName = null,
        [FromQuery] string? @class = null,
        [FromQuery] string? department = null,
        [FromQuery] string? section = null,
        [FromQuery] string? job = null,
        [FromQuery] string? mealType = null,
        [FromQuery] string? device = null,
        [FromQuery] string? decision = null,
        [FromQuery] string? status = null,
        [FromQuery] string sortBy = "timestamp",
        [FromQuery] bool descending = true,
        CancellationToken cancellationToken = default)
    {
        Response.ContentType = "application/pdf";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{type.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.pdf\"";
        await pdfService.GenerateAsync(type,
            new ReportQuery(start, end, studentNo, cardNo, firstName, lastName, @class, department, section, job,
                mealType, device, decision, status, sortBy, descending),
            Response.Body, cancellationToken);
    }

    [HttpGet("{type}/excel")]
    [PermissionAuthorize(Permissions.ReportsExport)]
    [EnableRateLimiting("expensive")]
    public async Task GetExcel(
        ReportType type,
        [FromQuery] DateTimeOffset? start = null,
        [FromQuery] DateTimeOffset? end = null,
        [FromQuery] string? studentNo = null,
        [FromQuery] string? cardNo = null,
        [FromQuery] string? firstName = null,
        [FromQuery] string? lastName = null,
        [FromQuery] string? @class = null,
        [FromQuery] string? department = null,
        [FromQuery] string? section = null,
        [FromQuery] string? job = null,
        [FromQuery] string? mealType = null,
        [FromQuery] string? device = null,
        [FromQuery] string? decision = null,
        [FromQuery] string? status = null,
        [FromQuery] string sortBy = "timestamp",
        [FromQuery] bool descending = true,
        CancellationToken cancellationToken = default)
    {
        Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{type.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.xlsx\"";
        await excelService.GenerateAsync(type,
            new ReportQuery(start, end, studentNo, cardNo, firstName, lastName, @class, department, section, job,
                mealType, device, decision, status, sortBy, descending),
            Response.Body, cancellationToken);
    }

    [HttpGet("{type}/csv")]
    [PermissionAuthorize(Permissions.ReportsExport)]
    [EnableRateLimiting("expensive")]
    public async Task GetCsv(
        ReportType type,
        [FromQuery] DateTimeOffset? start = null,
        [FromQuery] DateTimeOffset? end = null,
        [FromQuery] string? studentNo = null,
        [FromQuery] string? cardNo = null,
        [FromQuery] string? firstName = null,
        [FromQuery] string? lastName = null,
        [FromQuery] string? @class = null,
        [FromQuery] string? department = null,
        [FromQuery] string? section = null,
        [FromQuery] string? job = null,
        [FromQuery] string? mealType = null,
        [FromQuery] string? device = null,
        [FromQuery] string? decision = null,
        [FromQuery] string? status = null,
        [FromQuery] string sortBy = "timestamp",
        [FromQuery] bool descending = true,
        CancellationToken cancellationToken = default)
    {
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{type.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.csv\"";
        await csvService.GenerateAsync(type,
            new ReportQuery(start, end, studentNo, cardNo, firstName, lastName, @class, department, section, job,
                mealType, device, decision, status, sortBy, descending),
            Response.Body, cancellationToken);
    }
}
