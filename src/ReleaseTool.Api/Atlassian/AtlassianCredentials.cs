using System.Text;

namespace ReleaseTool.Api.Atlassian;

/// <summary>
/// One user's Atlassian API-token credentials, supplied per request by the
/// caller. Never persisted server-side and never written to configuration:
/// the token is a personal credential, and a shared one in appsettings would
/// let anyone with the URL write to release pages as its owner.
/// </summary>
public sealed record AtlassianCredentials(string Email, string ApiToken)
{
    public const string EmailHeader = "X-Atlassian-Email";
    public const string TokenHeader = "X-Atlassian-Token";

    /// <summary>base64(email:apiToken), the parameter for a Basic auth header.</summary>
    public string ToBasicParameter() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{ApiToken}"));

    public static bool TryRead(HttpRequest request, out AtlassianCredentials credentials)
    {
        var email = request.Headers[EmailHeader].ToString();
        var token = request.Headers[TokenHeader].ToString();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            credentials = null!;
            return false;
        }

        credentials = new AtlassianCredentials(email.Trim(), token.Trim());
        return true;
    }

    /// <summary>Keeps the token out of logs and error responses.</summary>
    public override string ToString() => $"{Email} (token redacted)";
}
