using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Application.Common;
using Yemekhane.Infrastructure.Backup;

namespace Yemekhane.Api.Infrastructure;

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// Onceden derlenmis kayit temsilcisi (CA1848): her 500'de bicimlendirme ve kutulama
    /// maliyeti odenmesin. Mesaj ve alan adlari LogError cagrisiyla ayni.
    /// </summary>
    private static readonly Action<ILogger, string, string, Exception?> LogUnhandled =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1, "UnhandledException"),
            "Islenmeyen istisna: {Method} {Path}");

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
            LogUnhandled(logger, httpContext.Request.Method, httpContext.Request.Path, exception);
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
