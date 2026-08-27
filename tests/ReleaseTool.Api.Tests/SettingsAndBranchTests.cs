using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using ReleaseTool.Api.DevOps;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// The Settings tab's file-backed persistence, and the deployment branch
/// operations the Deployment tab drives.
/// </summary>
public class SettingsAndBranchTests : IDisposable
{
    private readonly string _settingsFile =
        Path.Combine(Path.GetTempPath(), $"releasetool-settings-{Guid.NewGuid():N}.json");

    private Action<IWebHostBuilder> UsingTempSettings =>
        builder => builder.UseSetting("Settings:FilePath", _settingsFile);

    public void Dispose()
    {
        if (File.Exists(_settingsFile))
        {
            File.Delete(_settingsFile);
        }

        GC.SuppressFinalize(this);
    }

    private static HttpClient WithDevOpsToken(TestApp app, string token = "pat-123")
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(DevOpsCredentials.TokenHeader, token);
        return client;
    }

    private const string Source = "release/prod";
    private const string Branch = "dev/release/feat/PROJECT-RELEASE-13082026";
    private const string SourceSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private static object OneRepository => new
    {
        organization = "your-organization",
        project = "Platform",
        name = "sample-web",
    };

    // ---- Settings -----------------------------------------------------------

    [Fact]
    public async Task Settings_default_before_anything_is_saved()
    {
        using var app = new TestApp(configure: UsingTempSettings);
        using var client = app.CreateClient();

        var payload = SampleDocuments.Parse(await client.GetStringAsync("/api/settings"));

        Assert.Equal("dev/release/feat/PROJECT-RELEASE-{DDMMYYYY}", payload.GetProperty("branchNameFormat").GetString());
        Assert.Empty(payload.GetProperty("repositories").EnumerateArray());
    }

    /// <summary>
    /// The point of the file: settings outlive the browser, and the process.
    /// </summary>
    [Fact]
    public async Task Settings_survive_a_restart_of_the_app()
    {
        using (var app = new TestApp(configure: UsingTempSettings))
        {
            using var client = app.CreateClient();

            var response = await client.PutAsJsonAsync("/api/settings", new
            {
                branches = new { dev = "develop", sit = "release/sit", uat = "release/uat", prod = "release/prod" },
                branchNameFormat = "dev/release/feat/PROJECT-RELEASE-{DDMMYYYY}",
                defaultOrganization = "your-organization",
                defaultProject = "Platform",
                repositories = new[] { OneRepository },
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.True(File.Exists(_settingsFile));

        // A second app, reading the same file - the restart the user cares about.
        using (var restarted = new TestApp(configure: UsingTempSettings))
        {
            using var client = restarted.CreateClient();
            var payload = SampleDocuments.Parse(await client.GetStringAsync("/api/settings"));

            Assert.Equal("release/prod", payload.GetProperty("branches").GetProperty("prod").GetString());
            Assert.Equal("sample-web", payload.GetProperty("repositories")[0].GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Saving_trims_drops_blanks_and_dedupes_repositories()
    {
        using var app = new TestApp(configure: UsingTempSettings);
        using var client = app.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings", new
        {
            branches = new { dev = "  develop  ", sit = "", uat = "", prod = " release/prod " },
            branchNameFormat = "   ",
            defaultOrganization = "your-organization",
            defaultProject = "Platform",
            repositories = new object[]
            {
                new { organization = "your-organization", project = "Platform", name = " sample-web " },
                new { organization = "your-organization", project = "Platform", name = "SAMPLE-WEB" },
                new { organization = "", project = "", name = "deploy-scripts" },
                new { organization = "your-organization", project = "Platform", name = "   " },
            },
        });

        var payload = SampleDocuments.Parse(await response.Content.ReadAsStringAsync());
        var repositories = payload.GetProperty("repositories").EnumerateArray().ToList();

        Assert.Equal("develop", payload.GetProperty("branches").GetProperty("dev").GetString());

        // A blank format falls back rather than producing an unnamed branch.
        Assert.Equal("dev/release/feat/PROJECT-RELEASE-{DDMMYYYY}", payload.GetProperty("branchNameFormat").GetString());

        Assert.Equal(2, repositories.Count);
        Assert.Equal("deploy-scripts", repositories[0].GetProperty("name").GetString());
        Assert.Equal("sample-web", repositories[1].GetProperty("name").GetString());

        // A repository added without an org or project inherits the defaults.
        Assert.Equal("your-organization", repositories[0].GetProperty("organization").GetString());
        Assert.Equal("Platform", repositories[0].GetProperty("project").GetString());
    }

    /// <summary>Settings hold no secrets, so they must not need credentials.</summary>
    [Fact]
    public async Task Settings_need_no_credentials()
    {
        using var app = new TestApp(configure: UsingTempSettings);
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/settings")).StatusCode);
        Assert.Empty(app.Stub.Requests);
    }

    // ---- Creating branches --------------------------------------------------

    [Fact]
    public async Task Creating_a_branch_cuts_it_from_the_source_commit()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Source}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Source}}", "objectId": "{{SourceSha}}" } ] }""")
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}", """{ "value": [] }""")
            .OnPost("/refs?api-version=7.1",
                """{ "value": [ { "success": true, "updateStatus": "succeeded" } ] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = Branch,
            sourceBranch = Source,
            repositories = new[] { OneRepository },
        });

        var results = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(results[0].GetProperty("success").GetBoolean());

        // The new ref must point at the source commit and declare no previous one.
        var posted = app.Stub.LastOf(HttpMethod.Post)!.Body!;

        Assert.Contains($"refs/heads/{Branch}", posted);
        Assert.Contains(SourceSha, posted);
        Assert.Contains(new string('0', 40), posted);
    }

    /// <summary>
    /// A prefix filter would match 'release/prod-old' too, so the exact ref name
    /// has to be picked out of the results.
    /// </summary>
    [Fact]
    public async Task A_similarly_named_branch_is_not_mistaken_for_the_source()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Source}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Source}}-old", "objectId": "{{SourceSha}}" } ] }""")
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}", """{ "value": [] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = Branch,
            sourceBranch = Source,
            repositories = new[] { OneRepository },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("does not exist", result.GetProperty("message").GetString()!);

        // Nothing was written.
        Assert.DoesNotContain(app.Stub.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task An_existing_branch_is_reported_rather_than_overwritten()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Source}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Source}}", "objectId": "{{SourceSha}}" } ] }""")
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Branch}}", "objectId": "{{SourceSha}}" } ] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = Branch,
            sourceBranch = Source,
            repositories = new[] { OneRepository },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("already exists", result.GetProperty("message").GetString()!);
        Assert.DoesNotContain(app.Stub.Requests, r => r.Method == "POST");
    }

    /// <summary>
    /// One repository refusing must not lose the others - the whole reason
    /// results are per repository.
    /// </summary>
    [Fact]
    public async Task A_repository_that_refuses_does_not_stop_the_rest()
    {
        var stub = new StubAtlassian()
            .OnGet($"repositories/locked-repo/refs?api-version=7.1&filter={Uri.EscapeDataString($"heads/{Source}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Source}}", "objectId": "{{SourceSha}}" } ] }""")
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Source}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Source}}", "objectId": "{{SourceSha}}" } ] }""")
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}", """{ "value": [] }""")
            .OnPost("repositories/locked-repo/refs",
                """{ "value": [ { "success": false, "updateStatus": "createBranchPermissionRequired" } ] }""")
            .OnPost("/refs?api-version=7.1",
                """{ "value": [ { "success": true, "updateStatus": "succeeded" } ] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = Branch,
            sourceBranch = Source,
            repositories = new[]
            {
                OneRepository,
                new { organization = "your-organization", project = "Platform", name = "locked-repo" },
            },
        });

        var results = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(results, r => r.GetProperty("success").GetBoolean());

        var refused = results.Single(r => !r.GetProperty("success").GetBoolean());
        Assert.Contains("permission", refused.GetProperty("message").GetString()!);
    }

    // ---- Deleting branches --------------------------------------------------

    [Fact]
    public async Task Deleting_points_the_ref_at_nothing()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Branch}}", "objectId": "{{SourceSha}}" } ] }""")
            .OnPost("/refs?api-version=7.1",
                """{ "value": [ { "success": true, "updateStatus": "succeeded" } ] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches/delete", new
        {
            branchName = Branch,
            sourceBranch = "",
            repositories = new[] { OneRepository },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());

        var posted = app.Stub.LastOf(HttpMethod.Post)!.Body!;

        Assert.Contains($"\"oldObjectId\":\"{SourceSha}\"", posted.Replace(" ", string.Empty));
        Assert.Contains($"\"newObjectId\":\"{new string('0', 40)}\"", posted.Replace(" ", string.Empty));
    }

    /// <summary>"Already gone" is the outcome the user wanted, not a failure.</summary>
    [Fact]
    public async Task Deleting_a_branch_that_is_not_there_succeeds_quietly()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}", """{ "value": [] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches/delete", new
        {
            branchName = Branch,
            sourceBranch = "",
            repositories = new[] { OneRepository },
        });

        var result = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("results")[0];

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("Not present", result.GetProperty("message").GetString()!);
        Assert.DoesNotContain(app.Stub.Requests, r => r.Method == "POST");
    }

    // ---- Guards -------------------------------------------------------------

    [Theory]
    [InlineData("has space")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("double//slash")]
    [InlineData("dot..dot")]
    [InlineData("caret^")]
    public async Task An_invalid_branch_name_is_refused_before_any_repository_is_touched(string branch)
    {
        using var app = new TestApp(configure: UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = branch,
            sourceBranch = Source,
            repositories = new[] { OneRepository },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Stub.Requests);
    }

    [Fact]
    public async Task Creating_without_a_source_branch_explains_where_to_set_it()
    {
        using var app = new TestApp(configure: UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = Branch,
            sourceBranch = "",
            repositories = new[] { OneRepository },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PROD", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Stub.Requests);
    }

    [Fact]
    public async Task Creating_with_no_repositories_is_refused()
    {
        using var app = new TestApp(configure: UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches", new
        {
            branchName = Branch,
            sourceBranch = Source,
            repositories = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Branch_status_reports_existing_and_missing_without_writing()
    {
        var stub = new StubAtlassian()
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Source}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Source}}", "objectId": "{{SourceSha}}" } ] }""")
            .OnGet($"filter={Uri.EscapeDataString($"heads/{Branch}")}",
                $$"""{ "value": [ { "name": "refs/heads/{{Branch}}", "objectId": "{{SourceSha}}" } ] }""");

        using var app = new TestApp(stub, UsingTempSettings);
        using var client = WithDevOpsToken(app);

        var response = await client.PostAsJsonAsync("/api/devops/branches/status", new
        {
            branchName = Branch,
            sourceBranch = Source,
            repositories = new[] { OneRepository },
        });

        var status = SampleDocuments.Parse(await response.Content.ReadAsStringAsync())
            .GetProperty("repositories")[0];

        Assert.True(status.GetProperty("exists").GetBoolean());
        Assert.True(status.GetProperty("sourceExists").GetBoolean());
        Assert.DoesNotContain(app.Stub.Requests, r => r.Method == "POST");
    }
}
