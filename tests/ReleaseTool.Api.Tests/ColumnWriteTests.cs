using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// Requested By, PR Approved By and the two status columns, end to end.
/// </summary>
public class ColumnWriteTests
{
    private const string PullRequestComment =
        "PR https://your-organization.visualstudio.com/Platform/_git/sample-web/pullrequest/4821";

    private const string KoGaw = "0123456789abcdef01234567";

    private static StubAtlassian PageStub() => new StubAtlassian()
        .OnGet("wiki/api/v2/pages/PAGE_ID", SampleDocuments.PageEnvelope(42))
        .OnPut("wiki/api/v2/pages/PAGE_ID", """{ "id": "PAGE_ID", "version": { "number": 43 } }""");

    private static JsonNode WrittenTable(StubAtlassian stub)
    {
        var put = JsonNode.Parse(stub.LastOf(HttpMethod.Put)!.Body!)!;
        var document = JsonNode.Parse(put["body"]!["value"]!.GetValue<string>())!;

        return document["content"]!.AsArray()
            .Single(n => n?["attrs"]?["localId"]?.GetValue<string>() == "approvals-table")!;
    }

    /// <summary>Row 1 of the table is the PROJECT-1814 row; row 0 is the header.</summary>
    private static JsonNode Cell(StubAtlassian stub, int row, int column) =>
        WrittenTable(stub)["content"]![row]!["content"]![column]!;

    [Fact]
    public async Task Resolve_returns_the_jira_reporter_alongside_the_developer()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.JqlResponse("Business Analyst", "acc-ba",
                ("PROJECT-1814", "Alex Taylor", KoGaw, PullRequestComment)));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "PROJECT-1814" } });

        var assignment = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("assignments")[0];

        Assert.Equal("Business Analyst", assignment.GetProperty("reporterName").GetString());
        Assert.Equal("acc-ba", assignment.GetProperty("reporterAccountId").GetString());
    }

    [Fact]
    public async Task Apply_writes_the_reporter_into_requested_by()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new
                {
                    ticketKey = "PROJECT-1814",
                    developerName = "Alex Taylor",
                    accountId = KoGaw,
                    source = "PullRequest",
                    reporterName = "Business Analyst",
                    reporterAccountId = "acc-ba"
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var mention = Cell(stub, 1, 2)["content"]![0]!["content"]![0]!;

        Assert.Equal("mention", mention["type"]!.GetValue<string>());
        Assert.Equal("acc-ba", mention["attrs"]!["id"]!.GetValue<string>());
        Assert.Equal("@Business Analyst", mention["attrs"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_ticket_with_no_reporter_leaves_requested_by_alone()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new
                {
                    ticketKey = "PROJECT-1814",
                    developerName = "Dev",
                    accountId = "acc-dev",
                    source = "PullRequest",
                    reporterName = (string?)null,
                    reporterAccountId = (string?)null
                }
            }
        });

        Assert.Empty(Cell(stub, 1, 2)["content"]![0]!["content"]!.AsArray());
    }

    /// <summary>
    /// The approver applies only to the tickets the caller listed, so the row
    /// belonging to someone else keeps its empty approval columns.
    /// </summary>
    [Fact]
    public async Task Pr_approval_touches_only_the_listed_tickets()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Taylor", accountId = KoGaw, source = "PullRequest" },
                new { ticketKey = "PROJECT-1835", developerName = "Someone Else", accountId = "acc-other", source = "PullRequest" }
            },
            prApproval = new
            {
                displayName = "Alex Taylor",
                accountId = KoGaw,
                ticketKeys = new[] { "PROJECT-1814" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // PROJECT-1814 gets the approver as a mention.
        var approver = Cell(stub, 1, 3)["content"]![0]!["content"]![0]!;
        Assert.Equal("mention", approver["type"]!.GetValue<string>());
        Assert.Equal(KoGaw, approver["attrs"]!["id"]!.GetValue<string>());

        // PROJECT-1835 was not listed, so its approver cell stays empty.
        Assert.Empty(Cell(stub, 3, 3)["content"]![0]!["content"]!.AsArray());
    }

    [Fact]
    public async Task Recording_an_approver_also_sets_approved_and_merged()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Taylor", accountId = KoGaw, source = "PullRequest" }
            },
            prApproval = new { displayName = "Taylor", accountId = KoGaw, ticketKeys = new[] { "PROJECT-1814" } }
        });

        // Both columns list their allowed values as lozenges in the header, so
        // both get a lozenge in the page's own wording and colour.
        var status = Cell(stub, 1, 4)["content"]![0]!["content"]![0]!;
        Assert.Equal("status", status["type"]!.GetValue<string>());
        Assert.Equal("APPROVED", status["attrs"]!["text"]!.GetValue<string>());
        Assert.Equal("green", status["attrs"]!["color"]!.GetValue<string>());

        var merged = Cell(stub, 1, 5)["content"]![0]!["content"]![0]!;
        Assert.Equal("status", merged["type"]!.GetValue<string>());
        Assert.Equal("MERGED", merged["attrs"]!["text"]!.GetValue<string>());
        Assert.Equal("green", merged["attrs"]!["color"]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_can_skip_the_columns_it_was_not_asked_to_write()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            writeDeveloper = false,
            writeRequestedBy = true,
            assignments = new[]
            {
                new
                {
                    ticketKey = "PROJECT-1814",
                    developerName = "Taylor",
                    accountId = (string?)null,
                    source = "Defaulted",
                    reporterName = "Business Analyst",
                    reporterAccountId = "acc-ba"
                }
            }
        });

        // Developer skipped, so no account id was needed and the cell is untouched.
        Assert.Empty(Cell(stub, 1, 1)["content"]![0]!["content"]!.AsArray());

        var mention = Cell(stub, 1, 2)["content"]![0]!["content"]![0]!;
        Assert.Equal("acc-ba", mention["attrs"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task User_search_returns_matches_for_the_approver_picker()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/user/search", """
        [ { "accountId": "acc-roy", "displayName": "Jordan Lee",
            "emailAddress": "jordan.lee@example.com", "active": true, "accountType": "atlassian" } ]
        """);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/jira/users?query=roy");
        var users = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("users").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Jordan Lee", users[0].GetProperty("displayName").GetString());
        Assert.Equal("acc-roy", users[0].GetProperty("accountId").GetString());
        Assert.Equal("jordan.lee@example.com", users[0].GetProperty("email").GetString());
    }

    /// <summary>
    /// Mentioning a deactivated account or an app produces a dead link on the
    /// page, so neither is offered.
    /// </summary>
    [Fact]
    public async Task User_search_hides_inactive_and_app_accounts()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/user/search", """
        [ { "accountId": "acc-gone", "displayName": "Former Employee", "active": false, "accountType": "atlassian" },
          { "accountId": "acc-bot", "displayName": "Automation for Jira", "active": true, "accountType": "app" },
          { "accountId": "acc-real", "displayName": "Real Person", "active": true, "accountType": "atlassian" } ]
        """);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/jira/users?query=per");
        var users = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("displayName").GetString()).ToList();

        Assert.Equal(["Real Person"], users);
    }

    [Fact]
    public async Task User_search_ignores_a_query_too_short_to_be_useful()
    {
        using var app = new TestApp();
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/jira/users?query=r");
        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Empty(payload.GetProperty("users").EnumerateArray());
        Assert.Empty(app.Stub.Requests);
    }

    /// <summary>The approver is whoever was picked, not the connected user.</summary>
    [Fact]
    public async Task The_chosen_approver_is_tagged_not_the_connected_user()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/approvals/PAGE_ID/apply", new
        {
            expectedVersion = 42,
            assignments = new[]
            {
                new { ticketKey = "PROJECT-1814", developerName = "Taylor", accountId = KoGaw, source = "PullRequest" }
            },
            prApproval = new
            {
                displayName = "Jordan Lee",
                accountId = "fedcba9876543210fedcba98",
                ticketKeys = new[] { "PROJECT-1814" }
            }
        });

        var approver = Cell(stub, 1, 3)["content"]![0]!["content"]![0]!;

        Assert.Equal("fedcba9876543210fedcba98", approver["attrs"]!["id"]!.GetValue<string>());
        Assert.Equal("@Jordan Lee", approver["attrs"]!["text"]!.GetValue<string>());

        // The developer column still holds the developer, not the approver.
        Assert.Equal(KoGaw, Cell(stub, 1, 1)["content"]![0]!["content"]![0]!["attrs"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Clear_empties_only_the_chosen_columns()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/clear", new
        {
            expectedVersion = 42,
            columns = new[] { "DeveloperAssigned", "PrApprovedStatus" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // PROJECT-1835 had both a developer and a status lozenge; both are now gone.
        Assert.Empty(Cell(stub, 3, 1)["content"]![0]!["content"]!.AsArray());
        Assert.Empty(Cell(stub, 3, 4)["content"]![0]!["content"]!.AsArray());

        // The OTHER_PROJECT row and the ticket column are untouched.
        var raw = JsonNode.Parse(stub.LastOf(HttpMethod.Put)!.Body!)!["body"]!["value"]!.GetValue<string>();
        Assert.Contains("OTHER_PROJECT-9001", raw);
        Assert.Contains("PROJECT-1835", raw);
    }

    [Fact]
    public async Task Clear_with_no_columns_is_rejected_before_any_write()
    {
        var stub = PageStub();

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/clear",
            new { expectedVersion = 42, columns = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(stub.Requests, r => r.Method == "PUT");
    }
}
