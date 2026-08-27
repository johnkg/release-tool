using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Configuration;
using ReleaseTool.Api.Contracts;

namespace ReleaseTool.Api.Jira;

/// <summary>
/// Moves release tickets between the two workflow statuses, and carries the
/// resolution that goes with them.
///
/// Jira has no "set the status" call: a status is only reachable through a
/// transition, and which transitions exist depends on where the ticket stands
/// and on the caller's permissions. So each ticket is read, matched and moved
/// on its own, and a ticket that cannot get there is reported rather than
/// forced.
/// </summary>
public sealed class StatusTransitioner(
    JiraService jira,
    IOptions<AtlassianOptions> options,
    ILogger<StatusTransitioner> logger)
{
    /// <summary>The configured names, for the UI to label its buttons with.</summary>
    public ReleaseWorkflowNames Names => new(
        options.Value.DeployedToProductionStatus,
        options.Value.ReadyForDeploymentStatus,
        options.Value.ResolutionName);

    private string ResolutionName => options.Value.ResolutionName;

    public string NameOf(ReleaseStatus status) => status switch
    {
        ReleaseStatus.DeployedToProduction => options.Value.DeployedToProductionStatus,
        _ => options.Value.ReadyForDeploymentStatus
    };

    /// <summary>Current status and resolution per ticket, for the preview.</summary>
    public async Task<IReadOnlyList<TicketStatus>> ReadAsync(
        IReadOnlyList<string> ticketKeys,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var states = await jira.GetStatesAsync(ticketKeys, credentials, ct);

        return [.. ticketKeys.Select(key => states.TryGetValue(key, out var state)
            ? new TicketStatus(key, state.Status, state.Resolution)
            : new TicketStatus(key, null, null))];
    }

    /// <summary>
    /// Moves every ticket it can. One ticket failing never stops the others -
    /// unlike a cherry-pick run, the tickets are independent of each other.
    /// </summary>
    public async Task<TransitionResponse> ApplyAsync(
        IReadOnlyList<string> ticketKeys,
        ReleaseStatus target,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var targetName = NameOf(target);

        // One call for the whole batch, so a ticket already in the target status
        // costs nothing at all.
        var states = await jira.GetStatesAsync(ticketKeys, credentials, ct);
        var results = new List<TransitionResult>();

        foreach (var key in ticketKeys)
        {
            var state = states.TryGetValue(key, out var found) ? found : null;

            if (Same(state?.Status, targetName))
            {
                results.Add(await AlreadyThereAsync(key, state!, target, targetName, credentials, ct));
                continue;
            }

            results.Add(await MoveAsync(key, state?.Status, target, targetName, credentials, ct));
        }

        return new TransitionResponse(results);
    }

    /// <summary>
    /// A ticket already in the target status. Going live it may still be
    /// unresolved - it got there by some other route, or by an earlier run that
    /// only moved the status - so the resolution is filled in even though the
    /// status needs nothing.
    /// </summary>
    private async Task<TransitionResult> AlreadyThereAsync(
        string key,
        JiraIssueState state,
        ReleaseStatus target,
        string targetName,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        if (target != ReleaseStatus.DeployedToProduction || Same(state.Resolution, ResolutionName))
        {
            return new TransitionResult(key, state.Status, Success: true, Unchanged: true,
                $"Already {targetName}.");
        }

        try
        {
            var resolution = await FindResolutionAsync(credentials, ct);

            await jira.SetResolutionAsync(key, resolution.Id, credentials, ct);
            logger.LogInformation("{Key} was already {Status}; resolution set to '{Resolution}'",
                key, targetName, resolution.Name);

            return new TransitionResult(key, state.Status, Success: true, Unchanged: false,
                $"Already {targetName}; resolution set to {resolution.Name}.");
        }
        catch (Exception failure) when (failure is AtlassianApiException or ReleaseToolException)
        {
            return new TransitionResult(key, state.Status, Success: false, Unchanged: false,
                $"Already {targetName}, but the resolution could not be set: {Explain(failure)}");
        }
    }

    private async Task<TransitionResult> MoveAsync(
        string key,
        string? from,
        ReleaseStatus target,
        string targetName,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var live = target == ReleaseStatus.DeployedToProduction;

        try
        {
            var transitions = await jira.GetTransitionsAsync(key, credentials, ct);

            // The destination is what matters; a transition's own name is often a
            // verb rather than the status it leads to, so that is only a fallback.
            var move = Match(transitions, t => t.ToStatus, targetName)
                       ?? Match(transitions, t => t.Name, targetName);

            if (move is null)
            {
                var offered = transitions.Count == 0
                    ? "Jira offers no transitions at all - check your permissions on this ticket."
                    : $"Jira offers: {string.Join(", ", transitions.Select(Describe))}.";

                logger.LogWarning(
                    "No transition to '{Target}' for {Key} from '{From}'. {Offered}",
                    targetName, key, from ?? "unknown", offered);

                return new TransitionResult(key, from, Success: false, Unchanged: false,
                    $"No transition from {from ?? "its current status"} leads to {targetName}. {offered}");
            }

            // The Resolution dropdown belongs to the transition screen, so it is
            // filled in as part of the same move where the screen has one.
            JsonObject? fields = null;
            JiraResolution? afterwards = null;

            if (live && move.Resolution is { } dropdown)
            {
                var choice = dropdown.AllowedValues.FirstOrDefault(v => Same(v.Name, ResolutionName));

                if (choice is null)
                {
                    // Refuse rather than move: a required field would fail the
                    // call anyway, and moving without it leaves the job half done.
                    return new TransitionResult(key, from, Success: false, Unchanged: false,
                        $"The transition to {targetName} has no resolution called {ResolutionName}. "
                        + $"It offers: {Offered(dropdown)}.");
                }

                fields = new JsonObject
                {
                    ["resolution"] = new JsonObject { ["id"] = choice.Id }
                };
            }
            else if (live)
            {
                // No dropdown on this screen, so the resolution has to be a
                // separate edit. Looked up first: discovering it does not exist
                // after the ticket has moved would leave a half-done job.
                afterwards = await FindResolutionAsync(credentials, ct);
            }

            await jira.TransitionAsync(key, move.Id, credentials, ct, fields);

            logger.LogInformation(
                "{Key} moved from '{From}' to '{Target}' via transition {TransitionId} ('{TransitionName}')",
                key, from ?? "unknown", targetName, move.Id, move.Name);

            var landed = move.ToStatus ?? targetName;

            if (afterwards is null)
            {
                return new TransitionResult(key, from, Success: true, Unchanged: false,
                    fields is null
                        ? $"Moved to {landed}."
                        : $"Moved to {landed}, resolution {ResolutionName}.");
            }

            try
            {
                await jira.SetResolutionAsync(key, afterwards.Id, credentials, ct);

                return new TransitionResult(key, from, Success: true, Unchanged: false,
                    $"Moved to {landed}, resolution {afterwards.Name}.");
            }
            catch (AtlassianApiException failure)
            {
                // The status did move, so say so - but this row still needs a
                // person, hence not a success.
                logger.LogWarning("{Key} moved to '{Target}' but the resolution was refused: {Detail}",
                    key, targetName, failure.Detail);

                return new TransitionResult(key, from, Success: false, Unchanged: false,
                    $"Moved to {landed}, but the resolution could not be set: {Explain(failure)}");
            }
        }
        catch (Exception failure) when (failure is AtlassianApiException or ReleaseToolException)
        {
            logger.LogWarning("Transitioning {Key} to {Target} failed: {Reason}",
                key, target, Explain(failure));

            return new TransitionResult(key, from, Success: false, Unchanged: false, Explain(failure));
        }
    }

    /// <summary>
    /// Sets or clears the resolution without touching the status. Clearing is
    /// the way back: there is no resolution named "Unresolved", that state is
    /// the field holding nothing.
    /// </summary>
    public async Task<ResolutionResponse> ApplyResolutionAsync(
        IReadOnlyList<string> ticketKeys,
        bool resolved,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var states = await jira.GetStatesAsync(ticketKeys, credentials, ct);
        var results = new List<ResolutionOutcome>();

        foreach (var key in ticketKeys)
        {
            var current = states.TryGetValue(key, out var state) ? state.Resolution : null;

            if (resolved ? Same(current, ResolutionName) : current is null)
            {
                results.Add(new ResolutionOutcome(key, current, Success: true, Unchanged: true,
                    resolved ? $"Already {ResolutionName}." : "Already unresolved."));

                continue;
            }

            try
            {
                var wanted = resolved ? await FindResolutionAsync(credentials, ct) : null;

                await jira.SetResolutionAsync(key, wanted?.Id, credentials, ct);

                logger.LogInformation("{Key} resolution set to '{Resolution}'",
                    key, wanted?.Name ?? "Unresolved");

                results.Add(new ResolutionOutcome(key, wanted?.Name, Success: true, Unchanged: false,
                    wanted is null ? "Cleared to Unresolved." : $"Set to {wanted.Name}."));
            }
            catch (Exception failure) when (failure is AtlassianApiException or ReleaseToolException)
            {
                logger.LogWarning("Setting the resolution on {Key} failed: {Reason}", key, Explain(failure));

                results.Add(new ResolutionOutcome(key, current, Success: false, Unchanged: false,
                    Explain(failure)));
            }
        }

        return new ResolutionResponse(results);
    }

    /// <summary>
    /// The configured resolution as the site defines it. Cached in JiraService,
    /// so asking per ticket costs one call per run at most.
    /// </summary>
    private async Task<JiraResolution> FindResolutionAsync(AtlassianCredentials credentials, CancellationToken ct)
    {
        var resolutions = await jira.GetResolutionsAsync(credentials, ct);
        var match = resolutions.FirstOrDefault(r => Same(r.Name, ResolutionName));

        return match ?? throw ReleaseToolException.NotFound(
            $"Jira has no resolution called '{ResolutionName}'. It has: "
            + (resolutions.Count == 0 ? "none" : string.Join(", ", resolutions.Select(r => r.Name)))
            + ". Set Atlassian:ResolutionName to one of those.");
    }

    private static string Explain(Exception failure) => failure switch
    {
        AtlassianApiException api =>
            $"Jira refused with {(int)api.StatusCode}" + (api.Detail is null ? "." : $": {api.Detail}"),
        _ => failure.Message
    };

    private static string Offered(ResolutionField dropdown) =>
        dropdown.AllowedValues.Count == 0
            ? "nothing"
            : string.Join(", ", dropdown.AllowedValues.Select(v => v.Name));

    private static JiraTransition? Match(
        IReadOnlyList<JiraTransition> transitions,
        Func<JiraTransition, string?> field,
        string target)
        => transitions.FirstOrDefault(t => Same(field(t), target));

    /// <summary>
    /// Status comparison. Spacing around a slash is not meaningful - a workflow
    /// spelling it "UAT Done / Ready for Deployment" is the same status as the
    /// configured "YOUR_READY_STATUS" - so whitespace is ignored
    /// along with case. Two genuinely different statuses cannot collide on this.
    /// </summary>
    private static bool Same(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(Squash(left), Squash(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Squash(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    private static string Describe(JiraTransition transition) =>
        transition.ToStatus is null || Same(transition.ToStatus, transition.Name)
            ? transition.Name
            : $"{transition.Name} -> {transition.ToStatus}";
}
