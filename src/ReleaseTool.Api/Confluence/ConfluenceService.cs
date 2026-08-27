using System.Net;
using System.Text.Json.Nodes;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Contracts;

namespace ReleaseTool.Api.Confluence;

/// <summary>A page's ADF document plus the version needed to write it back.</summary>
public sealed record PageDocument(string PageId, string Title, int Version, JsonObject Document);

public sealed class ConfluenceService(AtlassianClient client, ILogger<ConfluenceService> logger)
{
    /// <summary>
    /// Space key to numeric ID. The pages endpoint rejects the key outright
    /// ("Expected type is long"), and the two look nothing alike:
    /// key '~sandbox-space' vs id '9000001'.
    /// </summary>
    public async Task<string> ResolveSpaceIdAsync(string spaceKey, AtlassianCredentials credentials, CancellationToken ct)
    {
        var response = await client.GetAsync(
            $"wiki/api/v2/spaces?keys={Uri.EscapeDataString(spaceKey)}", credentials, ct);

        var id = response?["results"]?.AsArray().FirstOrDefault()?["id"]?.ToString();

        if (string.IsNullOrEmpty(id))
        {
            throw ReleaseToolException.NotFound($"No space found with key '{spaceKey}'.");
        }

        logger.LogInformation("Space {SpaceKey} resolved to id {SpaceId}", spaceKey, id);
        return id;
    }

    public async Task<PageSummary> FindPageAsync(string spaceId, string title, AtlassianCredentials credentials, CancellationToken ct)
    {
        var response = await client.GetAsync(
            $"wiki/api/v2/spaces/{spaceId}/pages?title={Uri.EscapeDataString(title)}", credentials, ct);

        var page = response?["results"]?.AsArray().FirstOrDefault();

        if (page is null)
        {
            // An unpublished draft is invisible to both the API and CQL search,
            // so "no results" is far more often a draft than a typo.
            throw ReleaseToolException.NotFound(
                $"No page titled '{title}' in that space. Unpublished drafts are not visible to the API - is the page published?");
        }

        return new PageSummary(
            PageId: page["id"]!.ToString(),
            Title: page["title"]?.GetValue<string>() ?? title,
            SpaceId: spaceId,
            Version: page["version"]?["number"]?.GetValue<int>() ?? 0);
    }

    /// <summary>
    /// Pages in the space, newest first. Confluence sorts server-side with
    /// '-created-date', so the first result is the latest page added.
    /// </summary>
    public async Task<IReadOnlyList<PageListItem>> ListPagesAsync(
        string spaceId,
        AtlassianCredentials credentials,
        CancellationToken ct,
        int limit = 50)
    {
        var response = await client.GetAsync(
            $"wiki/api/v2/spaces/{spaceId}/pages?sort=-created-date&status=current&limit={limit}", credentials, ct);

        var pages = new List<PageListItem>();

        foreach (var page in response?["results"]?.AsArray() ?? [])
        {
            if (page?["id"] is null)
            {
                continue;
            }

            pages.Add(new PageListItem(
                PageId: page["id"]!.ToString(),
                Title: page["title"]?.GetValue<string>() ?? "(untitled)",
                CreatedAt: page["createdAt"]?.GetValue<string>() is { } created
                    && DateTimeOffset.TryParse(created, out var parsed)
                        ? parsed
                        : null));
        }

        return pages;
    }

    public async Task<PageDocument> FetchDocumentAsync(string pageId, AtlassianCredentials credentials, CancellationToken ct)
    {
        var response = await client.GetAsync(
            $"wiki/api/v2/pages/{pageId}?body-format=atlas_doc_format", credentials, ct)
            ?? throw ReleaseToolException.NotFound($"Page {pageId} returned no body.");

        // v2 hands back the ADF as a JSON *string*, not as an object.
        var raw = response["body"]?["atlas_doc_format"]?["value"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(raw) || JsonNode.Parse(raw) is not JsonObject document)
        {
            throw new ReleaseToolException($"Page {pageId} has no atlas_doc_format body to work with.");
        }

        return new PageDocument(
            PageId: pageId,
            Title: response["title"]?.GetValue<string>() ?? string.Empty,
            Version: response["version"]?["number"]?.GetValue<int>() ?? 0,
            Document: document);
    }

    /// <summary>Writes the mutated document back at version + 1.</summary>
    public async Task<int> UpdateDocumentAsync(PageDocument page, string versionMessage, AtlassianCredentials credentials, CancellationToken ct)
    {
        var next = page.Version + 1;

        var body = new JsonObject
        {
            ["id"] = page.PageId,
            ["status"] = "current",
            ["title"] = page.Title,
            ["body"] = new JsonObject
            {
                ["representation"] = "atlas_doc_format",
                // Must be the serialised string, mirroring how it was read.
                ["value"] = page.Document.ToJsonString()
            },
            ["version"] = new JsonObject
            {
                ["number"] = next,
                ["message"] = versionMessage
            }
        };

        var response = await client.PutAsync($"wiki/api/v2/pages/{page.PageId}", credentials, body, ct);
        var written = response?["version"]?["number"]?.GetValue<int>() ?? next;

        logger.LogInformation("Page {PageId} written as version {Version}", page.PageId, written);
        return written;
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to a freshly fetched document and writes
    /// it back, retrying on a concurrent edit. The retry refetches rather than
    /// forcing, so someone else's edit is merged instead of overwritten.
    /// </summary>
    public async Task<(int Version, int Changed)> UpdateWithRetryAsync(
        string pageId,
        Func<PageDocument, int> mutate,
        string versionMessage,
        AtlassianCredentials credentials,
        CancellationToken ct,
        int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            var page = await FetchDocumentAsync(pageId, credentials, ct);
            var changed = mutate(page);

            try
            {
                var version = await UpdateDocumentAsync(page, versionMessage, credentials, ct);
                return (version, changed);
            }
            catch (AtlassianApiException e) when (e.StatusCode == HttpStatusCode.Conflict && attempt < attempts)
            {
                logger.LogWarning(
                    "Concurrent edit on page {PageId}, refetching (attempt {Attempt} of {Attempts})",
                    pageId, attempt, attempts);
            }
        }
    }
}
