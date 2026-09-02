using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Yemekhane.Application.Reports;
using Yemekhane.Reports;
using Yemekhane.Api.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Yemekhane.Api.Controllers;

/// <summary>
/// Eylemin imzasinda olmayan sorgu parametrelerini 400 ile geri cevirir.
///
/// ASP.NET bilinmeyen parametreleri sessizce yok sayar: <c>?startDate=…</c> yazan bir
/// istemci hicbir filtre uygulanmadan TUM kayitlari alir ve bunu "bugunun raporu" sanir.
/// Rapor ucunda "filtre uygulanmadi" ile "filtre uygulandi, kayit yok" ayirt edilemezse
/// rapor guvenilmez; bu yuzden yanlis ad hata olarak doner ve dogru adlar mesajda listelenir.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RejectUnknownQueryParametersAttribute : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var known = context.ActionDescriptor.Parameters
            .Where(x => x.BindingInfo?.BindingSource == null
                        || x.BindingInfo.BindingSource == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Query)
            .Select(x => x.BindingInfo?.BinderModelName ?? x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = context.HttpContext.Request.Query.Keys
            .Where(key => !known.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length == 0) return;

        var expected = string.Join(", ", known.Where(x => x != "cancellationToken").OrderBy(x => x, StringComparer.Ordinal));
        context.Result = new BadRequestObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = $"Bilinmeyen sorgu parametresi: {string.Join(", ", unknown)}",
            Detail = $"Geçerli parametreler: {expected}.",
            Instance = context.HttpContext.Request.Path
        });
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

[ApiController]
[Route("api/reports")]
[RejectUnknownQueryParameters]
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
            department, section, job, mealType, device, decision, status, sortBy, descending, page, pageSize,
            CanReadSensitive()),
            cancellationToken);

    /// <summary>
    /// TC kimlik no (Sicil Listesi) yalnizca bu yetkiyle doner. Karar sorgu parametresinden degil
    /// JWT talebinden alinir; StudentsController ile ayni kural.
    /// </summary>
    private bool CanReadSensitive() => User.HasClaim(Permissions.ClaimType, Permissions.StudentsSensitiveRead);

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
                mealType, device, decision, status, sortBy, descending, IncludeSensitive: CanReadSensitive()),
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
                mealType, device, decision, status, sortBy, descending, IncludeSensitive: CanReadSensitive()),
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
                mealType, device, decision, status, sortBy, descending, IncludeSensitive: CanReadSensitive()),
            Response.Body, cancellationToken);
    }
}
