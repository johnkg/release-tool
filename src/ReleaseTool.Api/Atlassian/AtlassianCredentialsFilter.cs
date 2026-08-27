using Microsoft.Extensions.Options;
using ReleaseTool.Api.Configuration;

namespace ReleaseTool.Api.Atlassian;

/// <summary>
/// Resolves the caller's Atlassian credentials once, so no endpoint has to, and
/// rejects the request when there are none to be had.
/// </summary>
public sealed class AtlassianCredentialsFilter(IOptions<StoredCredentialsOptions> stored) : IEndpointFilter
{
    private const string ItemKey = "Atlassian.Credentials";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Headers win over configuration. Whoever sends their own token acts as
        // themselves, so a second user of a shared instance still shows up under
        // their own name in the Confluence page history.
        var credentials = AtlassianCredentials.TryRead(context.HttpContext.Request, out var fromHeaders)
            ? fromHeaders
            : stored.Value.ForAtlassian();

        if (credentials is null)
        {
            return Results.Problem(
                title: "Missing Atlassian credentials",
                detail: $"Send '{AtlassianCredentials.EmailHeader}' and '{AtlassianCredentials.TokenHeader}' headers, "
                    + $"or configure '{StoredCredentialsOptions.SectionName}:{nameof(StoredCredentialsOptions.AtlassianEmail)}' "
                    + $"and '{StoredCredentialsOptions.SectionName}:{nameof(StoredCredentialsOptions.AtlassianApiToken)}' "
                    + "in user-secrets or an environment variable.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        context.HttpContext.Items[ItemKey] = credentials;
        return await next(context);
    }

    /// <summary>Only valid inside an endpoint guarded by this filter.</summary>
    public static AtlassianCredentials Get(HttpContext context) =>
        context.Items[ItemKey] as AtlassianCredentials
        ?? throw new InvalidOperationException(
            $"No Atlassian credentials on the request. Add {nameof(AtlassianCredentialsFilter)} to the endpoint.");
}
