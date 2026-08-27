namespace ReleaseTool.Api;

/// <summary>
/// A failure the user can act on - a missing page, a table that is not there -
/// as opposed to an unexpected fault.
/// </summary>
public sealed class ReleaseToolException(string message, int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static ReleaseToolException NotFound(string message) =>
        new(message, StatusCodes.Status404NotFound);

    public static ReleaseToolException Conflict(string message) =>
        new(message, StatusCodes.Status409Conflict);
}
