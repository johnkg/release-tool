using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using ReleaseTool.Api.Adf;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Contracts;

namespace ReleaseTool.Api.Jira;

/// <summary>A ticket comment, already flattened for matching.</summary>
public sealed record JiraComment(string AuthorName, string AuthorAccountId, string Text, DateTimeOffset Created);

/// <summary>An Atlassian account, as named on an issue.</summary>
public sealed record JiraPerson(string DisplayName, string AccountId);

/// <summary>Everything one ticket contributes to the decision.</summary>
public sealed record JiraIssue(string Key, IReadOnlyList<JiraComment> Comments, JiraPerson? Reporter);

/// <summary>Where a ticket stands: its status, and its resolution if it has one.</summary>
public sealed record JiraIssueState(string Status, string? Resolution);

/// <summary>One of the choices in a transition screen's Resolution dropdown.</summary>
public sealed record JiraResolution(string Id, string Name);

/// <summary>
/// The Resolution field as one transition presents it. Absent entirely when the
/// transition has no such field - and sending a field a transition does not
/// have is a hard 400, so its absence has to be known rather than assumed.
/// </summary>
public sealed record ResolutionField(bool Required, IReadOnlyList<JiraResolution> AllowedValues);

/// <summary>
/// One move Jira is offering on an issue right now. <paramref name="ToStatus"/>
/// is the status it lands in, which is what a target status is matched against -
/// the transition's own name is often a verb ("Deploy"), not the destination.
/// </summary>
public sealed record JiraTransition(
    string Id,
    string Name,
    string? ToStatus,
    ResolutionField? Resolution = null);

public sealed class JiraService(AtlassianClient client, IMemoryCache cache, ILogger<JiraService> logger)
{
    private const int PageSize = 100;

    /// <summary>
    /// Comments and reporter for every ticket in one call, not one call per ticket.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, JiraIssue>> GetIssuesAsync(
        IReadOnlyList<string> ticketKeys,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var result = new Dictionary<string, JiraIssue>(StringComparer.OrdinalIgnoreCase);

        if (ticketKeys.Count == 0)
        {
            return result;
        }

        var jql = $"key in ({string.Join(", ", ticketKeys)})";
        string? pageToken = null;

        do
        {
            var request = new JsonObject
            {
                ["jql"] = jql,
                ["fields"] = new JsonArray("comment", "reporter"),
                ["maxResults"] = PageSize
            };

            if (pageToken is not null)
            {
                request["nextPageToken"] = pageToken;
            }

            var response = await client.PostAsync("rest/api/3/search/jql", credentials, request, ct);

            foreach (var issue in response?["issues"]?.AsArray() ?? [])
            {
                var key = issue?["key"]?.GetValue<string>();

                if (key is null)
                {
                    continue;
                }

                var fields = issue!["fields"];
                var comment = fields?["comment"];
                var comments = ReadComments(comment?["comments"] as JsonArray);

                // Search truncates long comment threads; refetch those in full so
                // a PR link buried deep in the thread is not missed.
                var total = comment?["total"]?.GetValue<int>() ?? comments.Count;

                if (total > comments.Count)
                {
                    logger.LogInformation(
                        "{Key} has {Total} comments but search returned {Returned}; refetching",
                        key, total, comments.Count);

                    comments = await FetchAllCommentsAsync(key, credentials, ct);
                }

                result[key] = new JiraIssue(key, comments, ReadPerson(fields?["reporter"]));
            }

            pageToken = response?["nextPageToken"]?.GetValue<string>();
        }
        while (pageToken is not null);

        return result;
    }

    private async Task<List<JiraComment>> FetchAllCommentsAsync(string key, AtlassianCredentials credentials, CancellationToken ct)
    {
        var all = new List<JiraComment>();
        var startAt = 0;

        while (true)
        {
            var response = await client.GetAsync(
                $"rest/api/3/issue/{key}/comment?startAt={startAt}&maxResults={PageSize}", credentials, ct);

            var batch = ReadComments(response?["comments"] as JsonArray);
            all.AddRange(batch);

            var total = response?["total"]?.GetValue<int>() ?? all.Count;
            startAt += PageSize;

            if (batch.Count == 0 || all.Count >= total)
            {
                return all;
            }
        }
    }

    /// <summary>
    /// A reporter can be absent - deleted accounts and some anonymised issues
    /// come back null - so this is nullable rather than defaulted.
    /// </summary>
    private static JiraPerson? ReadPerson(JsonNode? person)
    {
        var accountId = person?["accountId"]?.GetValue<string>();
        var displayName = person?["displayName"]?.GetValue<string>();

        return string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(displayName)
            ? null
            : new JiraPerson(displayName, accountId);
    }

    private static List<JiraComment> ReadComments(JsonArray? comments)
    {
        var parsed = new List<JiraComment>();

        foreach (var comment in comments ?? [])
        {
            if (comment is null)
            {
                continue;
            }

            parsed.Add(new JiraComment(
                AuthorName: comment["author"]?["displayName"]?.GetValue<string>() ?? string.Empty,
                AuthorAccountId: comment["author"]?["accountId"]?.GetValue<string>() ?? string.Empty,
                Text: AdfText.Flatten(comment["body"]),
                Created: comment["created"]?.GetValue<string>() is { } created
                    && DateTimeOffset.TryParse(created, out var parsedDate)
                        ? parsedDate
                        : DateTimeOffset.MinValue));
        }

        return parsed.OrderBy(c => c.Created).ToList();
    }

    /// <summary>
    /// The current status and resolution of every ticket, in one call rather
    /// than one per ticket. A ticket Jira does not return - deleted, or
    /// invisible to this account - is simply absent, and the caller reports it
    /// as unknown.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, JiraIssueState>> GetStatesAsync(
        IReadOnlyList<string> ticketKeys,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var states = new Dictionary<string, JiraIssueState>(StringComparer.OrdinalIgnoreCase);

        if (ticketKeys.Count == 0)
        {
            return states;
        }

        var jql = $"key in ({string.Join(", ", ticketKeys)})";
        string? pageToken = null;

        do
        {
            var request = new JsonObject
            {
                ["jql"] = jql,
                ["fields"] = new JsonArray("status", "resolution"),
                ["maxResults"] = PageSize
            };

            if (pageToken is not null)
            {
                request["nextPageToken"] = pageToken;
            }

            var response = await client.PostAsync("rest/api/3/search/jql", credentials, request, ct);

            foreach (var issue in response?["issues"]?.AsArray() ?? [])
            {
                var key = issue?["key"]?.GetValue<string>();
                var status = issue?["fields"]?["status"]?["name"]?.GetValue<string>();

                if (key is not null && status is not null)
                {
                    // An unresolved issue carries resolution: null, which is the
                    // normal state - not a missing field.
                    states[key] = new JiraIssueState(
                        status, issue!["fields"]?["resolution"]?["name"]?.GetValue<string>());
                }
            }

            pageToken = response?["nextPageToken"]?.GetValue<string>();
        }
        while (pageToken is not null);

        return states;
    }

    /// <summary>
    /// The moves available on this issue from where it stands. The list depends
    /// on the current status and on the caller's permissions, so it is read per
    /// issue and never cached.
    /// </summary>
    public async Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(
        string ticketKey,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        // expand=transitions.fields is what reveals each transition's screen -
        // without it there is no way to know whether Resolution can be sent,
        // and sending a field the screen does not have is a hard 400.
        var response = await client.GetAsync(
            $"rest/api/3/issue/{ticketKey}/transitions?expand=transitions.fields", credentials, ct);

        var transitions = new List<JiraTransition>();

        foreach (var transition in response?["transitions"]?.AsArray() ?? [])
        {
            var id = transition?["id"]?.GetValue<string>();

            if (id is null)
            {
                continue;
            }

            transitions.Add(new JiraTransition(
                Id: id,
                Name: transition!["name"]?.GetValue<string>() ?? string.Empty,
                ToStatus: transition["to"]?["name"]?.GetValue<string>(),
                Resolution: ReadResolutionField(transition["fields"]?["resolution"])));
        }

        return transitions;
    }

    /// <summary>
    /// The Resolution dropdown on a transition screen, or null when this
    /// transition has no such field.
    /// </summary>
    private static ResolutionField? ReadResolutionField(JsonNode? field)
    {
        if (field is null)
        {
            return null;
        }

        var allowed = new List<JiraResolution>();

        foreach (var value in field["allowedValues"]?.AsArray() ?? [])
        {
            var id = value?["id"]?.GetValue<string>();
            var name = value?["name"]?.GetValue<string>();

            if (id is not null && name is not null)
            {
                allowed.Add(new JiraResolution(id, name));
            }
        }

        return new ResolutionField(field["required"]?.GetValue<bool>() ?? false, allowed);
    }

    /// <summary>
    /// Performs one transition, optionally filling in its screen's fields -
    /// which is how a resolution is set, since the dropdown belongs to the
    /// transition rather than to the issue. Jira answers 204 with no body, so
    /// there is nothing to return; a failure arrives as an exception.
    /// </summary>
    public async Task TransitionAsync(
        string ticketKey,
        string transitionId,
        AtlassianCredentials credentials,
        CancellationToken ct,
        JsonObject? fields = null)
    {
        var body = new JsonObject
        {
            ["transition"] = new JsonObject { ["id"] = transitionId }
        };

        if (fields is not null)
        {
            body["fields"] = fields;
        }

        await client.PostAsync($"rest/api/3/issue/{ticketKey}/transitions", credentials, body, ct);
    }

    /// <summary>
    /// Sets the resolution on an issue directly, or clears it when
    /// <paramref name="resolutionId"/> is null - there is no resolution called
    /// "Unresolved", that state is the field holding nothing.
    ///
    /// GOTCHA: this needs Resolution on the issue's *edit* screen. Where it is
    /// only on a transition screen Jira answers 400, which is why this is the
    /// fallback and the transition itself is preferred.
    /// </summary>
    public async Task SetResolutionAsync(
        string ticketKey,
        string? resolutionId,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["resolution"] = resolutionId is null
                    ? null
                    : new JsonObject { ["id"] = resolutionId }
            }
        };

        await client.PutAsync($"rest/api/3/issue/{ticketKey}", credentials, body, ct);
    }

    /// <summary>
    /// Every resolution the site defines. Needed to turn the configured name
    /// into an id when the resolution is set outside a transition screen.
    /// </summary>
    public async Task<IReadOnlyList<JiraResolution>> GetResolutionsAsync(
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        const string cacheKey = "jira:resolutions";

        if (cache.TryGetValue<IReadOnlyList<JiraResolution>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var response = await client.GetAsync("rest/api/3/resolution", credentials, ct);
        var resolutions = new List<JiraResolution>();

        foreach (var resolution in response?.AsArray() ?? [])
        {
            var id = resolution?["id"]?.GetValue<string>();
            var name = resolution?["name"]?.GetValue<string>();

            if (id is not null && name is not null)
            {
                resolutions.Add(new JiraResolution(id, name));
            }
        }

        cache.Set(cacheKey, (IReadOnlyList<JiraResolution>)resolutions, TimeSpan.FromHours(12));
        return resolutions;
    }

    /// <summary>
    /// People matching a partial name or email, for the approver picker.
    /// Deactivated accounts and app/bot accounts are dropped - mentioning either
    /// produces a dead link on the page.
    /// </summary>
    public async Task<IReadOnlyList<JiraUser>> SearchUsersAsync(
        string query,
        AtlassianCredentials credentials,
        CancellationToken ct,
        int limit = 20)
    {
        var response = await client.GetAsync(
            $"rest/api/3/user/search?query={Uri.EscapeDataString(query)}&maxResults={limit}", credentials, ct);

        var users = new List<JiraUser>();

        foreach (var user in response?.AsArray() ?? [])
        {
            var accountId = user?["accountId"]?.GetValue<string>();
            var displayName = user?["displayName"]?.GetValue<string>();

            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(displayName))
            {
                continue;
            }

            var active = user!["active"]?.GetValue<bool>() ?? true;
            var accountType = user["accountType"]?.GetValue<string>();

            if (!active || (accountType is not null && accountType != "atlassian"))
            {
                continue;
            }

            users.Add(new JiraUser(accountId, displayName, user["emailAddress"]?.GetValue<string>()));
        }

        return users;
    }

    /// <summary>
    /// Display name to account ID, cached. IDs are never hardcoded - they rot.
    /// </summary>
    public async Task<string?> FindAccountIdAsync(string displayName, AtlassianCredentials credentials, CancellationToken ct)
    {
        var cacheKey = $"accountId:{displayName.ToLowerInvariant()}";

        if (cache.TryGetValue<string>(cacheKey, out var cached))
        {
            return cached;
        }

        var candidates = await SearchUsersAsync(displayName, credentials, ct, limit: 10);

        var match = candidates.FirstOrDefault(user =>
                        string.Equals(user.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault();

        if (match is null)
        {
            logger.LogWarning("No Atlassian account found for '{DisplayName}'", displayName);
            return null;
        }

        cache.Set(cacheKey, match.AccountId, TimeSpan.FromHours(12));
        return match.AccountId;
    }
}
