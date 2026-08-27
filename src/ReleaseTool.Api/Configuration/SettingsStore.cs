using System.Text.Json;
using ReleaseTool.Api.Contracts;

namespace ReleaseTool.Api.Configuration;

/// <summary>
/// Persists the Settings tab to a JSON file. Deliberately not a database: this
/// is a handful of branch names and repositories for one team, and a file is
/// something an admin can read, edit and back up without tooling.
///
/// Nothing secret is ever written here - tokens come from the Credentials
/// configuration section, which this store never touches.
/// </summary>
public sealed class SettingsStore(IHostEnvironment environment, IConfiguration configuration, ILogger<SettingsStore> logger)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // One writer at a time. Two browser tabs saving at once would otherwise
    // interleave and leave a truncated file.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Defaults beside the app rather than in it, so a published site keeps its
    /// settings across deploys if the folder is left in place.
    /// </summary>
    public string FilePath { get; } =
        configuration["Settings:FilePath"] is { Length: > 0 } configured
            ? Path.GetFullPath(configured, environment.ContentRootPath)
            : Path.Combine(environment.ContentRootPath, "App_Data", "settings.json");

    public async Task<AppSettings> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            return AppSettings.Empty;
        }

        await _gate.WaitAsync(ct);

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var stored = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Format, ct);

            return stored is null ? AppSettings.Empty : Normalise(stored);
        }
        catch (JsonException failure)
        {
            // A hand-edited file with a typo in it must not take the app down;
            // the user can see the defaults and save over them.
            logger.LogError(failure, "Settings file {Path} is not valid JSON. Using defaults.", FilePath);
            return AppSettings.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> WriteAsync(AppSettings settings, CancellationToken ct)
    {
        var clean = Normalise(settings);

        await _gate.WaitAsync(ct);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Written to a temporary file and moved into place, so a crash
            // mid-write cannot leave a half-written settings file behind.
            var temporary = FilePath + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, clean, Format, ct);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        return clean;
    }

    /// <summary>
    /// Trims, drops blank and duplicate repositories, and fills in a blank
    /// format - so the rest of the app can treat what it reads as usable.
    /// </summary>
    private static AppSettings Normalise(AppSettings settings)
    {
        var organization = settings.DefaultOrganization?.Trim() ?? string.Empty;
        var project = settings.DefaultProject?.Trim() ?? string.Empty;

        var repositories = (settings.Repositories ?? [])
            .Select(repo => new RepositoryRef(
                string.IsNullOrWhiteSpace(repo.Organization) ? organization : repo.Organization.Trim(),
                string.IsNullOrWhiteSpace(repo.Project) ? project : repo.Project.Trim(),
                repo.Name?.Trim() ?? string.Empty))
            .Where(repo => repo.Name.Length > 0)
            .DistinctBy(repo => $"{repo.Organization}/{repo.Project}/{repo.Name}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var branches = settings.Branches ?? new DeploymentBranches();

        return new AppSettings(
            new DeploymentBranches(
                branches.Dev?.Trim() ?? string.Empty,
                branches.Sit?.Trim() ?? string.Empty,
                branches.Uat?.Trim() ?? string.Empty,
                branches.Prod?.Trim() ?? string.Empty),
            string.IsNullOrWhiteSpace(settings.BranchNameFormat)
                ? AppSettings.DefaultBranchNameFormat
                : settings.BranchNameFormat.Trim(),
            organization,
            project,
            repositories,
            string.IsNullOrWhiteSpace(settings.CandidateBranchNameFormat)
                ? AppSettings.DefaultCandidateBranchNameFormat
                : settings.CandidateBranchNameFormat.Trim());
    }
}
