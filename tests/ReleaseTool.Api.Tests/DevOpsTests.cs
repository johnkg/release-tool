using System.Net;
using System.Net.Http.Json;
using ReleaseTool.Api.DevOps;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// Capturing pull request links during resolve, and reading them back from
/// Azure DevOps for the DevOps tab.
/// </summary>
public class DevOpsTests
{
    private const string LegacyPr =
        "https://your-organization.visualstudio.com/Platform/_git/sample-web/pullrequest/4821";

    private static HttpClient WithDevOpsToken(TestApp app, string token = "pat-123")
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(DevOpsCredentials.TokenHeader, token);
        return client;
    }

    [Fact]
    public async Task Resolve_returns_the_pull_request_links_it_found()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(("PROJECT-1814", "Taylor", "acc-1", $"PR {LegacyPr}")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814" } });

        var pr = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("pullRequests")[0];

        Assert.Equal("PROJECT-1814", pr.GetProperty("ticketKey").GetString());
        Assert.Equal("your-organization", pr.GetProperty("organization").GetString());
        Assert.Equal("Platform", pr.GetProperty("project").GetString());
        Assert.Equal("sample-web", pr.GetProperty("repository").GetString());
        Assert.Equal(4821, pr.GetProperty("pullRequestId").GetInt32());
        Assert.Equal("Taylor", pr.GetProperty("commentAuthor").GetString());
    }

    /// <summary>Older tickets carry visualstudio.com links, newer ones dev.azure.com.</summary>
    [Fact]
    public async Task Both_azure_devops_url_forms_are_recognised()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(
                ("PROJECT-1", "Dev", "acc-1", $"PR {LegacyPr}"),
                ("PROJECT-2", "Dev", "acc-1",
                    "PR https://dev.azure.com/your-organization/Platform/_git/sample-db/pullrequest/99")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1", "PROJECT-2" } });

        var prs = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("pullRequests").EnumerateArray().ToList();

        Assert.Equal(2, prs.Count);
        Assert.All(prs, pr => Assert.Equal("your-organization", pr.GetProperty("organization").GetString()));
        Assert.Contains(prs, pr => pr.GetProperty("repository").GetString() == "sample-db");
    }

    /// <summary>
    /// A ticket fixed under another ticket has no PR of its own - the comment is
    /// the only record of where the work went.
    /// </summary>
    [Fact]
    public async Task Tickets_without_a_pull_request_are_listed_with_their_comment()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(
                ("PROJECT-1814", "Taylor", "acc-1", $"PR {LegacyPr}"),
                ("PROJECT-1835", "Taylor", "acc-1", "Fixed on PROJECT-1834")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814", "PROJECT-1835" } });

        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());
        var notes = payload.GetProperty("fixedByNotes").EnumerateArray().ToList();

        // Only the ticket without a PR appears.
        var note = Assert.Single(notes);
        Assert.Equal("PROJECT-1835", note.GetProperty("ticketKey").GetString());
        Assert.Contains("Fixed on PROJECT-1834", note.GetProperty("comment").GetString());
        Assert.Equal("Taylor", note.GetProperty("author").GetString());
    }

    [Fact]
    public async Task The_same_pull_request_pasted_twice_is_listed_once()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(("PROJECT-1814", "Taylor", "acc-1", $"PR {LegacyPr} and again {LegacyPr}")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814" } });

        Assert.Single(SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("pullRequests").EnumerateArray());
    }

    [Fact]
    public async Task The_devops_endpoint_needs_its_own_token_not_the_atlassian_one()
    {
        using var app = new TestApp();
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests",
            new { pullRequests = Array.Empty<object>() });

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(DevOpsCredentials.TokenHeader, body);
    }

    [Fact]
    public async Task Pull_requests_are_grouped_by_repo_and_ordered_by_when_they_landed()
    {
        var stub = new StubAtlassian()
            .OnGet("/sample-web/pullrequests/2", Pr(2, "Later web change", "2026-08-05T10:00:00Z"))
            .OnGet("/sample-web/pullrequests/1", Pr(1, "Earlier web change", "2026-08-01T10:00:00Z"))
            .OnGet("/sample-db/pullrequests/3", Pr(3, "Db change", "2026-08-03T10:00:00Z", "sample-db"));

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[]
            {
                Reference("PROJECT-1", "sample-web", 2),
                Reference("PROJECT-2", "sample-web", 1),
                Reference("PROJECT-3", "sample-db", 3)
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var repositories = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories").EnumerateArray().ToList();

        // Repositories in name order.
        Assert.Equal(["sample-db", "sample-web"], repositories.Select(r => r.GetProperty("repository").GetString()));

        // Within a repo, oldest completion first.
        var web = repositories[1].GetProperty("pullRequests").EnumerateArray().ToList();
        Assert.Equal([1, 2], web.Select(pr => pr.GetProperty("pullRequestId").GetInt32()));
        Assert.Equal("Earlier web change", web[0].GetProperty("title").GetString());
    }

    /// <summary>An unmerged PR has no completion date, so it sorts after the merged ones.</summary>
    [Fact]
    public async Task Open_pull_requests_come_after_the_merged_ones()
    {
        var stub = new StubAtlassian()
            .OnGet("/sample-web/pullrequests/1", Pr(1, "Merged", "2026-08-01T10:00:00Z"))
            .OnGet("/sample-web/pullrequests/2", """
            { "pullRequestId": 2, "title": "Still open", "status": "active",
              "creationDate": "2026-08-02T10:00:00Z",
              "createdBy": { "displayName": "Dev" },
              "repository": { "name": "sample-web" },
              "targetRefName": "refs/heads/main" }
            """);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[] { Reference("PROJECT-2", "sample-web", 2), Reference("PROJECT-1", "sample-web", 1) }
        });

        var web = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0].GetProperty("pullRequests").EnumerateArray().ToList();

        Assert.Equal([1, 2], web.Select(pr => pr.GetProperty("pullRequestId").GetInt32()));
        Assert.Equal("refs/heads/main".Replace("refs/heads/", ""), web[0].GetProperty("targetBranch").GetString());
    }

    /// <summary>
    /// One inaccessible repository should not lose the rest of the release.
    /// </summary>
    [Fact]
    public async Task A_pull_request_that_cannot_be_read_is_reported_without_losing_the_others()
    {
        var stub = new StubAtlassian()
            .OnGet("/sample-web/pullrequests/1", Pr(1, "Fine", "2026-08-01T10:00:00Z"))
            .OnGet("/secret-repo/pullrequests/9", "{}", HttpStatusCode.Forbidden);

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests", new
        {
            pullRequests = new[] { Reference("PROJECT-1", "sample-web", 1), Reference("PROJECT-9", "secret-repo", 9) }
        });

        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Single(payload.GetProperty("repositories").EnumerateArray());

        var failure = Assert.Single(payload.GetProperty("failures").EnumerateArray());
        Assert.Equal("PROJECT-9", failure.GetProperty("ticketKey").GetString());
        Assert.Contains("permission", failure.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_pat_is_sent_as_basic_auth_with_an_empty_username()
    {
        var stub = new StubAtlassian().OnGet("/sample-web/pullrequests/1", Pr(1, "Fine", "2026-08-01T10:00:00Z"));

        using var app = new TestApp(stub);
        using var client = WithDevOpsToken(app, "my-pat");

        await client.PostAsJsonAsync("/api/devops/pull-requests",
            new { pullRequests = new[] { Reference("PROJECT-1", "sample-web", 1) } });

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":my-pat"));

        Assert.Equal(expected, stub.Requests.Single().AuthParameter);
    }

    private static object Reference(string ticketKey, string repository, int id) => new
    {
        ticketKey,
        url = $"https://your-organization.visualstudio.com/Platform/_git/{repository}/pullrequest/{id}",
        organization = "your-organization",
        project = "Platform",
        repository,
        pullRequestId = id,
        commentAuthor = "Dev",
        commentedAt = "2026-08-01T10:00:00+08:00"
    };

    private static string Pr(int id, string title, string closedDate, string repository = "sample-web") => $$"""
    { "pullRequestId": {{id}}, "title": "{{title}}", "status": "completed",
      "creationDate": "2026-07-01T10:00:00Z", "closedDate": "{{closedDate}}",
      "createdBy": { "displayName": "Dev" },
      "repository": { "name": "{{repository}}" },
      "targetRefName": "refs/heads/main" }
    """;
}
