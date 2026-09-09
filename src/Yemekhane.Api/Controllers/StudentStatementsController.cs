using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Statements;

namespace Yemekhane.Api.Controllers;

/// <summary>
/// Ogrenci ekstresi: secilen tarih araligindaki odemeler, bakiye hareketleri, taksit/borc
/// durumu ve yemek kullanimi tek belgede. Veli 1-2 yil oncesini sorabildigi icin aralik
/// serbesttir; yil sonu sifirlamasi mali kayitlari silmez.
/// </summary>
[ApiController]
[Route("api/students/{studentId:guid}/statement")]
public sealed class StudentStatementsController(StudentStatementService service, IStudentStatementPdfService pdf) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.CashRead)]
    public Task<StudentStatement> Get(Guid studentId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var (defaultFrom, defaultTo) = service.DefaultRange();
        return service.BuildAsync(new StudentStatementQuery(studentId, from ?? defaultFrom, to ?? defaultTo), cancellationToken);
    }

    [HttpGet("pdf")]
    [PermissionAuthorize(Permissions.ReportsExport)]
    [EnableRateLimiting("expensive")]
    public async Task<IActionResult> Pdf(Guid studentId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var (defaultFrom, defaultTo) = service.DefaultRange();
        var statement = await service.BuildAsync(new StudentStatementQuery(studentId, from ?? defaultFrom, to ?? defaultTo),
            cancellationToken);
        Response.ContentType = "application/pdf";
        Response.Headers.ContentDisposition = $"attachment; filename=ekstre-{statement.StudentNo}-{statement.From:yyyyMMdd}.pdf";
        await pdf.GenerateAsync(statement, Response.Body, cancellationToken);
        return new EmptyResult();
    }
}
