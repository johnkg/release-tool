using System.ComponentModel.DataAnnotations;

namespace ReleaseTool.Api.Configuration;

/// <summary>
/// Non-secret Atlassian settings. The API token is deliberately absent - each
/// user supplies their own per request, see <see cref="Atlassian.AtlassianCredentials"/>.
/// </summary>
public sealed class AtlassianOptions
{
    public const string SectionName = "Atlassian";

    /// <summary>
    /// Site base URL. On the API-token route this is the site itself, not the
    /// api.atlassian.com/ex gateway, so no cloud ID is involved.
    /// </summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://your-domain.atlassian.net/";

    /// <summary>
    /// Pre-fills the space field in the UI. Point this at a sandbox space while
    /// the tool is being validated, so a careless run cannot reach a live
    /// release page.
    /// </summary>
    [Required]
    public string DefaultSpaceKey { get; set; } = string.Empty;

    /// <summary>
    /// The Jira project key whose tickets this tool acts on, without the dash.
    /// Rows on the Approvals table for any other project are left alone, which
    /// is how a release page that mixes projects is handled.
    /// </summary>
    [Required]
    [RegularExpression("^[A-Za-z][A-Za-z0-9]*$",
        ErrorMessage = "TicketKeyPrefix must be a Jira project key such as 'PROJECT', with no dash.")]
    public string TicketKeyPrefix { get; set; } = "PROJECT";

    /// <summary>
    /// Developer assigned when a ticket has neither a PR comment nor a
    /// "fixed on" reference. Resolved to an account ID at runtime, never hardcoded.
    /// </summary>
    [Required]
    public string FallbackDeveloperName { get; set; } = string.Empty;

    /// <summary>
    /// The workflow status a ticket reaches once the release is live. Jira has
    /// no "set the status" call - a status is only reachable through a
    /// transition - so this is matched against the transitions the workflow
    /// offers and must read as the workflow spells it.
    /// </summary>
    [Required]
    public string DeployedToProductionStatus { get; set; } = "YOUR_DEPLOYED_STATUS";

    /// <summary>
    /// Where a ticket goes back to when a release is re-cut or rolled back.
    /// The reverse of <see cref="DeployedToProductionStatus"/>.
    /// </summary>
    [Required]
    public string ReadyForDeploymentStatus { get; set; } = "YOUR_READY_STATUS";

    /// <summary>
    /// The resolution a ticket carries once the release is live. Jira offers it
    /// as a dropdown on the transition screen, defaulted to unresolved, so the
    /// tool fills it in as part of the same move. There is no counterpart for
    /// going back: <em>Unresolved</em> is the field being cleared, not a value.
    /// </summary>
    [Required]
    public string ResolutionName { get; set; } = "Done";
}
