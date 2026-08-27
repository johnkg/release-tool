using System.Net;
using System.Net.Http.Json;
using ReleaseTool.Api.DevOps;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// The files a release touches, and replaying its pull requests onto a
/// deployment or candidate branch.
/// </summary>
public class ChangedFilesAndMergeTests
{
    private const string Target = "dev/release/feat/PROJECT-RELEASE-13082026";
    private const string TargetSha = "1111111111111111111111111111111111111111";
    private const string SourceSha = "2222222222222222222222222222222222222222";
    private const string MergeSha = "3333333333333333333333333333333333333333";

    private static HttpClient WithDevOpsToken(TestApp app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(DevOpsCredentials.TokenHeader, "pat-123");
        return client;
    }

    private static object PullRequest(string ticket, int id) => new
    {
        ticketKey = ticket,
        url = $"https://your-organization.visualstudio.com/Platform/_git/sample-web/pullrequest/{id}",
        organization = "your-organization",
        project = "Platform",
        repository = "sample-web",
        pullRequestId = id,
        commentAuthor = "Someone",
        commentedAt = "2026-08-01T00:00:00Z",
    };

    private static object Repository => new
    {
        organization = "your-organization",
        project = "Platform",
        name = "sample-web",
    };

    // ---- Changed files ------------------------------------------------------

    /// <summary>
    /// Two tickets on the same file is the signal the Deployment tab uses to
    /// hold back a whole-repository merge.
    /// </summary>
    [Fact]
    public async Task Changed_files_are_labelled_with_every_ticket_that_touched_them()
    {
        var stub = new StubAtlassian()
            .OnGet("pullRequests/4821/iterations?", """{ "value": [ { "id": 1 }, { "id": 2 } ] }""")
            .OnGet("pullRequests/4821/iterations/2/changes", """
            { "changeEntries": [
                { "item": { "path": "/src/Shared.cs" } },
                { "item": { "path": "/src/OnlyMine.cs" } } ] }
            """)
            .OnGet("pullRequests/4822/iterations?", """{ "value": [ { "id": 1 } ] }""")
            .OnGet("pullRequests/4822/iterations/1/changes", """
            { "changeEntries": [ { "item": { "path": "/src/Shared.cs" } } ] }
            """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/changed-files", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821), PullRequest("PROJECT-1815", 4822) },
        });

        var repository = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0];

        Assert.True(repository.GetProperty("hasOverlap").GetBoolean());

        var files = repository.GetProperty("files").EnumerateArray().ToList();

        // The overlapping file sorts first: it is the one a human must look at.
        Assert.Equal("/src/Shared.cs", files[0].GetProperty("path").GetString());
        Assert.True(files[0].GetProperty("overlapping").GetBoolean());

        var tickets = files[0].GetProperty("ticketKeys").EnumerateArray()
            .Select(k => k.GetString()).ToList();

        Assert.Equal(["PROJECT-1814", "PROJECT-1815"], tickets);

        Assert.False(files[1].GetProperty("overlapping").GetBoolean());
    }

    [Fact]
    public async Task A_release_with_no_shared_file_reports_no_overlap()
    {
        var stub = new StubAtlassian()
            .OnGet("pullRequests/4821/iterations?", """{ "value": [ { "id": 1 } ] }""")
            .OnGet("pullRequests/4821/iterations/1/changes",
                """{ "changeEntries": [ { "item": { "path": "/src/One.cs" } } ] }""")
            .OnGet("pullRequests/4822/iterations?", """{ "value": [ { "id": 1 } ] }""")
            .OnGet("pullRequests/4822/iterations/1/changes",
                """{ "changeEntries": [ { "item": { "path": "/src/Two.cs" } } ] }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/changed-files", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821), PullRequest("PROJECT-1815", 4822) },
        });

        var repository = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0];

        Assert.False(repository.GetProperty("hasOverlap").GetBoolean());
    }

    /// <summary>One unreadable pull request must not lose the rest.</summary>
    [Fact]
    public async Task A_pull_request_whose_changes_cannot_be_read_is_reported_separately()
    {
        var stub = new StubAtlassian()
            .OnGet("pullRequests/4821/iterations?", "{}", HttpStatusCode.Forbidden)
            .OnGet("pullRequests/4822/iterations?", """{ "value": [ { "id": 1 } ] }""")
            .OnGet("pullRequests/4822/iterations/1/changes",
                """{ "changeEntries": [ { "item": { "path": "/src/Two.cs" } } ] }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/changed-files", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821), PullRequest("PROJECT-1815", 4822) },
        });

        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Single(payload.GetProperty("repositories").EnumerateArray());
        Assert.Single(payload.GetProperty("failures").EnumerateArray());
        Assert.Contains("Code", payload.GetProperty("failures")[0].GetProperty("reason").GetString()!);
    }

    // ---- Cherry-picking -----------------------------------------------------

    /// <summary>
    /// Mirrors the real shape: GitAsyncOperationStatus spells success
    /// "completed", and the result lands on the generated branch rather than
    /// being returned in the response.
    /// </summary>
    private static StubAtlassian CherryPickStub(string status = "completed") =>
        new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Target}}", "objectId": "{{TargetSha}}" } ] }""")
            // The scratch branch the cherry-pick is generated onto.
            // One entry per pull request in the tests below: the branch name
            // carries the PR id, and FindRefAsync matches the exact name.
            .OnGet("filter=heads%2Frelease-tool", $$"""
            { "value": [
                { "name": "refs/heads/release-tool/cherry-pick/4821-11111111", "objectId": "{{MergeSha}}" },
                { "name": "refs/heads/release-tool/cherry-pick/4822-11111111", "objectId": "{{MergeSha}}" } ] }
            """)
            .OnPost("/cherryPicks?api-version=7.1-preview.1",
                $$"""{ "cherryPickId": 3, "status": "{{status}}" }""")
            .OnPost("/refs?api-version=7.1",
                """{ "value": [ { "success": true, "updateStatus": "succeeded" } ] }""");

    /// <summary>
    /// Cherry-pick, not merge. A merge of the target head with a pull request
    /// commit merges two histories and drags in everything that landed on the
    /// other branch since they diverged; only the one commit's diff is wanted.
    /// </summary>
    [Fact]
    public async Task Cherry_picking_applies_the_commit_onto_the_target_and_moves_the_branch()
    {
        using var app = new TestApp(CherryPickStub());
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "feature/x" },
            },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(MergeSha, result.GetProperty("commitId").GetString());

        // Onto the target branch, picking exactly the one commit.
        var picked = app.Stub.Requests.First(r => r.PathAndQuery.Contains("/cherryPicks")).Body!;

        Assert.Contains($"refs/heads/{Target}", picked);
        Assert.Contains(SourceSha, picked);
        Assert.Contains("generatedRefName", picked);

        // The target head is not a parent of anything here - that was the merge.
        Assert.DoesNotContain(TargetSha, picked);
        Assert.DoesNotContain(app.Stub.Requests, r => r.PathAndQuery.Contains("/merges"));

        // The branch only moves when its own ref is pointed at the new commit.
        var refUpdates = app.Stub.Requests
            .Where(r => r.Method == "POST" && r.PathAndQuery.Contains("/refs"))
            .Select(r => r.Body!.Replace(" ", string.Empty))
            .ToList();

        Assert.Contains(refUpdates, body =>
            body.Contains($"\"oldObjectId\":\"{TargetSha}\"")
            && body.Contains($"\"newObjectId\":\"{MergeSha}\""));
    }

    /// <summary>
    /// The generated branch is an implementation detail, and must not be left
    /// behind cluttering the repository.
    /// </summary>
    [Fact]
    public async Task The_generated_branch_is_removed_afterwards()
    {
        using var app = new TestApp(CherryPickStub());
        using var client = WithDevOpsToken(app);

        await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var deletes = app.Stub.Requests
            .Where(r => r.Method == "POST" && r.PathAndQuery.Contains("/refs"))
            .Select(r => r.Body!.Replace(" ", string.Empty))
            .ToList();

        Assert.Contains(deletes, body =>
            body.Contains("release-tool/cherry-pick/")
            && body.Contains($"\"newObjectId\":\"{new string('0', 40)}\""));
    }

    /// <summary>Even a failed cherry-pick may have created the branch first.</summary>
    [Fact]
    public async Task The_generated_branch_is_removed_after_a_failure_too()
    {
        using var app = new TestApp(CherryPickStub("failed"));
        using var client = WithDevOpsToken(app);

        await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        Assert.Contains(app.Stub.Requests, r =>
            r.Method == "POST" && r.PathAndQuery.Contains("/refs")
            && r.Body!.Contains("release-tool/cherry-pick/"));
    }

    /// <summary>
    /// Regression: the success check looked for "succeeded", which is not a
    /// GitAsyncOperationStatus value at all - so every good operation was
    /// reported as a failure and the branch was never moved.
    /// </summary>
    [Fact]
    public async Task A_completed_cherry_pick_is_a_success_not_a_failure()
    {
        using var app = new TestApp(CherryPickStub("completed"));
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(MergeSha, result.GetProperty("commitId").GetString());
    }

    /// <summary>
    /// The reason Azure DevOps gives is the whole value of the message. Without
    /// it the user gets "Merge did not succeed (failed)" and no way forward.
    /// </summary>
    [Fact]
    public async Task A_failed_cherry_pick_reports_the_reason_azure_devops_gave()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Target}}", "objectId": "{{TargetSha}}" } ] }""")
            .OnPost("/cherryPicks?api-version=7.1-preview.1", """
            { "cherryPickId": 3, "status": "failed",
              "detailedStatus": { "failureMessage": "Conflict in src/Shared.cs" } }
            """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];
        var message = result.GetProperty("message").GetString()!;

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Conflict in src/Shared.cs", message);

        // And which commit it was trying to bring in.
        Assert.Contains(SourceSha[..8], message);
    }

    /// <summary>
    /// A failed merge has to leave enough behind to reproduce it by hand: both
    /// parents, the branch, the repository and what Azure DevOps sent back.
    /// </summary>
    [Fact]
    public async Task A_failed_cherry_pick_is_logged_with_everything_needed_to_reproduce_it()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Target}}", "objectId": "{{TargetSha}}" } ] }""")
            .OnPost("/cherryPicks?api-version=7.1-preview.1", """
            { "cherryPickId": 3, "status": "failed",
              "detailedStatus": { "failureMessage": "Operation resulted in a conflict." } }
            """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var logs = app.Logs.RenderAll();

        Assert.Contains("Operation resulted in a conflict.", logs);
        Assert.Contains(TargetSha, logs);
        Assert.Contains(SourceSha, logs);
        Assert.Contains(Target, logs);
        Assert.Contains("sample-web", logs);
        Assert.Contains("4821", logs);
    }

    /// <summary>A failure with no explanation still has to point somewhere.</summary>
    [Fact]
    public async Task A_failed_cherry_pick_with_no_reason_says_where_to_look()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Target}}", "objectId": "{{TargetSha}}" } ] }""")
            .OnPost("/cherryPicks?api-version=7.1-preview.1", """{ "cherryPickId": 3, "status": "failed" }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var message = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0].GetProperty("message").GetString()!;

        Assert.Contains("conflict", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(failed)", message);
    }

    /// <summary>
    /// A queued cherry-pick is polled at its own address. The id belongs in the
    /// path; hanging it off the collection URL as a query parameter is not the
    /// route.
    /// </summary>
    [Fact]
    public async Task A_queued_cherry_pick_is_polled_at_the_operation_url()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Target}}", "objectId": "{{TargetSha}}" } ] }""")
            // One entry per pull request in the tests below: the branch name
            // carries the PR id, and FindRefAsync matches the exact name.
            .OnGet("filter=heads%2Frelease-tool", $$"""
            { "value": [
                { "name": "refs/heads/release-tool/cherry-pick/4821-11111111", "objectId": "{{MergeSha}}" },
                { "name": "refs/heads/release-tool/cherry-pick/4822-11111111", "objectId": "{{MergeSha}}" } ] }
            """)
            .OnPost("/cherryPicks?api-version=7.1-preview.1", """{ "cherryPickId": 3, "status": "queued" }""")
            .OnGet("/cherryPicks/3", """{ "cherryPickId": 3, "status": "completed" }""")
            .OnPost("/refs?api-version=7.1",
                """{ "value": [ { "success": true, "updateStatus": "succeeded" } ] }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains(app.Stub.Requests, r => r.Method == "GET" && r.PathAndQuery.Contains("/cherryPicks/3"));
    }

    /// <summary>
    /// "Cherry-pick all" is one at a time until the last one lands. Every pick
    /// moves the target, so anything after a conflict would be built on a head
    /// that is no longer there - the run stops and says where.
    /// </summary>
    [Fact]
    public async Task A_conflict_stops_the_run_and_leaves_the_rest_untried()
    {
        using var app = new TestApp(CherryPickStub("failed"));
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
                new { pullRequestId = 4822, ticketKey = "PROJECT-1815", sourceCommit = SourceSha, sourceBranch = "b" },
            },
        });

        var results = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results").EnumerateArray().ToList();

        Assert.Single(results);
        Assert.Equal(4821, results[0].GetProperty("pullRequestId").GetInt32());
        Assert.False(results[0].GetProperty("success").GetBoolean());

        // The second was never attempted.
        Assert.DoesNotContain(app.Stub.Requests, r => r.Body?.Contains("4822") == true);
    }

    /// <summary>
    /// The happy path for "cherry-pick all": each one lands, in order, and the
    /// branch moves once per pull request.
    /// </summary>
    [Fact]
    public async Task Cherry_picking_all_runs_through_every_pull_request_in_order()
    {
        using var app = new TestApp(CherryPickStub());
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
                new { pullRequestId = 4822, ticketKey = "PROJECT-1815", sourceCommit = MergeSha, sourceBranch = "b" },
            },
        });

        var results = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GetProperty("success").GetBoolean()));
        Assert.Equal([4821, 4822], results.Select(r => r.GetProperty("pullRequestId").GetInt32()));

        // One cherry-pick per pull request, not one batched call.
        Assert.Equal(2, app.Stub.Requests.Count(r =>
            r.Method == "POST" && r.PathAndQuery.Contains("/cherryPicks?")));
    }

    [Fact]
    public async Task Merging_into_a_branch_that_does_not_exist_says_to_create_it_first()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}", """{ "value": [] }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Create it first", result.GetProperty("message").GetString()!);
    }

    /// <summary>
    /// Re-merging something already in would otherwise create an empty commit.
    /// </summary>
    [Fact]
    public async Task A_pull_request_already_in_the_branch_is_reported_as_up_to_date()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Target}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Target}}", "objectId": "{{SourceSha}}" } ] }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = Target,
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("up to date", result.GetProperty("message").GetString()!);
        Assert.DoesNotContain(app.Stub.Requests, r => r.PathAndQuery.Contains("/cherryPicks"));
    }

    [Fact]
    public async Task Merging_without_a_target_branch_is_refused()
    {
        using var app = new TestApp();
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/merge", new
        {
            repository = Repository,
            targetBranch = "",
            pullRequests = new[]
            {
                new { pullRequestId = 4821, ticketKey = "PROJECT-1814", sourceCommit = SourceSha, sourceBranch = "a" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Stub.Requests);
    }

    // ---- Identity and commit ids -------------------------------------------

    [Fact]
    public async Task The_lookup_carries_the_source_commit_so_a_merge_needs_no_second_read()
    {
        var stub = new StubAtlassian().OnGet("pullrequests/4821", $$"""
        {
          "title": "Fix the thing",
          "status": "completed",
          "sourceRefName": "refs/heads/feature/x",
          "targetRefName": "refs/heads/release/uat",
          "lastMergeSourceCommit": { "commitId": "{{SourceSha}}" },
          "lastMergeCommit": { "commitId": "{{MergeSha}}" }
        }
        """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821) },
        });

        var pr = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0].GetProperty("pullRequests")[0];

        // Full SHAs: the UI decides how much of one to show.
        Assert.Equal(SourceSha, pr.GetProperty("sourceCommit").GetString());
        Assert.Equal(MergeSha, pr.GetProperty("mergeCommit").GetString());
        Assert.Equal("feature/x", pr.GetProperty("sourceBranch").GetString());
        Assert.Equal("your-organization", pr.GetProperty("organization").GetString());
    }

    /// <summary>
    /// A squashed pull request landed as one commit, and the Deployment tab
    /// replays that rather than the commits the squash collapsed - so the flag
    /// has to reach the client.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_lookup_reports_whether_a_pull_request_was_squashed(bool squashed)
    {
        var stub = new StubAtlassian().OnGet("pullrequests/4821", $$"""
        {
          "title": "Fix the thing",
          "status": "completed",
          "completionOptions": { "squashMerge": {{(squashed ? "true" : "false")}} },
          "lastMergeSourceCommit": { "commitId": "{{SourceSha}}" },
          "lastMergeCommit": { "commitId": "{{MergeSha}}" }
        }
        """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821) },
        });

        var pr = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0].GetProperty("pullRequests")[0];

        Assert.Equal(squashed, pr.GetProperty("squashed").GetBoolean());

        // Both commits travel either way; which one to replay is the client's call.
        Assert.Equal(SourceSha, pr.GetProperty("sourceCommit").GetString());
        Assert.Equal(MergeSha, pr.GetProperty("mergeCommit").GetString());
    }

    /// <summary>A pull request with no completion options is not squashed.</summary>
    [Fact]
    public async Task A_pull_request_without_completion_options_is_not_squashed()
    {
        var stub = new StubAtlassian().OnGet("pullrequests/4821",
            """{ "title": "Fix the thing", "status": "active" }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821) },
        });

        var pr = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0].GetProperty("pullRequests")[0];

        Assert.False(pr.GetProperty("squashed").GetBoolean());
    }

    /// <summary>
    /// The "only mine" filter matches on the sign-in name, so the lookup has to
    /// carry it - display names are not reliably the same string in the profile
    /// API and on a repository identity.
    /// </summary>
    [Fact]
    public async Task The_lookup_carries_the_author_sign_in_name()
    {
        var stub = new StubAtlassian().OnGet("pullrequests/4821", """
        {
          "title": "Fix the thing",
          "status": "completed",
          "createdBy": { "displayName": "Alex Taylor", "uniqueName": "alex.taylor@example.com" }
        }
        """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[] { PullRequest("PROJECT-1814", 4821) },
        });

        var pr = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0].GetProperty("pullRequests")[0];

        Assert.Equal("Alex Taylor", pr.GetProperty("author").GetString());
        Assert.Equal("alex.taylor@example.com", pr.GetProperty("authorEmail").GetString());
    }

    [Fact]
    public async Task The_devops_identity_endpoint_names_the_account_the_token_acts_as()
    {
        var stub = new StubAtlassian().OnGet("/_apis/profile/profiles/me",
            """{ "displayName": "Jordan Lee", "emailAddress": "jordan.lee@example.com" }""");

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var payload = SampleDocuments.Parse(await client.GetStringAsync("/api/devops/me"));

        Assert.Equal("Jordan Lee", payload.GetProperty("displayName").GetString());
        Assert.Equal("jordan.lee@example.com", payload.GetProperty("email").GetString());

        // Organisation-scoped: the un-scoped host 401s a personal access token.
        Assert.Contains(app.Stub.Requests, r => r.PathAndQuery.Contains("/your-organization/_apis/profile"));
    }
}
