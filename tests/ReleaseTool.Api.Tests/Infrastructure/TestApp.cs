using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.DevOps;
using Serilog.Core;

namespace ReleaseTool.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real application - the same middleware order, filters, options
/// validation and endpoints - with only the outbound HTTP handler swapped.
/// </summary>
public sealed class TestApp(
    StubAtlassian? stub = null,
    Action<IWebHostBuilder>? configure = null,
    IDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public StubAtlassian Stub { get; } = stub ?? new StubAtlassian();

    /// <summary>Everything the app logged during the test.</summary>
    public CollectingSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Atlassian:BaseUrl", "https://your-domain.atlassian.net/");
        builder.UseSetting("Atlassian:DefaultSpaceKey", "~sandbox-space");
        builder.UseSetting("Atlassian:FallbackDeveloperName", "Jordan Lee");

        // Never the developer's real settings file, and never the app's default
        // path - a test run must not overwrite what the tool is using.
        builder.UseSetting("Settings:FilePath",
            Path.Combine(Path.GetTempPath(), $"releasetool-tests-{Guid.NewGuid():N}.json"));

        configure?.Invoke(builder);

        // Added LAST, so it outranks every source the application registers.
        //
        // This has to be a configuration source rather than UseSetting: the test
        // host's content root is the API project directory, so Program.cs finds
        // the developer's real appsettings.Local.json there and adds it at the
        // end of the chain - where it would beat any host setting and quietly
        // authenticate the "no credentials" tests.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Credentials:AtlassianEmail"] = string.Empty,
                ["Credentials:AtlassianApiToken"] = string.Empty,
                ["Credentials:DevOpsPersonalAccessToken"] = string.Empty,
            };

            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                values[key] = value;
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            // Adds to the existing named client, so the BaseAddress configured in
            // Program.cs still applies - only the transport is replaced.
            services.AddHttpClient<AtlassianClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Stub);

            services.AddHttpClient<DevOpsService>()
                .ConfigurePrimaryHttpMessageHandler(() => Stub);

            // Program.cs calls ReadFrom.Services, so a registered sink joins the
            // real logging pipeline.
            services.AddSingleton<ILogEventSink>(Logs);
        });
    }

    /// <summary>A client carrying the credential headers every endpoint requires.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AtlassianCredentials.EmailHeader, "tester@example.com");
        client.DefaultRequestHeaders.Add(AtlassianCredentials.TokenHeader, TestToken);
        return client;
    }

    /// <summary>Distinctive so a leak into logs is unambiguous.</summary>
    public const string TestToken = "tok_SENTINEL_do_not_log_9f3b";

    public static string ExpectedBasic(string email, string token) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));

    public static AuthenticationHeaderValue Basic(string parameter) => new("Basic", parameter);
}
