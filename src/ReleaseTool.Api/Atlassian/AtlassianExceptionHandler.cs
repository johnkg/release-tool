using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ReleaseTool.Api.Atlassian;

/// <summary>
/// Turns an Atlassian failure into a problem response the UI can act on,
/// rather than a bare 500. A 401 here means the user's token was rejected -
/// distinct from the 401 the credentials filter raises for a missing header.
/// </summary>
public sealed class AtlassianExceptionHandler(ILogger<AtlassianExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not AtlassianApiException failure)
        {
            return false;
        }

        var (status, title) = failure.StatusCode switch
        {
            HttpStatusCode.Unauthorized => (StatusCodes.Status401Unauthorized,
                "Atlassian rejected the token. Check the email and API token."),
            HttpStatusCode.Forbidden => (StatusCodes.Status403Forbidden,
                "That account lacks permission for this page or project."),
            // Confluence answers an unauthenticated request with 404 rather than
            // 401, so a bad token is indistinguishable from a missing page here.
            HttpStatusCode.NotFound => (StatusCodes.Status404NotFound,
                "Not found in Atlassian. Check the page is published rather than a draft, and verify the token - Confluence reports 404 for an unauthenticated request."),
            HttpStatusCode.Conflict => (StatusCodes.Status409Conflict,
                "The page changed since it was loaded. Reload and try again."),
            HttpStatusCode.TooManyRequests => (StatusCodes.Status429TooManyRequests,
                "Atlassian is rate limiting. Wait a moment and retry."),
            _ => (StatusCodes.Status502BadGateway, "Atlassian request failed.")
        };

        logger.LogError(failure, "Atlassian call to {Path} failed with {StatusCode}",
            failure.RequestPath, (int)failure.StatusCode);

        var problem = new ProblemDetails
        {
            Title = title,
            Detail = failure.Detail,
            Status = status,
            Instance = failure.RequestPath
        };

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
