using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ReleaseTool.Api;

/// <summary>Surfaces actionable failures with the status they carry.</summary>
public sealed class ReleaseToolExceptionHandler(ILogger<ReleaseToolExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not ReleaseToolException failure)
        {
            return false;
        }

        logger.LogWarning("{Message}", failure.Message);

        context.Response.StatusCode = failure.StatusCode;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = failure.Message,
            Status = failure.StatusCode
        }, ct);

        return true;
    }
}
