using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReleaseTool.Api.Adf;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Configuration;
using ReleaseTool.Api.Confluence;
using ReleaseTool.Api.DevOps;
using ReleaseTool.Api.Contracts;
using ReleaseTool.Api.Jira;

namespace ReleaseTool.Api.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        // Every endpoint acts as the caller, so the whole group requires credentials.
        var api = app.MapGroup("/api").AddEndpointFilter<AtlassianCredentialsFilter>();

        // Azure DevOps uses a different credential, so it gets its own group
        // rather than forcing callers to send Atlassian headers they do not need.
        var devops = app.MapGroup("/api/devops").AddEndpointFilter<DevOpsCredentialsFilter>();

        devops.MapPost("/pull-requests", LookupPullRequestsAsync);
        devops.MapPost("/changed-files", ChangedFilesAsync);
        devops.MapPost("/merge", MergeAsync);
        devops.MapGet("/me", WhoAmIAsync);
        devops.MapPost("/branches/status", BranchStatusAsync);
        devops.MapPost("/branches", CreateBranchesAsync);
        devops.MapPost("/branches/delete", DeleteBranchesAsync);

        // Outside the guarded group on purpose: the UI reads this before it has
        // any credentials, to find out whether it still needs to ask for them.
        app.MapGet("/api/config", GetConfig);

        // Also outside: settings hold no secrets, and the Deployment tab has to
        // work without anyone ever touching the Atlassian tab.
        app.MapGet("/api/settings", (SettingsStore store, CancellationToken ct) => store.ReadAsync(ct));
        app.MapPut("/api/settings", async (SettingsStore store, AppSettings settings, CancellationToken ct) =>
            Results.Ok(await store.WriteAsync(settings, ct)));

        api.MapPost("/auth/verify", VerifyAsync);
        api.MapGet("/jira/users", SearchUsersAsync);
        api.MapPost("/jira/statuses", TicketStatusesAsync);
        api.MapPost("/jira/transition", TransitionTicketsAsync);
        api.MapPost("/jira/resolution", SetResolutionAsync);
        api.MapGet("/confluence/pages", ListPagesAsync);
        api.MapGet("/confluence/page", FindPageAsync);
        api.MapGet("/approvals/{pageId}", GetApprovalsAsync);
        api.MapPost("/approvals/{pageId}/resolve", ResolveAsync);
        api.MapPost("/approvals/{pageId}/apply", ApplyAsync);
        api.MapPost("/approvals/{pageId}/clear", ClearAsync);

        return app;
    }

    /// <summary>
    /// Tells the UI which credentials the server already holds, so it can skip
    /// asking for them. Reports existence only - a token must never travel back
    /// to the browser, where any script on the origin could read it.
    /// </summary>
    private static IResult GetConfig(
        HttpContext context,
        IOptions<AtlassianOptions> options,
        IOptions<StoredCredentialsOptions> stored)
    {
        var workflow = new ReleaseWorkflowNames(
            options.Value.DeployedToProductionStatus,
            options.Value.ReadyForDeploymentStatus,
            options.Value.ResolutionName);

        // The host's theme is only the caller's theme when they are the same
        // machine. A hosted instance must not push its server's setting at
        // everyone who opens it.
        var remote = context.Connection.RemoteIpAddress;
        var local = remote is not null && IPAddress.IsLoopback(remote);

        return Results.Ok(new ConfigResponse(
            Atlassian: new StoredCredentialStatus(
                stored.Value.HasAtlassian,
                stored.Value.HasAtlassian ? stored.Value.AtlassianEmail.Trim() : null),
            DevOps: new StoredCredentialStatus(stored.Value.HasDevOps, null),
            DefaultSpaceKey: options.Value.DefaultSpaceKey,
            Workflow: workflow,
            OsTheme: local ? OsTheme.Read() : null));
    }

    /// <summary>Proves the token works and tells the UI who is connected.</summary>
    private static async Task<IResult> VerifyAsync(
        HttpContext context,
        AtlassianClient client,
        CancellationToken ct)
    {
        var credentials = AtlassianCredentialsFilter.Get(context);
        var me = await client.GetAsync("rest/api/3/myself", credentials, ct);

        if (me is null)
        {
            return Results.Problem(
                title: "Unexpected empty response from Jira",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new VerifyResponse(
            AccountId: me["accountId"]?.GetValue<string>() ?? string.Empty,
            DisplayName: me["displayName"]?.GetValue<string>() ?? string.Empty,
            Email: me["emailAddress"]?.GetValue<string>()));
    }

    /// <summary>
    /// Feeds the approver picker. Searching Jira rather than accepting typed
    /// text is what guarantees the mention resolves to a real account.
    /// </summary>
    private static async Task<IResult> SearchUsersAsync(
        HttpContext context,
        JiraService jira,
        string? query,
        CancellationToken ct)
    {
        var trimmed = query?.Trim() ?? string.Empty;

        // Jira matches very broadly on one character; wait for something usable.
        if (trimmed.Length < 2)
        {
            return Results.Ok(new UserSearchResponse([]));
        }

        var credentials = AtlassianCredentialsFilter.Get(context);
        return Results.Ok(new UserSearchResponse(await jira.SearchUsersAsync(trimmed, credentials, ct)));
    }

    /// <summary>
    /// Where the given tickets stand in the workflow right now. Read-only, so
    /// the UI can show the current status before anyone presses a button.
    /// </summary>
    private static async Task<IResult> TicketStatusesAsync(
        HttpContext context,
        StatusTransitioner transitioner,
        IOptions<AtlassianOptions> options,
        TicketStatusRequest request,
        CancellationToken ct)
    {
        var keys = InScopeKeysOf(request.TicketKeys, options.Value.TicketKeyPrefix);

        if (keys.Count == 0)
        {
            return Results.Ok(new TicketStatusResponse([]));
        }

        var credentials = AtlassianCredentialsFilter.Get(context);
        return Results.Ok(new TicketStatusResponse(await transitioner.ReadAsync(keys, credentials, ct)));
    }

    /// <summary>
    /// Moves the given tickets into one of the two release statuses. The caller
    /// decides which tickets qualify - on the Atlassian tab, the rows where the
    /// connected user is the resolved developer - so the preview and the write
    /// cannot disagree, the same rule the PR approval follows.
    /// </summary>
    private static async Task<IResult> TransitionTicketsAsync(
        HttpContext context,
        StatusTransitioner transitioner,
        IOptions<AtlassianOptions> options,
        TransitionRequest request,
        CancellationToken ct)
    {
        var keys = InScopeKeysOf(request.TicketKeys, options.Value.TicketKeyPrefix);

        if (keys.Count == 0)
        {
            return Results.Problem(
                title: "Choose at least one ticket.",
                detail: "Only PROJECT tickets can be transitioned; OTHER_PROJECT tickets are skipped.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var credentials = AtlassianCredentialsFilter.Get(context);
        return Results.Ok(await transitioner.ApplyAsync(keys, request.Target, credentials, ct));
    }

    /// <summary>
    /// Sets or clears the resolution without touching the status. Going live
    /// sets it as part of the transition; this is what puts it back, since
    /// "Unresolved" is the field cleared rather than a value to transition to.
    /// </summary>
    private static async Task<IResult> SetResolutionAsync(
        HttpContext context,
        StatusTransitioner transitioner,
        IOptions<AtlassianOptions> options,
        ResolutionRequest request,
        CancellationToken ct)
    {
        var keys = InScopeKeysOf(request.TicketKeys, options.Value.TicketKeyPrefix);

        if (keys.Count == 0)
        {
            return Results.Problem(
                title: "Choose at least one ticket.",
                detail: "Only PROJECT tickets can be changed; OTHER_PROJECT tickets are skipped.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var credentials = AtlassianCredentialsFilter.Get(context);
        return Results.Ok(await transitioner.ApplyResolutionAsync(keys, request.Resolved, credentials, ct));
    }

    /// <summary>Feeds the page picker. Newest page first.</summary>
    private static async Task<IResult> ListPagesAsync(
        HttpContext context,
        ConfluenceService confluence,
        IOptions<AtlassianOptions> options,
        string? spaceKey,
        CancellationToken ct)
    {
        var credentials = AtlassianCredentialsFilter.Get(context);
        var key = string.IsNullOrWhiteSpace(spaceKey) ? options.Value.DefaultSpaceKey : spaceKey;

        var spaceId = await confluence.ResolveSpaceIdAsync(key, credentials, ct);
        var pages = await confluence.ListPagesAsync(spaceId, credentials, ct);

        return Results.Ok(new PageListResponse(spaceId, pages));
    }

    private static async Task<IResult> FindPageAsync(
        HttpContext context,
        ConfluenceService confluence,
        IOptions<AtlassianOptions> options,
        string? spaceKey,
        string? title,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.Problem(title: "A page title is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var credentials = AtlassianCredentialsFilter.Get(context);
        var key = string.IsNullOrWhiteSpace(spaceKey) ? options.Value.DefaultSpaceKey : spaceKey;

        var spaceId = await confluence.ResolveSpaceIdAsync(key, credentials, ct);
        return Results.Ok(await confluence.FindPageAsync(spaceId, title, credentials, ct));
    }

    private static async Task<IResult> GetApprovalsAsync(
        HttpContext context,
        ConfluenceService confluence,
        IOptions<AtlassianOptions> options,
        string pageId,
        CancellationToken ct)
    {
        var credentials = AtlassianCredentialsFilter.Get(context);
        var page = await confluence.FetchDocumentAsync(pageId, credentials, ct);

        var table = LocateTable(page);
        var columns = ApprovalsTable.ResolveColumns(table);

        var rows = ApprovalsTable.ExtractRows(table, columns, options.Value.TicketKeyPrefix)
            .Select(r => new ApprovalRow(r.TicketKey, r.RowIndex, r.Values))
            .ToList();

        return Results.Ok(new ApprovalsResponse(
            pageId,
            page.Version,
            [.. columns.All.Keys],
            rows));
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext context,
        DeveloperResolver resolver,
        IOptions<AtlassianOptions> options,
        string pageId,
        ResolveRequest request,
        CancellationToken ct)
    {
        var credentials = AtlassianCredentialsFilter.Get(context);
        var keys = InScopeKeysOf(request.TicketKeys, options.Value.TicketKeyPrefix);

        if (keys.Count == 0)
        {
            return Results.Ok(new ResolveResponse([], [], []));
        }

        var result = await resolver.ResolveAsync(keys, credentials, ct);

        return Results.Ok(new ResolveResponse(
            result.Assignments, result.PullRequests, result.FixedByNotes));
    }

    /// <summary>
    /// Reads the pull requests found on the tickets, grouped by repository.
    /// The caller passes the references it already has from resolve, so this
    /// never needs to re-read Jira.
    /// </summary>
    private static async Task<IResult> LookupPullRequestsAsync(
        HttpContext context,
        DevOpsService devops,
        DevOpsLookupRequest request,
        CancellationToken ct)
    {
        if (request.PullRequests.Count == 0)
        {
            return Results.Ok(new DevOpsLookupResponse([], []));
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.LookupAsync(request.PullRequests, credentials, ct));
    }

    /// <summary>
    /// Who the Azure DevOps token acts as, for the UI to show. The profile API
    /// is organisation-scoped, so the configured default organisation is used.
    /// </summary>
    private static async Task<IResult> WhoAmIAsync(
        HttpContext context,
        DevOpsService devops,
        SettingsStore store,
        CancellationToken ct)
    {
        var settings = await store.ReadAsync(ct);

        if (string.IsNullOrWhiteSpace(settings.DefaultOrganization))
        {
            return Results.Problem(
                title: "No Azure DevOps organisation configured.",
                detail: "Set the default organisation on the Settings tab.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.WhoAmIAsync(settings.DefaultOrganization, credentials, ct));
    }

    /// <summary>
    /// Every file the release touches, per repository, labelled with the tickets
    /// behind it. Takes the same references the pull request lookup does.
    /// </summary>
    private static async Task<IResult> ChangedFilesAsync(
        HttpContext context,
        DevOpsService devops,
        DevOpsLookupRequest request,
        CancellationToken ct)
    {
        if (request.PullRequests.Count == 0)
        {
            return Results.Ok(new ChangedFilesResponse([], []));
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.ChangedFilesAsync(request.PullRequests, credentials, ct));
    }

    /// <summary>
    /// Replays pull requests onto a deployment or candidate branch, in order,
    /// stopping at the first failure so the caller knows where to pick up.
    /// </summary>
    private static async Task<IResult> MergeAsync(
        HttpContext context,
        DevOpsService devops,
        MergeRequest request,
        CancellationToken ct)
    {
        if (request.PullRequests.Count == 0)
        {
            return Results.Problem(
                title: "Choose at least one pull request.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.TargetBranch))
        {
            return Results.Problem(
                title: "No target branch.",
                detail: "Merging needs the deployment or candidate branch to merge into.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.MergeAsync(request, credentials, ct));
    }

    /// <summary>
    /// Reports whether the deployment branch is already there, per repository.
    /// Read-only, so the tab can show state before anyone presses a button.
    /// </summary>
    private static async Task<IResult> BranchStatusAsync(
        HttpContext context,
        DevOpsService devops,
        BranchRequest request,
        CancellationToken ct)
    {
        if (request.Repositories.Count == 0)
        {
            return Results.Ok(new BranchStatusResponse([]));
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.BranchStatusAsync(request, credentials, ct));
    }

    private static async Task<IResult> CreateBranchesAsync(
        HttpContext context,
        DevOpsService devops,
        BranchRequest request,
        CancellationToken ct)
    {
        if (Invalid(request) is { } problem)
        {
            return problem;
        }

        if (string.IsNullOrWhiteSpace(request.SourceBranch))
        {
            return Results.Problem(
                title: "No source branch.",
                detail: "Set the PROD branch on the Settings tab - that is what the deployment branch is cut from.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.CreateBranchesAsync(request, credentials, ct));
    }

    private static async Task<IResult> DeleteBranchesAsync(
        HttpContext context,
        DevOpsService devops,
        BranchRequest request,
        CancellationToken ct)
    {
        if (Invalid(request) is { } problem)
        {
            return problem;
        }

        var credentials = DevOpsCredentialsFilter.Get(context);
        return Results.Ok(await devops.DeleteBranchesAsync(request, credentials, ct));
    }

    /// <summary>
    /// Shared guard for the write operations. A blank or malformed branch name
    /// must be caught here rather than sent to every repository in turn.
    /// </summary>
    private static IResult? Invalid(BranchRequest request)
    {
        if (request.Repositories.Count == 0)
        {
            return Results.Problem(
                title: "Choose at least one repository.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var branch = request.BranchName?.Trim() ?? string.Empty;

        if (branch.Length == 0)
        {
            return Results.Problem(title: "A branch name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Git refuses these outright; catching them here gives one clear message
        // instead of the same refusal repeated per repository.
        if (branch.StartsWith('/') || branch.EndsWith('/') || branch.Contains("//")
            || branch.Contains("..") || branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || branch.Any(c => c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            return Results.Problem(
                title: "That is not a valid branch name.",
                detail: "No spaces, no '..', no leading or trailing '/', and none of ~ ^ : ? * [ \\.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static async Task<IResult> ApplyAsync(
        HttpContext context,
        ConfluenceService confluence,
        IOptions<AtlassianOptions> options,
        string pageId,
        ApplyRequest request,
        CancellationToken ct)
    {
        var credentials = AtlassianCredentialsFilter.Get(context);

        // Writing a mention without an account ID would produce a broken node.
        var unresolved = request.Assignments
            .Where(a => request.WriteDeveloper && string.IsNullOrWhiteSpace(a.AccountId))
            .Select(a => a.TicketKey)
            .ToList();

        if (unresolved.Count > 0)
        {
            return Results.Problem(
                title: "Some assignments have no Atlassian account ID.",
                detail: $"Unresolved: {string.Join(", ", unresolved)}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.PrApproval is { } approval && string.IsNullOrWhiteSpace(approval.AccountId))
        {
            return Results.Problem(
                title: "The PR approver has no Atlassian account ID.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await EnsureUnchangedAsync(confluence, pageId, request.ExpectedVersion, credentials, ct);

        var byTicket = request.Assignments.ToDictionary(a => a.TicketKey, StringComparer.OrdinalIgnoreCase);

        var approvedTickets = request.PrApproval is null
            ? []
            : new HashSet<string>(request.PrApproval.TicketKeys, StringComparer.OrdinalIgnoreCase);

        var (version, changed) = await confluence.UpdateWithRetryAsync(
            pageId,
            page =>
            {
                var table = LocateTable(page);
                var columns = ApprovalsTable.ResolveColumns(table);
                var updated = 0;

                // Re-extract per attempt: a retry runs against a refetched document.
                foreach (var row in ApprovalsTable.ExtractRows(table, columns, options.Value.TicketKeyPrefix))
                {
                    if (!byTicket.TryGetValue(row.TicketKey, out var assignment))
                    {
                        continue;
                    }

                    if (request.WriteDeveloper && columns[ApprovalColumn.DeveloperAssigned] is { } developerColumn)
                    {
                        updated += Count(ApprovalsTable.SetMention(
                            table, row.RowIndex, developerColumn, assignment.AccountId!, assignment.DeveloperName));
                    }

                    if (request.WriteRequestedBy
                        && columns[ApprovalColumn.RequestedBy] is { } requestedColumn
                        && !string.IsNullOrWhiteSpace(assignment.ReporterAccountId))
                    {
                        updated += Count(ApprovalsTable.SetMention(
                            table, row.RowIndex, requestedColumn,
                            assignment.ReporterAccountId!, assignment.ReporterName!));
                    }

                    if (request.PrApproval is null || !approvedTickets.Contains(row.TicketKey))
                    {
                        continue;
                    }

                    if (columns[ApprovalColumn.PrApprovedBy] is { } approverColumn)
                    {
                        updated += Count(ApprovalsTable.SetMention(
                            table, row.RowIndex, approverColumn,
                            request.PrApproval.AccountId, request.PrApproval.DisplayName));
                    }

                    // Recording an approver implies both statuses.
                    foreach (var column in (ApprovalColumn[])
                             [ApprovalColumn.PrApprovedStatus, ApprovalColumn.MergedToDeploymentBranch])
                    {
                        if (columns[column] is { } statusColumn)
                        {
                            updated += Count(ApprovalsTable.SetStatusLike(
                                table, row.RowIndex, statusColumn, ApprovalColumns.StatusTextFor(column)));
                        }
                    }
                }

                return updated;
            },
            $"Approvals filled in by Release Tool ({request.Assignments.Count} tickets)",
            credentials,
            ct);

        return Results.Ok(new ApplyResponse(pageId, version, changed));
    }

    /// <summary>Empties chosen columns on every PROJECT row, for resetting a test page.</summary>
    private static async Task<IResult> ClearAsync(
        HttpContext context,
        ConfluenceService confluence,
        IOptions<AtlassianOptions> options,
        string pageId,
        ClearRequest request,
        CancellationToken ct)
    {
        var credentials = AtlassianCredentialsFilter.Get(context);

        if (request.Columns.Count == 0)
        {
            return Results.Problem(
                title: "Choose at least one column to clear.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await EnsureUnchangedAsync(confluence, pageId, request.ExpectedVersion, credentials, ct);

        var (version, changed) = await confluence.UpdateWithRetryAsync(
            pageId,
            page =>
            {
                var table = LocateTable(page);
                var columns = ApprovalsTable.ResolveColumns(table);
                var cleared = 0;

                foreach (var row in ApprovalsTable.ExtractRows(table, columns, options.Value.TicketKeyPrefix))
                {
                    foreach (var column in request.Columns)
                    {
                        if (columns[column] is { } index)
                        {
                            cleared += Count(ApprovalsTable.ClearCell(table, row.RowIndex, index));
                        }
                    }
                }

                return cleared;
            },
            $"Approvals columns cleared by Release Tool ({string.Join(", ", request.Columns)})",
            credentials,
            ct);

        return Results.Ok(new ApplyResponse(pageId, version, changed));
    }

    private static async Task EnsureUnchangedAsync(
        ConfluenceService confluence,
        string pageId,
        int expectedVersion,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var current = await confluence.FetchDocumentAsync(pageId, credentials, ct);

        if (current.Version != expectedVersion)
        {
            throw ReleaseToolException.Conflict(
                $"The page moved from version {expectedVersion} to {current.Version} since it was loaded. Reload and review before applying.");
        }
    }

    /// <summary>
    /// Narrows a list of ticket keys to the configured project. A release page
    /// often lists tickets from several, and only one of them is ours to touch.
    /// </summary>
    private static List<string> InScopeKeysOf(IEnumerable<string> keys, string ticketPrefix) =>
        keys.Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => ApprovalsTable.IsInScope(k, ticketPrefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int Count(bool written) => written ? 1 : 0;

    private static JsonObject LocateTable(PageDocument page) =>
        ApprovalsTable.Locate(page.Document)
        ?? throw ReleaseToolException.NotFound(
            "No table found after an 'Approvals' heading on that page.");
}
