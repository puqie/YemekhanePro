using Serilog.Context;

namespace Yemekhane.Api.Infrastructure;

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out var supplied) &&
                            Guid.TryParse(supplied, out var parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");
        context.TraceIdentifier = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("OperationId", context.Request.Headers["X-Operation-ID"].ToString()))
        using (LogContext.PushProperty("DeviceId", context.Request.Headers["X-Device-ID"].ToString()))
            await next(context);
    }
}
