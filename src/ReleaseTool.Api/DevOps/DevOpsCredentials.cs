using System.Text;
using Microsoft.Extensions.Options;
using ReleaseTool.Api.Configuration;

namespace ReleaseTool.Api.DevOps;

/// <summary>
/// An Azure DevOps personal access token, supplied per request by the caller or
/// read from configuration when the caller sends none.
/// Azure DevOps expects Basic auth with an empty username.
/// </summary>
public sealed record DevOpsCredentials(string PersonalAccessToken)
{
    public const string TokenHeader = "X-DevOps-Token";

    public string ToBasicParameter() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($":{PersonalAccessToken}"));

    public static bool TryRead(HttpRequest request, out DevOpsCredentials credentials)
    {
        var token = request.Headers[TokenHeader].ToString();

        if (string.IsNullOrWhiteSpace(token))
        {
            credentials = null!;
            return false;
        }

        credentials = new DevOpsCredentials(token.Trim());
        return true;
    }

    /// <summary>Keeps the token out of logs and error responses.</summary>
    public override string ToString() => "Azure DevOps PAT (redacted)";
}

/// <summary>
/// Resolves the PAT from the request, falling back to configuration, and
/// rejects the request when there is neither.
/// </summary>
public sealed class DevOpsCredentialsFilter(IOptions<StoredCredentialsOptions> stored) : IEndpointFilter
{
    private const string ItemKey = "DevOps.Credentials";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // A sent token wins, same rule as the Atlassian filter.
        var credentials = DevOpsCredentials.TryRead(context.HttpContext.Request, out var fromHeader)
            ? fromHeader
            : stored.Value.ForDevOps();

        if (credentials is null)
        {
            return Results.Problem(
                title: "Missing Azure DevOps token",
                detail: $"Send a '{DevOpsCredentials.TokenHeader}' header with a personal access token, "
                    + $"or configure '{StoredCredentialsOptions.SectionName}:"
                    + $"{nameof(StoredCredentialsOptions.DevOpsPersonalAccessToken)}' "
                    + "in user-secrets or an environment variable.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        context.HttpContext.Items[ItemKey] = credentials;
        return await next(context);
    }

    public static DevOpsCredentials Get(HttpContext context) =>
        context.Items[ItemKey] as DevOpsCredentials
        ?? throw new InvalidOperationException(
            $"No DevOps credentials on the request. Add {nameof(DevOpsCredentialsFilter)} to the endpoint.");
}
