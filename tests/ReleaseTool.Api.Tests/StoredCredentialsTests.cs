using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using ReleaseTool.Api.DevOps;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// Credentials read from configuration, so they are not typed in every session.
/// The rules under test: configuration is a fallback, a sent credential always
/// wins, and no token ever travels back to the browser.
/// </summary>
public class StoredCredentialsTests
{
    private const string ConfiguredEmail = "configured@example.com";
    private const string ConfiguredToken = "cfg_SENTINEL_never_leaves_the_server_7c1a";
    private const string ConfiguredPat = "pat_SENTINEL_never_leaves_the_server_2d4e";

    /// <summary>
    /// Applied as the last configuration source by <see cref="TestApp"/>, which
    /// is the only way to be sure of beating the developer's own
    /// appsettings.Local.json.
    /// </summary>
    private static Dictionary<string, string?> Configured => new()
    {
        ["Credentials:AtlassianEmail"] = ConfiguredEmail,
        ["Credentials:AtlassianApiToken"] = ConfiguredToken,
        ["Credentials:DevOpsPersonalAccessToken"] = ConfiguredPat,
    };

    [Fact]
    public async Task Configured_credentials_are_used_when_the_caller_sends_none()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself",
            """{ "accountId": "acc-1", "displayName": "Jordan Lee" }""");

        using var app = new TestApp(stub, settings: Configured);
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            TestApp.ExpectedBasic(ConfiguredEmail, ConfiguredToken),
            stub.Requests.Single().AuthParameter);
    }

    /// <summary>
    /// The audit trail depends on this: someone who sends their own token must
    /// act as themselves, whatever the server holds.
    /// </summary>
    [Fact]
    public async Task Caller_headers_win_over_the_configured_credentials()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself",
            """{ "accountId": "acc-2", "displayName": "Alex Taylor" }""");

        using var app = new TestApp(stub, settings: Configured);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(
            TestApp.ExpectedBasic("tester@example.com", TestApp.TestToken),
            stub.Requests.Single().AuthParameter);
    }

    [Fact]
    public async Task With_nothing_configured_a_credential_free_request_is_still_rejected()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Stub.Requests);
    }

    [Fact]
    public async Task The_configured_devops_pat_is_used_when_no_header_is_sent()
    {
        using var app = new TestApp(settings: Configured);
        using var client = app.CreateClient();

        // An empty list short-circuits before any Azure DevOps call, so this
        // tests the filter and nothing else.
        var response = await client.PostAsJsonAsync("/api/devops/pull-requests",
            new { pullRequests = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_pat_the_devops_endpoint_still_401s()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devops/pull-requests",
            new { pullRequests = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(DevOpsCredentials.TokenHeader, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The whole point of holding the token server-side is that the browser
    /// never sees it. This is the test that keeps it that way.
    /// </summary>
    [Fact]
    public async Task The_config_endpoint_reports_existence_but_never_the_secrets()
    {
        using var app = new TestApp(settings: Configured);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/config");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(ConfiguredToken, body);
        Assert.DoesNotContain(ConfiguredPat, body);

        var payload = SampleDocuments.Parse(body);

        Assert.True(payload.GetProperty("atlassian").GetProperty("configured").GetBoolean());
        Assert.True(payload.GetProperty("devOps").GetProperty("configured").GetBoolean());

        // The account the tool will act as has to be visible - it is the name
        // that lands in the Confluence page history.
        Assert.Equal(ConfiguredEmail, payload.GetProperty("atlassian").GetProperty("email").GetString());

        // A DevOps PAT has no email to report, and must not borrow the Atlassian one.
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("devOps").GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task The_config_endpoint_is_readable_without_credentials_and_reports_the_default_space()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var payload = SampleDocuments.Parse(await client.GetStringAsync("/api/config"));

        Assert.False(payload.GetProperty("atlassian").GetProperty("configured").GetBoolean());
        Assert.False(payload.GetProperty("devOps").GetProperty("configured").GetBoolean());
        Assert.Equal("~sandbox-space", payload.GetProperty("defaultSpaceKey").GetString());
    }

    /// <summary>
    /// The host's own light/dark setting is only the caller's when they are the
    /// same machine. The test host talks over loopback, so it should be told.
    /// </summary>
    [Fact]
    public async Task The_config_endpoint_reports_the_host_theme_to_a_local_caller()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var payload = SampleDocuments.Parse(await client.GetStringAsync("/api/config"));
        var theme = payload.GetProperty("osTheme");

        // Null on a machine whose setting cannot be read; never anything else.
        Assert.True(
            theme.ValueKind == JsonValueKind.Null || theme.GetString() is "light" or "dark",
            $"osTheme was '{theme}'");
    }

    /// <summary>A configured token must not reach the log files either.</summary>
    [Fact]
    public async Task A_configured_token_is_never_logged()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself",
            """{ "accountId": "acc-1", "displayName": "Jordan Lee" }""");

        using var app = new TestApp(stub, settings: Configured);
        using var client = app.CreateClient();

        await client.PostAsync("/api/auth/verify", null);
        await app.Logs.WaitForRequestCompletion("/api/auth/verify");

        Assert.DoesNotContain(ConfiguredToken, app.Logs.RenderAll());
    }
}
