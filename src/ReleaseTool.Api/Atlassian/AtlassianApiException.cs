using System.Net;

namespace ReleaseTool.Api.Atlassian;

/// <summary>A non-success response from Jira or Confluence.</summary>
public sealed class AtlassianApiException(
    HttpStatusCode statusCode,
    string requestPath,
    string? detail)
    : Exception($"Atlassian returned {(int)statusCode} for '{requestPath}'.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string RequestPath { get; } = requestPath;

    /// <summary>Truncated response body. Never contains the caller's token.</summary>
    public string? Detail { get; } = detail;
}
