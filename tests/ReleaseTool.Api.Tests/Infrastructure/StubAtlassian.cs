using System.Net;
using System.Text;

namespace ReleaseTool.Api.Tests.Infrastructure;

public sealed record RecordedRequest(string Method, string PathAndQuery, string? Body, string? AuthParameter);

/// <summary>
/// Stands in for Jira and Confluence so the tests exercise the real pipeline
/// without touching the live site. Unmatched calls fail loudly rather than
/// returning something plausible.
/// </summary>
public class StubAtlassian : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string PathContains, HttpStatusCode Status, string Json)> _rules = [];

    public List<RecordedRequest> Requests { get; } = [];

    public StubAtlassian On(HttpMethod method, string pathContains, HttpStatusCode status, string json)
    {
        _rules.Add((method, pathContains, status, json));
        return this;
    }

    public StubAtlassian OnGet(string pathContains, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(HttpMethod.Get, pathContains, status, json);

    public StubAtlassian OnPost(string pathContains, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(HttpMethod.Post, pathContains, status, json);

    public StubAtlassian OnPut(string pathContains, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(HttpMethod.Put, pathContains, status, json);

    public RecordedRequest? LastOf(HttpMethod method) =>
        Requests.LastOrDefault(r => r.Method == method.Method);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method.Method, path, body, request.Headers.Authorization?.Parameter));

        foreach (var rule in _rules)
        {
            if (rule.Method == request.Method && path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(rule.Status)
                {
                    Content = new StringContent(rule.Json, Encoding.UTF8, "application/json")
                };
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($$"""{"error":"no stub for {{request.Method.Method}} {{path}}"}""",
                Encoding.UTF8, "application/json")
        };
    }
}
