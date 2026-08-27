using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ReleaseTool.Api.Atlassian;

/// <summary>
/// Typed client for the Atlassian site. Jira and Confluence share one host on
/// the API-token route, so one client covers both:
///   rest/api/3/...    Jira
///   wiki/api/v2/...   Confluence
/// Auth is Basic and per request - credentials belong to the caller, not the
/// client - so no default Authorization header is ever set.
/// </summary>
public sealed class AtlassianClient(HttpClient http, ILogger<AtlassianClient> logger)
{
    public Task<JsonNode?> GetAsync(string path, AtlassianCredentials credentials, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, credentials, body: null, ct);

    public Task<JsonNode?> PostAsync(string path, AtlassianCredentials credentials, JsonNode body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, credentials, body, ct);

    public Task<JsonNode?> PutAsync(string path, AtlassianCredentials credentials, JsonNode body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, credentials, body, ct);

    private async Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        AtlassianCredentials credentials,
        JsonNode? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials.ToBasicParameter());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Atlassian {Method} {Path} -> {StatusCode}",
                method.Method, path, (int)response.StatusCode);

            throw new AtlassianApiException(response.StatusCode, path, Truncate(payload));
        }

        logger.LogDebug("Atlassian {Method} {Path} -> {StatusCode}", method.Method, path, (int)response.StatusCode);

        return string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
    }

    private static string? Truncate(string? value, int max = 500) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max] + "...";
}
