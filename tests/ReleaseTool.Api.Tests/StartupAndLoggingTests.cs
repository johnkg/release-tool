using System.Net;
using Microsoft.AspNetCore.Hosting;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Tests.Infrastructure;
using Serilog.Events;

namespace ReleaseTool.Api.Tests;

public class StartupAndLoggingTests
{
    [Fact]
    public void Invalid_configuration_fails_at_startup_naming_the_field()
    {
        using var app = new TestApp(configure: b => b.UseSetting("Atlassian:BaseUrl", "not-a-url"));

        var failure = Record.Exception(() => app.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("BaseUrl", failure.ToString());
    }

    [Fact]
    public void Missing_configuration_fails_at_startup()
    {
        using var app = new TestApp(configure: b => b.UseSetting("Atlassian:FallbackDeveloperName", ""));

        var failure = Record.Exception(() => app.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("FallbackDeveloperName", failure.ToString());
    }

    [Fact]
    public void Credentials_never_render_their_token()
    {
        var credentials = new AtlassianCredentials("someone@example.com", "super-secret-token");

        Assert.DoesNotContain("super-secret-token", credentials.ToString());
        Assert.Contains("someone@example.com", credentials.ToString());
    }

    /// <summary>
    /// Request logging has to sit outside the exception handler. With the order
    /// reversed, a handled Atlassian 401 is logged as an unhandled 500 while the
    /// client is correctly sent 401 - so the logs disagree with reality.
    /// </summary>
    [Fact]
    public async Task Handled_failures_are_logged_with_the_status_the_client_received()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself", "{}", HttpStatusCode.Unauthorized);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/auth/verify", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var completion = await app.Logs.WaitForRequestCompletion("/api/auth/verify");

        Assert.True(completion is not null,
            $"no request completion event. Captured {app.Logs.Events.Count} events: {app.Logs.RenderAll()}");

        // The status recorded must be the one the client got. With the handler
        // outside the request logging, this reads 500 with a stack trace.
        Assert.Equal("401", completion!.Properties["StatusCode"].ToString());
        Assert.Equal(LogEventLevel.Information, completion.Level);
        Assert.Null(completion.Exception);
    }

    [Fact]
    public async Task The_token_never_reaches_the_logs()
    {
        var stub = new StubAtlassian().OnGet("rest/api/3/myself", "{}", HttpStatusCode.Unauthorized);

        using var app = new TestApp(stub);
        using var client = app.CreateAuthenticatedClient();

        await client.PostAsync("/api/auth/verify", null);

        Assert.NotEmpty(app.Logs.Events);
        Assert.DoesNotContain(TestApp.TestToken, app.Logs.RenderAll());
    }
}
