using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReleaseTool.Api;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Configuration;
using ReleaseTool.Api.Confluence;
using ReleaseTool.Api.DevOps;
using ReleaseTool.Api.Endpoints;
using ReleaseTool.Api.Jira;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// A gitignored file beside appsettings.json, for credentials on a machine where
// user-secrets is not an option - notably a published site, where the
// user-secrets provider is not loaded at all because the environment is not
// Development. Environment variables are re-added afterwards so they still win.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services));

builder.Services.AddOptions<AtlassianOptions>()
    .Bind(builder.Configuration.GetSection(AtlassianOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Optional by design: with nothing configured the app behaves exactly as before
// and every caller supplies their own credentials.
builder.Services.AddOptions<StoredCredentialsOptions>()
    .Bind(builder.Configuration.GetSection(StoredCredentialsOptions.SectionName));

// Without this, DeveloperSource crosses the wire as 0/1/2 and the UI has to
// know the ordinal - and an incoming "PullRequest" fails to bind at all.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ReleaseToolExceptionHandler>();
builder.Services.AddExceptionHandler<AtlassianExceptionHandler>();
builder.Services.AddMemoryCache();

// Azure DevOps is reached with a per-request PAT, so this client also sets no
// default Authorization header.
builder.Services.AddHttpClient<DevOpsService>(http => http.Timeout = TimeSpan.FromSeconds(60));

// Singleton: it owns the write lock that keeps two concurrent saves from
// interleaving into a half-written settings file.
builder.Services.AddSingleton<SettingsStore>();

builder.Services.AddScoped<ConfluenceService>();
builder.Services.AddScoped<JiraService>();
builder.Services.AddScoped<DeveloperResolver>();
builder.Services.AddScoped<StatusTransitioner>();

// One typed client for both Jira and Confluence - same host on the API-token
// route. IHttpClientFactory comes from the shared framework, no package needed.
builder.Services.AddHttpClient<AtlassianClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<AtlassianOptions>>().Value;

    // A base address without a trailing slash silently drops its last segment
    // when combined with a relative path.
    var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";

    http.BaseAddress = new Uri(baseUrl);
    http.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

// Server-held credentials are a convenience with a consequence: every edit is
// attributed to that account unless the caller sends their own token. Say so
// out loud at boot so it is never a silent surprise on a shared host.
var storedCredentials = app.Services.GetRequiredService<IOptions<StoredCredentialsOptions>>().Value;

if (storedCredentials.HasAtlassian)
{
    app.Logger.LogWarning(
        "Atlassian credentials are configured server-side. Page edits will be attributed to {Email} "
        + "unless the caller sends their own token. Put a sign-in in front of this app before sharing the URL.",
        storedCredentials.AtlassianEmail);
}

// Request logging must sit OUTSIDE the exception handler, or it records the
// raw exception as a 500 and never sees the status the client actually got.
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

// The SPA is published into wwwroot and served from the same origin. In
// development it comes from Vite instead, so these are no-ops there.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// An unmatched /api path must 404 rather than fall through to the SPA shell,
// which would answer a typo'd endpoint with 200 and a page of HTML.
app.Map("/api/{**path}", () => Results.NotFound());

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so the integration tests can host the real pipeline.</summary>
public partial class Program;
