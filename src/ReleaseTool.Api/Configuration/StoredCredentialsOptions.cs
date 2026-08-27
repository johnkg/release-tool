using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.DevOps;

namespace ReleaseTool.Api.Configuration;

/// <summary>
/// Credentials read from configuration, so they do not have to be typed in
/// every session. Bound from <see cref="IConfiguration"/>, which is the whole
/// point: the same code reads user-secrets on a dev machine and an environment
/// variable or a vault on a server, and the storage choice never reaches here.
///
/// These are secrets. They belong in <c>dotnet user-secrets</c> or an
/// environment variable - never in appsettings.json, which is source-controlled.
/// Nothing in this class is ever sent to the browser: the config endpoint
/// reports only whether a credential exists, never its value.
/// </summary>
public sealed class StoredCredentialsOptions
{
    public const string SectionName = "Credentials";

    public string AtlassianEmail { get; set; } = string.Empty;

    public string AtlassianApiToken { get; set; } = string.Empty;

    public string DevOpsPersonalAccessToken { get; set; } = string.Empty;

    /// <summary>Both halves are needed - Basic auth here is email:token.</summary>
    public bool HasAtlassian =>
        !string.IsNullOrWhiteSpace(AtlassianEmail) && !string.IsNullOrWhiteSpace(AtlassianApiToken);

    public bool HasDevOps => !string.IsNullOrWhiteSpace(DevOpsPersonalAccessToken);

    public AtlassianCredentials? ForAtlassian() =>
        HasAtlassian ? new AtlassianCredentials(AtlassianEmail.Trim(), AtlassianApiToken.Trim()) : null;

    public DevOpsCredentials? ForDevOps() =>
        HasDevOps ? new DevOpsCredentials(DevOpsPersonalAccessToken.Trim()) : null;

    /// <summary>Belt and braces: a stray log of this object cannot leak a token.</summary>
    public override string ToString() =>
        $"Atlassian: {(HasAtlassian ? AtlassianEmail : "not configured")}, "
        + $"Azure DevOps: {(HasDevOps ? "configured" : "not configured")} (tokens redacted)";
}
