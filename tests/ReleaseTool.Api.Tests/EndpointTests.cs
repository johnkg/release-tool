using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// The checks that were previously run by hand with curl.
/// </summary>
public class EndpointTests
{
    [Fact]
    public async Task Health_is_open_and_reports_ok()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_credential_headers_are_rejected_with_the_header_names()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/auth/verify", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AtlassianCredentials.EmailHeader, body);
        Assert.Contains(AtlassianCredentials.TokenHeader, body);

        // Rejected before any outbound call is made.
        Assert.Empty(app.Stub.Requests);
    }

    [Fact]
    public async Task Verify_returns_the_connected_user()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself", """
        { "accountId": "0123456789abcdef01234567", "displayName": "Alex Taylor", "emailAddress": "j@example.com" }
        """);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/auth/verify", null);
        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Alex Taylor", payload.GetProperty("displayName").GetString());
        Assert.Equal("0123456789abcdef01234567", payload.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task Credentials_are_sent_as_basic_auth_built_from_the_caller_headers()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself", """{ "accountId": "a", "displayName": "b" }""");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(
            TestApp.ExpectedBasic("tester@example.com", TestApp.TestToken),
            stub.Requests.Single().AuthParameter);
    }

    [Fact]
    public async Task A_rejected_token_surfaces_as_401_not_500()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself",
            """{ "message": "Client must be authenticated to access this resource." }""",
            HttpStatusCode.Unauthorized);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("rejected the token", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Confluence answers an unauthenticated request with 404, so the message has
    /// to point at both possible causes.
    /// </summary>
    [Fact]
    public async Task A_missing_page_explains_drafts_and_tokens()
    {
        var stub = new StubAtlassian().OnGet("wiki/api/v2/pages/", """{ "errors": [] }""", HttpStatusCode.NotFound);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/approvals/PAGE_ID");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("draft", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rate_limiting_is_passed_through_as_429()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself", "{}", HttpStatusCode.TooManyRequests);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Page_lookup_resolves_the_space_key_to_an_id_first()
    {
        var stub = new StubAtlassian()
            .OnGet("wiki/api/v2/spaces?keys=", """{ "results": [ { "id": "9000001", "key": "~sandbox-space" } ] }""")
            .OnGet("wiki/api/v2/spaces/9000001/pages",
                """{ "results": [ { "id": "PAGE_ID", "title": "Copy of Release 1.2.3 - 13 August 2026", "version": { "number": 42 } } ] }""");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/confluence/page?title=Copy%20of%20Release%201.2.3%20-%2013%20August%202026");
        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PAGE_ID", payload.GetProperty("pageId").GetString());
        Assert.Equal(42, payload.GetProperty("version").GetInt32());

        // The numeric id, never the key, is what reaches the pages endpoint.
        Assert.Contains(stub.Requests, r => r.PathAndQuery.Contains("/spaces/9000001/pages"));
    }

    [Fact]
    public async Task An_unknown_space_key_is_reported_as_not_found()
    {
        var stub = new StubAtlassian().OnGet("wiki/api/v2/spaces?keys=", """{ "results": [] }""");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/confluence/page?spaceKey=NOPE&title=Anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approvals_returns_afg_rows_only()
    {
        var stub = new StubAtlassian().OnGet("wiki/api/v2/pages/PAGE_ID", SampleDocuments.PageEnvelope());

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/approvals/PAGE_ID");
        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(42, payload.GetProperty("version").GetInt32());

        var rows = payload.GetProperty("rows").EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("PROJECT-1814", rows[0].GetProperty("ticketKey").GetString());
        Assert.Equal("PROJECT-1835", rows[1].GetProperty("ticketKey").GetString());
        Assert.DoesNotContain(rows, r => r.GetProperty("ticketKey").GetString()!.StartsWith("OTHER_PROJECT"));

        // Column names must cross the wire as names, not enum ordinals, or the
        // UI has to hardcode the numbering.
        Assert.Equal("Someone Else", rows[1].GetProperty("values").GetProperty("DeveloperAssigned").GetString());

        var available = payload.GetProperty("availableColumns").EnumerateArray()
            .Select(c => c.GetString()).ToList();

        Assert.Contains("RequestedBy", available);
        Assert.Contains("PrApprovedStatus", available);
    }

    [Fact]
    public async Task Page_list_is_newest_first_for_the_picker()
    {
        var stub = new StubAtlassian()
            .OnGet("wiki/api/v2/spaces?keys=", """{ "results": [ { "id": "9000001" } ] }""")
            .OnGet("wiki/api/v2/spaces/9000001/pages", """
            { "results": [
                { "id": "3", "title": "Newest release", "createdAt": "2026-08-11T09:00:00.000Z" },
                { "id": "2", "title": "Older release", "createdAt": "2026-07-01T09:00:00.000Z" } ] }
            """);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/confluence/pages");
        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var pages = payload.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal("Newest release", pages[0].GetProperty("title").GetString());

        // Confluence does the ordering, so the request must ask for it.
        Assert.Contains(stub.Requests, r => r.PathAndQuery.Contains("sort=-created-date"));
    }

    [Fact]
    public async Task A_page_without_an_approvals_table_is_reported_clearly()
    {
        var envelope = SampleDocuments.PageEnvelope("1", "Some page", 1,
            """{ "type": "doc", "content": [ { "type": "heading", "content": [ { "type": "text", "text": "I. Scope" } ] } ] }""");

        var stub = new StubAtlassian().OnGet("wiki/api/v2/pages/1", envelope);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/approvals/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Approvals", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// With the SPA served from the same origin, an unmatched /api path must not
    /// fall through to index.html and answer a typo with 200 and a page of HTML.
    /// </summary>
    [Fact]
    public async Task An_unmatched_api_path_404s_instead_of_serving_the_spa()
    {
        using var app = new TestApp();
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/no-such-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<!doctype html", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolving_only_assd_tickets_makes_no_jira_call()
    {
        using var app = new TestApp();
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/approvals/PAGE_ID/resolve",
            new { ticketKeys = new[] { "OTHER_PROJECT-9001", "OTHER_PROJECT-9002" } });

        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(payload.GetProperty("assignments").EnumerateArray());
        Assert.Empty(app.Stub.Requests);
    }
}
