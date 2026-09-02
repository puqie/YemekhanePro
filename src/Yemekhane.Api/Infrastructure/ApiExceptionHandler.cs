using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
using Yemekhane.Infrastructure.Backup;

namespace Yemekhane.Api.Infrastructure;

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            RequestValidationException => StatusCodes.Status400BadRequest,
            BackupValidationException => StatusCodes.Status400BadRequest,
            BadHttpRequestException => StatusCodes.Status400BadRequest,
            ArgumentException => StatusCodes.Status400BadRequest,
            EntityNotFoundException => StatusCodes.Status404NotFound,
            EntityConflictException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };
        // 500'ler istemciye genel bir mesajla doner; asil istisna YALNIZCA burada
        // gorulur. Once hic yazilmiyordu: sahada "beklenmeyen hata" teshis edilemiyordu.
        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Islenmeyen istisna: {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status500InternalServerError
                    ? "İstek işlenirken beklenmeyen bir hata oluştu."
                    : exception.Message
            }
        });
    }
}
