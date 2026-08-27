using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// The derivation rules and the write-back, end to end through the API.
/// </summary>
public class ResolveAndApplyTests
{
    private const string PullRequestComment =
        "PR https://your-organization.visualstudio.com/Platform/_git/sample-web/pullrequest/4821";

    [Fact]
    public async Task A_pull_request_comment_makes_its_author_the_developer()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(("PROJECT-1814", "Alex Taylor", "0123456789abcdef01234567", PullRequestComment)));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814" } });

        var assignment = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("assignments")[0];

        Assert.Equal("Alex Taylor", assignment.GetProperty("developerName").GetString());
        Assert.Equal("0123456789abcdef01234567", assignment.GetProperty("accountId").GetString());
        Assert.Equal("PullRequest", assignment.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_fixed_on_comment_falls_to_the_secondary_rule()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(("PROJECT-1835", "Alex Taylor", "acc-alex", "Fixed on PROJECT-1834")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1835" } });

        var assignment = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("assignments")[0];

        Assert.Equal("Alex Taylor", assignment.GetProperty("developerName").GetString());
        Assert.Equal("Reference", assignment.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_ticket_with_no_useful_comment_defaults_and_is_marked_as_defaulted()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql",
                SampleDocuments.JqlResponse(("PROJECT-1816", "Someone", "acc-x", "Deployed to UAT")))
            .OnGet("rest/api/3/user/search",
                """[ { "accountId": "fedcba9876543210fedcba98", "displayName": "Jordan Lee" } ]""");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1816" } });

        var assignment = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("assignments")[0];

        Assert.Equal("Jordan Lee", assignment.GetProperty("developerName").GetString());
        Assert.Equal("fedcba9876543210fedcba98", assignment.GetProperty("accountId").GetString());
        Assert.Equal("Defaulted", assignment.GetProperty("source").GetString());
    }

    [Fact]
    public async Task All_tickets_are_fetched_in_one_jql_call()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse(
                ("PROJECT-1814", "Taylor", "a1", PullRequestComment),
                ("PROJECT-1835", "Taylor", "a1", PullRequestComment),
                ("PROJECT-1853", "Lee", "a2", PullRequestComment)));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814", "PROJECT-1835", "PROJECT-1853" } });

        var jqlCalls = stub.Requests.Count(r => r.PathAndQuery.Contains("search/jql"));

        Assert.Equal(1, jqlCalls);
        Assert.Contains("PROJECT-1814", stub.Requests[0].Body);
        Assert.Contains("PROJECT-1853", stub.Requests[0].Body);
    }

    /// <summary>
    /// Search truncates long threads, so a PR link past the cut-off would be
    /// missed and the ticket would wrongly default.
    /// </summary>
    [Fact]
    public async Task A_truncated_comment_thread_is_refetched_in_full()
    {
        var truncated = new JsonObject
        {
            ["issues"] = new JsonArray(new JsonObject
            {
                ["key"] = "PROJECT-1814",
                ["fields"] = new JsonObject
                {
                    ["comment"] = new JsonObject
                    {
                        ["total"] = 40,
                        ["comments"] = new JsonArray(new JsonObject
                        {
                            ["created"] = "2026-08-01T10:00:00.000+0800",
                            ["author"] = new JsonObject { ["displayName"] = "Early Commenter", ["accountId"] = "acc-early" },
                            ["body"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray() }
                        })
                    }
                }
            })
        }.ToJsonString();

        var full = new JsonObject
        {
            ["total"] = 2,
            ["comments"] = new JsonArray(
                new JsonObject
                {
                    ["created"] = "2026-08-01T10:00:00.000+0800",
                    ["author"] = new JsonObject { ["displayName"] = "Early Commenter", ["accountId"] = "acc-early" },
                    ["body"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray() }
                },
                new JsonObject
                {
                    ["created"] = "2026-08-05T10:00:00.000+0800",
                    ["author"] = new JsonObject { ["displayName"] = "Real Developer", ["accountId"] = "acc-real" },
                    ["body"] = new JsonObject
                    {
                        ["type"] = "doc",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "paragraph",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = PullRequestComment })
                        })
                    }
                })
        }.ToJsonString();

        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", truncated)
            .OnGet("rest/api/3/issue/PROJECT-1814/comment", full);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814" } });

        var assignment = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("assignments")[0];

        Assert.Equal("Real Developer", assignment.GetProperty("developerName").GetString());
        Assert.Equal("PullRequest", assignment.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Apply_writes_a_mention_at_version_plus_one_and_leaves_the_rest_alone()
    {
        var stub = new StubAtlassian()
            .OnGet("wiki/api/v2/pages/PAGE_ID", SampleDocuments.PageEnvelope(42))
            .OnPut("wiki/api/v2/pages/PAGE_ID", """{ "id": "PAGE_ID", "version": { "number": 43 } }""");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Alex Taylor", accountId = "0123456789abcdef01234567", source = "PullRequest" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(43, payload.GetProperty("newVersion").GetInt32());
        Assert.Equal(1, payload.GetProperty("cellsUpdated").GetInt32());

        var put = JsonNode.Parse(stub.LastOf(HttpMethod.Put)!.Body!)!;

        Assert.Equal(43, put["version"]!["number"]!.GetValue<int>());
        Assert.Equal("atlas_doc_format", put["body"]!["representation"]!.GetValue<string>());

        // The ADF must go back as a serialised string, mirroring how it arrived.
        var written = JsonNode.Parse(put["body"]!["value"]!.GetValue<string>())!;
        var approvals = written["content"]!.AsArray()
            .Single(n => n?["attrs"]?["localId"]?.GetValue<string>() == "approvals-table")!;

        var mention = approvals["content"]![1]!["content"]![1]!["content"]![0]!["content"]![0]!;

        Assert.Equal("mention", mention["type"]!.GetValue<string>());
        Assert.Equal("0123456789abcdef01234567", mention["attrs"]!["id"]!.GetValue<string>());
        Assert.Equal("@Alex Taylor", mention["attrs"]!["text"]!.GetValue<string>());

        // Untouched structure survives the round trip.
        var raw = put["body"]!["value"]!.GetValue<string>();
        Assert.Contains("decoy-table", raw);
        Assert.Contains("OTHER_PROJECT-9001", raw);
        Assert.Contains("\"colwidth\":[220]", raw);
        Assert.Contains("#f4f5f7", raw);
        Assert.Contains("Someone Else", raw);
    }

    [Fact]
    public async Task Apply_refuses_when_the_page_moved_since_it_was_previewed()
    {
        var stub = new StubAtlassian().OnGet("wiki/api/v2/pages/PAGE_ID", SampleDocuments.PageEnvelope(45));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Dev", accountId = "acc-1", source = "PullRequest" }
            }
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(stub.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task Apply_refuses_assignments_without_an_account_id()
    {
        var stub = new StubAtlassian().OnGet("wiki/api/v2/pages/PAGE_ID", SampleDocuments.PageEnvelope(42));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Nobody", accountId = (string?)null, source = "Defaulted" }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PROJECT-1814", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(stub.Requests, r => r.Method == "PUT");
    }

    /// <summary>
    /// A concurrent edit must be merged, not overwritten: the retry refetches and
    /// reapplies rather than forcing the stale document back.
    /// </summary>
    [Fact]
    public async Task Apply_retries_a_conflict_by_refetching()
    {
        var stub = new ConflictOnceStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Dev One", accountId = "acc-1", source = "PullRequest" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count(r => r.Method == "PUT"));

        // The successful write is built on the refetched version, not the stale one.
        var put = JsonNode.Parse(stub.LastOf(HttpMethod.Put)!.Body!)!;
        Assert.Equal(51, put["version"]!["number"]!.GetValue<int>());
    }

    /// <summary>Fails the first PUT with 409, then serves a newer page.</summary>
    private sealed class ConflictOnceStub : StubAtlassian
    {
        private int _puts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await base.SendAsync(request, ct);

            if (request.Method == HttpMethod.Get)
            {
                var version = Requests.Count(r => r.Method == "PUT") == 0 ? 42 : 50;
                return Json(HttpStatusCode.OK, SampleDocuments.PageEnvelope(version));
            }

            if (request.Method == HttpMethod.Put && ++_puts == 1)
            {
                return Json(HttpStatusCode.Conflict, """{ "message": "version conflict" }""");
            }

            return Json(HttpStatusCode.OK, """{ "id": "PAGE_ID", "version": { "number": 51 } }""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}
