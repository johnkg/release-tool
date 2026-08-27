using System.Net;
using System.Net.Http.Json;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// Moving release tickets between the two workflow statuses, end to end through
/// the API. Jira has no "set the status" call, so the interesting part is which
/// transition gets chosen - and what happens when there is not one.
/// </summary>
public class StatusTransitionTests
{
    private const string Deployed = "YOUR_DEPLOYED_STATUS";
    private const string Ready = "YOUR_READY_STATUS";
    private const string DoneResolution = """[{"id":"10000","name":"Done"}]""";

    private static bool TransitionPosted(TestApp app, string ticketKey) =>
        app.Stub.Requests.Any(r =>
            r.Method == "POST" && r.PathAndQuery.Contains($"/issue/{ticketKey}/transitions"));

    private static RecordedRequest? IssueEdit(TestApp app, string ticketKey) =>
        app.Stub.Requests.LastOrDefault(r =>
            r.Method == "PUT" && r.PathAndQuery.EndsWith($"/issue/{ticketKey}"));

    [Fact]
    public async Task The_transition_landing_in_the_target_status_is_the_one_posted()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT")))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionsResponse(
                ("11", "Reject", "Rejected"),
                ("31", "Deploy", Deployed),
                ("41", "Close", "Done")))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "")
            .OnGet("rest/api/3/resolution", DoneResolution)
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1814", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("unchanged").GetBoolean());
        Assert.Equal("In UAT", result.GetProperty("fromStatus").GetString());

        // The destination decides it, not the transition's own name.
        Assert.Contains("\"id\":\"31\"", app.Stub.LastOf(HttpMethod.Post)!.Body);
    }

    [Fact]
    public async Task A_ticket_already_in_the_target_status_and_resolved_is_left_alone()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql",
                SampleDocuments.StatusResponse(("PROJECT-1814", Deployed, "Done")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("unchanged").GetBoolean());

        // Not even the transitions were read - the ticket is already there.
        Assert.DoesNotContain(app.Stub.Requests, r => r.PathAndQuery.Contains("/transitions"));
    }

    [Fact]
    public async Task Reverting_matches_a_status_however_the_workflow_spaces_its_slash()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", Deployed)))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionsResponse(
                ("21", "Back to deployment queue", "UAT Done / Ready for Deployment")))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "ReadyForDeployment" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("\"id\":\"21\"", app.Stub.LastOf(HttpMethod.Post)!.Body);
    }

    [Fact]
    public async Task A_ticket_the_workflow_will_not_move_is_reported_with_what_jira_offers()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1816", "Open")))
            .OnGet("PROJECT-1816/transitions", SampleDocuments.TransitionsResponse(
                ("11", "Start work", "In Progress")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1816" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());

        var message = result.GetProperty("message").GetString()!;
        Assert.Contains("Open", message);
        Assert.Contains("Start work", message);
        Assert.False(TransitionPosted(app, "PROJECT-1816"));
    }

    [Fact]
    public async Task One_ticket_failing_does_not_stop_the_others()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql",
                SampleDocuments.StatusResponse(("PROJECT-1816", "Open"), ("PROJECT-1835", "In UAT")))
            // Jira refuses to even list the moves on the first one.
            .OnGet("PROJECT-1816/transitions", """{"errorMessages":["You do not have permission"]}""",
                HttpStatusCode.Forbidden)
            .OnGet("PROJECT-1835/transitions", SampleDocuments.TransitionsResponse(
                ("31", "Deploy", Deployed)))
            .On(HttpMethod.Post, "PROJECT-1835/transitions", HttpStatusCode.NoContent, "")
            .OnGet("rest/api/3/resolution", DoneResolution)
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1835", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1816", "PROJECT-1835" }, target = "DeployedToProduction" });

        var results = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results");

        Assert.False(results[0].GetProperty("success").GetBoolean());
        Assert.Contains("403", results[0].GetProperty("message").GetString());

        Assert.True(results[1].GetProperty("success").GetBoolean());
        Assert.True(TransitionPosted(app, "PROJECT-1835"));
    }

    [Fact]
    public async Task Non_afg_tickets_are_never_transitioned()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT")))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionsResponse(
                ("31", "Deploy", Deployed)))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "")
            .OnGet("rest/api/3/resolution", DoneResolution)
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1814", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814", "OTHER_PROJECT-9001" }, target = "DeployedToProduction" });

        var results = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("PROJECT-1814", results[0].GetProperty("ticketKey").GetString());
        Assert.False(TransitionPosted(app, "OTHER_PROJECT-9001"));
    }

    [Fact]
    public async Task A_request_with_nothing_transitionable_is_refused()
    {
        using var app = new TestApp();
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "OTHER_PROJECT-9001" }, target = "DeployedToProduction" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Stub.Requests);
    }

    [Fact]
    public async Task Every_ticket_status_is_read_in_one_call()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT"), ("PROJECT-1835", Deployed)));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/statuses",
            new { ticketKeys = new[] { "PROJECT-1814", "PROJECT-1835" } });

        var tickets = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("tickets");

        Assert.Equal("In UAT", tickets[0].GetProperty("status").GetString());
        Assert.Equal(Deployed, tickets[1].GetProperty("status").GetString());
        Assert.Single(app.Stub.Requests);
    }

    [Fact]
    public async Task A_ticket_jira_does_not_return_is_reported_as_unknown_rather_than_guessed()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/statuses",
            new { ticketKeys = new[] { "PROJECT-1814", "PROJECT-9999" } });

        var tickets = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("tickets");

        Assert.Equal("PROJECT-9999", tickets[1].GetProperty("ticketKey").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, tickets[1].GetProperty("status").ValueKind);
    }

    [Fact]
    public async Task The_config_endpoint_reports_the_names_the_buttons_are_labelled_with()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var workflow = SampleDocuments.Parse(await client.GetStringAsync("/api/config"))
            .GetProperty("workflow");

        Assert.Equal(Deployed, workflow.GetProperty("deployedToProduction").GetString());
        Assert.Equal(Ready, workflow.GetProperty("readyForDeployment").GetString());
        Assert.Equal("Done", workflow.GetProperty("resolution").GetString());
    }

    // ---- Resolution ---------------------------------------------------------

    [Fact]
    public async Task The_resolution_rides_along_with_the_transition_when_the_screen_offers_it()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT")))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionWithResolution(
                "31", "Deploy", Deployed, required: true, ("10000", "Done"), ("10001", "Won't Do")))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("""{"resolution":{"id":"10000"}}""", app.Stub.LastOf(HttpMethod.Post)!.Body);

        // The dropdown is on the transition, so there is nothing left to edit.
        Assert.Null(IssueEdit(app, "PROJECT-1814"));
    }

    [Fact]
    public async Task A_transition_screen_without_the_dropdown_gets_the_resolution_as_its_own_edit()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT")))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionsResponse(
                ("31", "Deploy", Deployed)))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "")
            .OnGet("rest/api/3/resolution", DoneResolution)
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1814", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("Done", result.GetProperty("message").GetString());
        Assert.Contains("""{"resolution":{"id":"10000"}}""", IssueEdit(app, "PROJECT-1814")!.Body);
    }

    [Fact]
    public async Task A_resolution_the_dropdown_does_not_offer_stops_the_move_being_half_done()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", "In UAT")))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionWithResolution(
                "31", "Deploy", Deployed, required: true, ("10001", "Won't Do")))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Won't Do", result.GetProperty("message").GetString());

        // Nothing moved: a status change without its resolution is worse than none.
        Assert.False(TransitionPosted(app, "PROJECT-1814"));
    }

    [Fact]
    public async Task A_ticket_already_deployed_but_unresolved_still_gets_its_resolution()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", Deployed)))
            .OnGet("rest/api/3/resolution", DoneResolution)
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1814", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("unchanged").GetBoolean());
        Assert.NotNull(IssueEdit(app, "PROJECT-1814"));
        Assert.False(TransitionPosted(app, "PROJECT-1814"));
    }

    [Fact]
    public async Task Going_back_to_the_deployment_queue_leaves_the_resolution_alone()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql",
                SampleDocuments.StatusResponse(("PROJECT-1814", Deployed, "Done")))
            .OnGet("PROJECT-1814/transitions", SampleDocuments.TransitionsResponse(
                ("21", "Back out", Ready)))
            .On(HttpMethod.Post, "PROJECT-1814/transitions", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "ReadyForDeployment" });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("resolution", app.Stub.LastOf(HttpMethod.Post)!.Body);
        Assert.Null(IssueEdit(app, "PROJECT-1814"));
    }

    [Fact]
    public async Task Clearing_the_resolution_sends_null_and_never_touches_the_status()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql",
                SampleDocuments.StatusResponse(("PROJECT-1814", Deployed, "Done")))
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1814", HttpStatusCode.NoContent, "");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/resolution",
            new { ticketKeys = new[] { "PROJECT-1814" }, resolved = false });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("resolution").ValueKind);

        // Unresolved is the field cleared, not a value called "Unresolved".
        Assert.Contains("""{"resolution":null}""", IssueEdit(app, "PROJECT-1814")!.Body);
        Assert.DoesNotContain(app.Stub.Requests, r => r.PathAndQuery.Contains("/transitions"));
    }

    [Fact]
    public async Task A_ticket_that_is_already_unresolved_is_left_alone()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", Deployed)));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/resolution",
            new { ticketKeys = new[] { "PROJECT-1814" }, resolved = false });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("unchanged").GetBoolean());
        Assert.Null(IssueEdit(app, "PROJECT-1814"));
    }

    [Fact]
    public async Task A_configured_resolution_the_site_does_not_have_names_the_ones_it_does()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql", SampleDocuments.StatusResponse(("PROJECT-1814", Deployed)))
            .OnGet("rest/api/3/resolution",
                SampleDocuments.ResolutionsResponse(("10001", "Won't Do"), ("10002", "Duplicate")));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/resolution",
            new { ticketKeys = new[] { "PROJECT-1814" }, resolved = true });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());

        var message = result.GetProperty("message").GetString()!;
        Assert.Contains("Won't Do", message);
        Assert.Contains("Duplicate", message);
    }

    [Fact]
    public async Task An_edit_screen_without_the_resolution_field_reports_what_jira_said()
    {
        var stub = new StubAtlassian()
            .OnPost("rest/api/3/search/jql",
                SampleDocuments.StatusResponse(("PROJECT-1814", Deployed, "Done")))
            .On(HttpMethod.Put, "rest/api/3/issue/PROJECT-1814", HttpStatusCode.BadRequest,
                """{"errorMessages":[],"errors":{"resolution":"Field 'resolution' cannot be set. It is not on the appropriate screen, or unknown."}}""");

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/resolution",
            new { ticketKeys = new[] { "PROJECT-1814" }, resolved = false });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("not on the appropriate screen", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_status_read_carries_the_resolution_so_the_table_can_show_both()
    {
        var stub = new StubAtlassian().OnPost("rest/api/3/search/jql",
            SampleDocuments.StatusResponse(("PROJECT-1814", Deployed, "Done"), ("PROJECT-1835", "In UAT", null)));

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/jira/statuses",
            new { ticketKeys = new[] { "PROJECT-1814", "PROJECT-1835" } });

        var tickets = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("tickets");

        Assert.Equal("Done", tickets[0].GetProperty("resolution").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, tickets[1].GetProperty("resolution").ValueKind);
    }

    [Fact]
    public async Task Changing_a_resolution_requires_credentials_like_every_other_write()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/jira/resolution",
            new { ticketKeys = new[] { "PROJECT-1814" }, resolved = false });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Stub.Requests);
    }

    [Fact]
    public async Task Transitioning_requires_credentials_like_every_other_write()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/jira/transition",
            new { ticketKeys = new[] { "PROJECT-1814" }, target = "DeployedToProduction" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Stub.Requests);
    }
}
