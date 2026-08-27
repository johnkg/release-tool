using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ReleaseTool.Api.Contracts;

namespace ReleaseTool.Api.DevOps;

/// <summary>
/// Reads pull requests from Azure DevOps. The organisation, project and repo
/// all come from the URL found on the Jira ticket, so nothing needs configuring
/// per repository.
/// </summary>
public sealed class DevOpsService(HttpClient http, ILogger<DevOpsService> logger)
{
    private const string ApiVersion = "7.1";

    /// <summary>Azure DevOps is happy with a handful of concurrent reads.</summary>
    private const int MaxConcurrency = 5;

    public async Task<DevOpsLookupResponse> LookupAsync(
        IReadOnlyList<PullRequestRef> references,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var found = new List<DevOpsPullRequest>();
        var failures = new List<DevOpsLookupFailure>();

        using var gate = new SemaphoreSlim(MaxConcurrency);

        var lookups = references.Select(async reference =>
        {
            await gate.WaitAsync(ct);

            try
            {
                return (Reference: reference, Result: await FetchAsync(reference, credentials, ct), Error: (string?)null);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return (Reference: reference, Result: (DevOpsPullRequest?)null, Error: failure.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var (reference, result, error) in await Task.WhenAll(lookups))
        {
            if (result is not null)
            {
                found.Add(result);
            }
            else
            {
                logger.LogWarning("Could not read PR {Url}: {Reason}", reference.Url, error);
                failures.Add(new DevOpsLookupFailure(reference.TicketKey, reference.Url, error ?? "Unknown error."));
            }
        }

        return new DevOpsLookupResponse(Group(found), failures);
    }

    /// <summary>
    /// One section per repository. Within a repo, completed pull requests come
    /// first in the order they landed, since that is the order a deployment
    /// replays them; anything still open has no completion date and follows.
    /// </summary>
    private static List<DevOpsRepositoryGroup> Group(IEnumerable<DevOpsPullRequest> pullRequests) =>
        [.. pullRequests
            .GroupBy(pr => pr.Repository, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DevOpsRepositoryGroup(
                group.Key,
                [.. group
                    .OrderBy(pr => pr.CompletedAt is null)
                    .ThenBy(pr => pr.CompletedAt ?? pr.CreatedAt ?? DateTimeOffset.MaxValue)
                    .ThenBy(pr => pr.PullRequestId)]))];

    private async Task<DevOpsPullRequest?> FetchAsync(
        PullRequestRef reference,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        // The legacy '{org}.visualstudio.com' host still serves the REST API, so
        // the address is rebuilt from the link rather than configured.
        var url =
            $"https://{reference.Organization}.visualstudio.com/" +
            $"{Uri.EscapeDataString(reference.Project)}/_apis/git/repositories/" +
            $"{Uri.EscapeDataString(reference.Repository)}/pullrequests/{reference.PullRequestId}" +
            $"?api-version={ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials.ToBasicParameter());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Explain(response.StatusCode));
        }

        var payload = await response.Content.ReadAsStringAsync(ct);

        // A wrong PAT gets an HTML sign-in page with a 200, not a 401.
        if (JsonNode.Parse(payload) is not JsonObject pullRequest)
        {
            throw new InvalidOperationException("Azure DevOps returned a non-JSON response. Check the token.");
        }

        return new DevOpsPullRequest(
            TicketKey: reference.TicketKey,
            Project: reference.Project,
            Repository: pullRequest["repository"]?["name"]?.GetValue<string>() ?? reference.Repository,
            PullRequestId: reference.PullRequestId,
            Title: pullRequest["title"]?.GetValue<string>() ?? "(no title)",
            Status: pullRequest["status"]?.GetValue<string>() ?? "unknown",
            Author: pullRequest["createdBy"]?["displayName"]?.GetValue<string>(),
            CreatedAt: ReadDate(pullRequest["creationDate"]),
            CompletedAt: ReadDate(pullRequest["closedDate"]),
            TargetBranch: ShortBranch(pullRequest["targetRefName"]?.GetValue<string>()),
            WebUrl: reference.Url,
            AuthorEmail: pullRequest["createdBy"]?["uniqueName"]?.GetValue<string>(),
            SourceBranch: ShortBranch(pullRequest["sourceRefName"]?.GetValue<string>()),
            SourceCommit: pullRequest["lastMergeSourceCommit"]?["commitId"]?.GetValue<string>(),
            MergeCommit: pullRequest["lastMergeCommit"]?["commitId"]?.GetValue<string>(),
            Squashed: pullRequest["completionOptions"]?["squashMerge"]?.GetValue<bool>() ?? false,
            Organization: reference.Organization);
    }

    /// <summary>
    /// Who the token acts as.
    /// GOTCHA: the profile API lives on its own host, and the un-scoped
    /// `app.vssps.visualstudio.com` answers a personal access token with 401.
    /// It has to be the organisation-scoped `vssps.dev.azure.com/{org}`, and it
    /// is preview-only - there is no stable api-version for it.
    /// </summary>
    public async Task<DevOpsIdentity> WhoAmIAsync(
        string organization,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var payload = await SendAsync(
            HttpMethod.Get,
            $"https://vssps.dev.azure.com/{Uri.EscapeDataString(organization)}" +
            $"/_apis/profile/profiles/me?api-version={ApiVersion}-preview.3",
            body: null, credentials, ct);

        return new DevOpsIdentity(
            payload["displayName"]?.GetValue<string>() ?? "(unnamed account)",
            payload["emailAddress"]?.GetValue<string>());
    }

    // ---- Changed files ------------------------------------------------------

    /// <summary>
    /// Every file the release touches, per repository, labelled with the tickets
    /// that touched it. A file carrying more than one ticket is flagged: that is
    /// what makes replaying a whole repository in one go unsafe.
    /// </summary>
    public async Task<ChangedFilesResponse> ChangedFilesAsync(
        IReadOnlyList<PullRequestRef> references,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var failures = new List<DevOpsLookupFailure>();
        var found = new List<(PullRequestRef Reference, string Path)>();

        using var gate = new SemaphoreSlim(MaxConcurrency);

        var lookups = references.Select(async reference =>
        {
            await gate.WaitAsync(ct);

            try
            {
                return (Reference: reference, Paths: await FilesInAsync(reference, credentials, ct), Error: (string?)null);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return (Reference: reference, Paths: (IReadOnlyList<string>)[], Error: failure.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var (reference, paths, error) in await Task.WhenAll(lookups))
        {
            if (error is not null)
            {
                logger.LogWarning("Could not read changes for PR {Url}: {Reason}", reference.Url, error);
                failures.Add(new DevOpsLookupFailure(reference.TicketKey, reference.Url, error));
                continue;
            }

            found.AddRange(paths.Select(path => (reference, path)));
        }

        var repositories = found
            .GroupBy(entry => entry.Reference.Repository, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var files = group
                    .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(byPath =>
                    {
                        var tickets = byPath
                            .Select(entry => entry.Reference.TicketKey)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        return new ChangedFile(
                            byPath.Key,
                            tickets,
                            [.. byPath.Select(e => e.Reference.PullRequestId).Distinct().Order()],
                            Overlapping: tickets.Count > 1);
                    })
                    // The files a human has to look at first.
                    .OrderByDescending(file => file.Overlapping)
                    .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new RepositoryChanges(
                    group.Key,
                    group.First().Reference.Project,
                    files,
                    files.Any(file => file.Overlapping));
            })
            .ToList();

        return new ChangedFilesResponse(repositories, failures);
    }

    /// <summary>
    /// The paths in a pull request's latest iteration - the PR as it stands,
    /// rather than the sum of every revision it went through.
    /// </summary>
    private async Task<IReadOnlyList<string>> FilesInAsync(
        PullRequestRef reference,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var iterations = await SendAsync(
            HttpMethod.Get, PullRequestUrl(reference, "iterations"), body: null, credentials, ct);

        var latest = iterations["value"]?.AsArray()
            .Select(entry => entry?["id"]?.GetValue<int>() ?? 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        if (latest == 0)
        {
            return [];
        }

        var changes = await SendAsync(
            HttpMethod.Get, PullRequestUrl(reference, $"iterations/{latest}/changes"),
            body: null, credentials, ct);

        return [.. changes["changeEntries"]?.AsArray()
            .Select(entry => entry?["item"]?["path"]?.GetValue<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase) ?? []];
    }

    private static string PullRequestUrl(PullRequestRef reference, string suffix) =>
        $"https://{reference.Organization}.visualstudio.com/" +
        $"{Uri.EscapeDataString(reference.Project)}/_apis/git/repositories/" +
        $"{Uri.EscapeDataString(reference.Repository)}/pullRequests/{reference.PullRequestId}/" +
        $"{suffix}?api-version={ApiVersion}";

    // ---- Replaying pull requests onto a branch ------------------------------

    /// <summary>
    /// Cherry-picks each pull request's commit onto the target branch, one at a
    /// time in the order given, and <b>stops at the first failure</b>: every
    /// cherry-pick moves the target, so anything after a conflict would be built
    /// on a head that is no longer there.
    ///
    /// Cherry-pick rather than merge on purpose. A merge of the target head and
    /// a pull request commit merges two <i>histories</i>, and the deployment
    /// branch is cut from PROD while the commit sits on UAT - so it drags in
    /// everything that landed on UAT since they diverged, and conflicts on that
    /// rather than on the change being replayed. A cherry-pick applies only
    /// diff(commit^, commit), which is what "replay this pull request" means.
    /// </summary>
    public async Task<MergeResponse> MergeAsync(
        MergeRequest request,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var results = new List<MergeResult>();

        logger.LogInformation(
            "Replaying {Count} pull request(s) onto {Repository}/{TargetBranch}.",
            request.PullRequests.Count, request.Repository.Name, request.TargetBranch);

        foreach (var candidate in request.PullRequests)
        {
            MergeResult result;

            try
            {
                result = await MergeOneAsync(request.Repository, request.TargetBranch, candidate, credentials, ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                logger.LogError(failure,
                    "Cherry-picking PR !{PullRequestId} ({TicketKey}) onto {Repository}/{TargetBranch} threw.",
                    candidate.PullRequestId, candidate.TicketKey, request.Repository.Name, request.TargetBranch);

                result = new MergeResult(candidate.PullRequestId, candidate.TicketKey, false, failure.Message, null);
            }

            results.Add(result);

            if (!result.Success)
            {
                logger.LogWarning(
                    "Stopping after PR !{PullRequestId}: {Remaining} pull request(s) left untried, "
                    + "because every cherry-pick moves {TargetBranch}.",
                    candidate.PullRequestId,
                    request.PullRequests.Count - results.Count, request.TargetBranch);

                break;
            }
        }

        return new MergeResponse(results);
    }

    private async Task<MergeResult> MergeOneAsync(
        RepositoryRef repository,
        string targetBranch,
        MergeCandidate candidate,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate.SourceCommit))
        {
            return new MergeResult(candidate.PullRequestId, candidate.TicketKey, false,
                "No source commit on this pull request - fetch the pull requests again.", null);
        }

        var target = await FindRefAsync(repository, targetBranch, credentials, ct);

        if (target is null)
        {
            return new MergeResult(candidate.PullRequestId, candidate.TicketKey, false,
                $"Branch '{targetBranch}' does not exist here. Create it first.", null);
        }

        // Nothing to replay onto itself.
        if (string.Equals(target, candidate.SourceCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new MergeResult(candidate.PullRequestId, candidate.TicketKey, true,
                "Already up to date.", target);
        }

        // Azure DevOps lands the cherry-pick on a branch it creates, so it needs
        // a name of its own. Scoped to the pull request and the target so two
        // runs cannot collide, and removed again below whatever happens.
        var scratchBranch = $"release-tool/cherry-pick/{candidate.PullRequestId}-{Short(target)}";
        var scratchRef = $"refs/heads/{scratchBranch}";

        var body = new JsonObject
        {
            ["generatedRefName"] = scratchRef,
            ["ontoRefName"] = $"refs/heads/{targetBranch.Trim()}",
            ["repository"] = new JsonObject { ["name"] = repository.Name },
            ["source"] = new JsonObject
            {
                ["commitList"] = new JsonArray(new JsonObject { ["commitId"] = candidate.SourceCommit }),
            },
        };

        logger.LogInformation(
            "Cherry-picking {SourceCommit} (PR !{PullRequestId}, {TicketKey}) onto "
            + "{Repository}/{TargetBranch} at {TargetHead} via {ScratchBranch}.",
            candidate.SourceCommit, candidate.PullRequestId, candidate.TicketKey,
            repository.Name, targetBranch, target, scratchBranch);

        try
        {
            var started = await SendAsync(HttpMethod.Post, CherryPicksUrl(repository), body, credentials, ct);
            var finished = await AwaitCherryPickAsync(repository, started, credentials, ct);
            var status = finished["status"]?.GetValue<string>() ?? "unknown";

            // Same GitAsyncOperationStatus enum as the ref operations: success
            // is "completed", and a conflict arrives as "failed".
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Cherry-pick {Status} for PR !{PullRequestId} ({TicketKey}) in "
                    + "{Organization}/{Project}/{Repository}. Onto {TargetBranch} at {TargetHead}, "
                    + "picking {SourceCommit}. Reason: {FailureMessage}. "
                    + "Request: {Request}. Response: {Response}",
                    status, candidate.PullRequestId, candidate.TicketKey,
                    repository.Organization, repository.Project, repository.Name,
                    targetBranch, target, candidate.SourceCommit,
                    FailureMessage(finished) ?? "(none given)",
                    body.ToJsonString(), finished.ToJsonString());

                return new MergeResult(candidate.PullRequestId, candidate.TicketKey, false,
                    ExplainCherryPick(status, FailureMessage(finished), candidate.SourceCommit), null);
            }

            // The result lives on the generated branch; the target only moves
            // when its own ref is pointed at that commit.
            var picked = await FindRefAsync(repository, scratchBranch, credentials, ct);

            if (string.IsNullOrWhiteSpace(picked))
            {
                logger.LogWarning(
                    "Cherry-pick completed for PR !{PullRequestId} in {Repository} but {ScratchBranch} "
                    + "does not exist. Response: {Response}",
                    candidate.PullRequestId, repository.Name, scratchBranch, finished.ToJsonString());

                return new MergeResult(candidate.PullRequestId, candidate.TicketKey, false,
                    "Azure DevOps reported the cherry-pick as completed but produced no commit.", null);
            }

            var moved = await UpdateRefAsync(
                repository, targetBranch, oldObjectId: target, newObjectId: picked,
                success: "Cherry-picked.", credentials, ct);

            if (moved.Success)
            {
                logger.LogInformation(
                    "Cherry-picked PR !{PullRequestId} ({TicketKey}) onto {Repository}/{TargetBranch}. "
                    + "{TargetHead} -> {PickedCommit}.",
                    candidate.PullRequestId, candidate.TicketKey, repository.Name, targetBranch,
                    target, picked);
            }
            else
            {
                logger.LogWarning(
                    "Cherry-pick produced {PickedCommit} for PR !{PullRequestId} in {Repository}, "
                    + "but {TargetBranch} could not be moved from {TargetHead}: {Reason}.",
                    picked, candidate.PullRequestId, repository.Name, targetBranch, target, moved.Message);
            }

            return new MergeResult(candidate.PullRequestId, candidate.TicketKey, moved.Success, moved.Message, picked);
        }
        finally
        {
            // The scratch branch is an implementation detail and must not be
            // left behind - including when the cherry-pick failed, since Azure
            // DevOps may have created it before hitting the conflict.
            await DeleteScratchBranchAsync(repository, scratchBranch, credentials, ct);
        }
    }

    /// <summary>
    /// Removes the branch the cherry-pick landed on. Best effort: a leftover
    /// scratch branch is untidy, but failing the whole replay over it would be
    /// worse, so this only logs.
    /// </summary>
    private async Task DeleteScratchBranchAsync(
        RepositoryRef repository,
        string branch,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        try
        {
            if (await FindRefAsync(repository, branch, credentials, ct) is not { } existing)
            {
                return;
            }

            await UpdateRefAsync(repository, branch, oldObjectId: existing, newObjectId: NoObject,
                success: "Removed.", credentials, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogWarning(failure,
                "Could not remove the temporary branch {Branch} in {Repository}. Delete it by hand.",
                branch, repository.Name);
        }
    }

    /// <summary>
    /// The cherry-pick is queued, so the first response is usually not the
    /// answer. Polls the operation until it settles or the budget runs out.
    /// </summary>
    private async Task<JsonObject> AwaitCherryPickAsync(
        RepositoryRef repository,
        JsonObject cherryPick,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        const int MaxAttempts = 40;

        var identifier = cherryPick["cherryPickId"]?.GetValue<int?>();
        var current = cherryPick;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var status = current["status"]?.GetValue<string>() ?? string.Empty;

            // notSet / queued / inProgress are the only non-terminal states.
            if (status is not ("queued" or "inProgress" or "notSet" or ""))
            {
                return current;
            }

            if (identifier is null)
            {
                return current;
            }

            await Task.Delay(500, ct);

            current = await SendAsync(
                HttpMethod.Get, CherryPickOperationUrl(repository, identifier.Value),
                body: null, credentials, ct);
        }

        return current;
    }

    private static string CherryPicksUrl(RepositoryRef repository) =>
        $"https://{repository.Organization}.visualstudio.com/" +
        $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/" +
        $"{Uri.EscapeDataString(repository.Name)}/cherryPicks?api-version={ApiVersion}-preview.1";

    /// <summary>
    /// The operation's own address. A query parameter on the collection URL is
    /// not it - the id belongs in the path.
    /// </summary>
    private static string CherryPickOperationUrl(RepositoryRef repository, int cherryPickId) =>
        $"https://{repository.Organization}.visualstudio.com/" +
        $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/" +
        $"{Uri.EscapeDataString(repository.Name)}/cherryPicks/{cherryPickId}" +
        $"?api-version={ApiVersion}-preview.1";

    /// <summary>Azure DevOps explains a failed operation here, and nowhere else.</summary>
    private static string? FailureMessage(JsonObject operation) =>
        operation["detailedStatus"]?["failureMessage"]?.GetValue<string>() is { Length: > 0 } message
            ? message
            : null;

    private static string Because(string? failureMessage) =>
        failureMessage is null ? string.Empty : $": {failureMessage.TrimEnd('.')}";

    /// <summary>
    /// The statuses are <c>GitAsyncOperationStatus</c>: notSet, queued,
    /// inProgress, completed, failed, abandoned. A conflict arrives as
    /// <c>failed</c> with the detail in the failure message, so that message is
    /// the useful half and is always carried through.
    /// </summary>
    private static string ExplainCherryPick(string status, string? failureMessage, string sourceCommit)
    {
        var reason = Because(failureMessage);

        return status switch
        {
            "failed" when failureMessage is null =>
                $"Azure DevOps could not cherry-pick {Short(sourceCommit)} - no reason given. "
                + "A conflict is the usual cause; check the branch in Azure DevOps.",
            "failed" => $"Could not cherry-pick {Short(sourceCommit)}{reason}.",
            "abandoned" => $"Azure DevOps abandoned the cherry-pick{reason}.",
            "queued" or "inProgress" =>
                "The cherry-pick was still running when the tool stopped waiting. "
                + "Check Azure DevOps before retrying, or it may be applied twice.",
            _ => $"Cherry-pick did not succeed ({status}){reason}."
        };
    }

    private static string Short(string commit) =>
        commit.Length > 8 ? commit[..8] : commit;

    // ---- Deployment branches ------------------------------------------------

    /// <summary>An all-zero object id is how the refs API spells "did not exist".</summary>
    private const string NoObject = "0000000000000000000000000000000000000000";

    /// <summary>
    /// Cuts <paramref name="request"/>.BranchName from the source branch in every
    /// listed repository. Each repository is reported on separately: one repo the
    /// token cannot write to must not strand the rest half-created.
    /// </summary>
    public async Task<BranchOperationResponse> CreateBranchesAsync(
        BranchRequest request,
        DevOpsCredentials credentials,
        CancellationToken ct) =>
        new(await ForEachRepositoryAsync(request.Repositories, async repository =>
        {
            var source = await FindRefAsync(repository, request.SourceBranch, credentials, ct);

            if (source is null)
            {
                return new BranchResult(repository.Name, repository.Project, false,
                    $"Source branch '{request.SourceBranch}' does not exist here.");
            }

            if (await FindRefAsync(repository, request.BranchName, credentials, ct) is not null)
            {
                return new BranchResult(repository.Name, repository.Project, false,
                    "Branch already exists.");
            }

            return await UpdateRefAsync(
                repository, request.BranchName, oldObjectId: NoObject, newObjectId: source,
                success: $"Created from {request.SourceBranch}.", credentials, ct);
        }, ct));

    /// <summary>
    /// Deletes the branch wherever it exists. A repository that never had it is
    /// reported as such rather than as a failure - deleting is a tidy-up, and
    /// "already gone" is the outcome the user wanted.
    /// </summary>
    public async Task<BranchOperationResponse> DeleteBranchesAsync(
        BranchRequest request,
        DevOpsCredentials credentials,
        CancellationToken ct) =>
        new(await ForEachRepositoryAsync(request.Repositories, async repository =>
        {
            var existing = await FindRefAsync(repository, request.BranchName, credentials, ct);

            if (existing is null)
            {
                return new BranchResult(repository.Name, repository.Project, true, "Not present.");
            }

            return await UpdateRefAsync(
                repository, request.BranchName, oldObjectId: existing, newObjectId: NoObject,
                success: "Deleted.", credentials, ct);
        }, ct));

    /// <summary>Drives the tab's per-repository state without changing anything.</summary>
    public async Task<BranchStatusResponse> BranchStatusAsync(
        BranchRequest request,
        DevOpsCredentials credentials,
        CancellationToken ct) =>
        new(await ForEachRepositoryAsync(request.Repositories, async repository =>
        {
            try
            {
                var exists = await FindRefAsync(repository, request.BranchName, credentials, ct) is not null;

                var sourceExists = string.IsNullOrWhiteSpace(request.SourceBranch)
                    || await FindRefAsync(repository, request.SourceBranch, credentials, ct) is not null;

                return new BranchStatus(repository.Name, repository.Project, exists, sourceExists, null);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return new BranchStatus(repository.Name, repository.Project, false, false, failure.Message);
            }
        }, ct));

    /// <summary>
    /// Runs the same operation over every repository, capped like the PR reads,
    /// turning an unexpected failure into a result rather than losing the batch.
    /// </summary>
    private async Task<List<T>> ForEachRepositoryAsync<T>(
        IReadOnlyList<RepositoryRef> repositories,
        Func<RepositoryRef, Task<T>> operation,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);

        var work = repositories.Select(async repository =>
        {
            await gate.WaitAsync(ct);

            try
            {
                return await operation(repository);
            }
            finally
            {
                gate.Release();
            }
        });

        return [.. await Task.WhenAll(work)];
    }

    /// <summary>The commit a branch points at, or null when there is no such branch.</summary>
    private async Task<string?> FindRefAsync(
        RepositoryRef repository,
        string branch,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var name = $"refs/heads/{branch.Trim()}";

        // filter is a prefix match, so 'main' would also return 'main-old'.
        // The exact name is picked out of the results below.
        var payload = await SendAsync(
            HttpMethod.Get,
            RefsUrl(repository, $"&filter={Uri.EscapeDataString($"heads/{branch.Trim()}")}"),
            body: null, credentials, ct);

        return payload["value"]?.AsArray()
            .FirstOrDefault(entry =>
                string.Equals(entry?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            ?["objectId"]?.GetValue<string>();
    }

    private async Task<BranchResult> UpdateRefAsync(
        RepositoryRef repository,
        string branch,
        string oldObjectId,
        string newObjectId,
        string success,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        var body = new JsonArray(new JsonObject
        {
            ["name"] = $"refs/heads/{branch.Trim()}",
            ["oldObjectId"] = oldObjectId,
            ["newObjectId"] = newObjectId,
        });

        var payload = await SendAsync(HttpMethod.Post, RefsUrl(repository), body, credentials, ct);
        var outcome = payload["value"]?.AsArray().FirstOrDefault();

        if (outcome?["success"]?.GetValue<bool>() == true)
        {
            return new BranchResult(repository.Name, repository.Project, true, success);
        }

        // updateStatus carries the actionable reason, e.g. createBranchPermissionRequired.
        var status = outcome?["updateStatus"]?.GetValue<string>() ?? "unknown";

        return new BranchResult(repository.Name, repository.Project, false, Explain(status));
    }

    private static string RefsUrl(RepositoryRef repository, string extra = "") =>
        $"https://{repository.Organization}.visualstudio.com/" +
        $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/" +
        $"{Uri.EscapeDataString(repository.Name)}/refs?api-version={ApiVersion}{extra}";

    private async Task<JsonObject> SendAsync(
        HttpMethod method,
        string url,
        JsonNode? body,
        DevOpsCredentials credentials,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials.ToBasicParameter());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Explain(response.StatusCode));
        }

        // Same trap as the PR reads: a wrong PAT gets an HTML sign-in page with a 200.
        return JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidOperationException("Azure DevOps returned a non-JSON response. Check the token.");
    }

    /// <summary>Turns the refs API's updateStatus into something actionable.</summary>
    private static string Explain(string updateStatus) => updateStatus switch
    {
        "createBranchPermissionRequired" => "The token lacks permission to create branches here.",
        "forcePushRequired" => "That branch exists and points somewhere else.",
        "invalidRefName" => "Azure DevOps rejected the branch name.",
        "rejectedByPlugin" => "A branch policy rejected this.",
        _ => $"Azure DevOps refused the change ({updateStatus})."
    };

    private static string Explain(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Azure DevOps rejected the token.",
        HttpStatusCode.NonAuthoritativeInformation => "Azure DevOps rejected the token.",
        HttpStatusCode.Forbidden => "The token lacks Code (read, write) permission for this project.",
        HttpStatusCode.NotFound => "No such repository, or no access to it.",
        _ => $"Azure DevOps returned {(int)status}."
    };

    private static DateTimeOffset? ReadDate(JsonNode? node) =>
        node?.GetValue<string>() is { } text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;

    /// <summary>'refs/heads/release/1.2.3' reads better as 'release/1.2.3'.</summary>
    private static string? ShortBranch(string? refName) =>
        refName?.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) == true
            ? refName["refs/heads/".Length..]
            : refName;
}
