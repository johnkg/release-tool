using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReleaseTool.Api.Tests.Infrastructure;

/// <summary>
/// A document shaped like the real release page: a decoy table before the
/// Approvals heading, tickets held in inlineCard URLs, an OTHER_PROJECT row to skip, and
/// attributes (colwidth, background, localId) whose survival is the point.
/// </summary>
public static class SampleDocuments
{
    /// <summary>
    /// Columns: 0 Ticket, 1 Developer Assigned, 2 Requested By, 3 PR Approved By,
    /// 4 PR Approved Status, 5 Merged to Deployment Branch. The last PROJECT row is
    /// already filled in by hand, so it doubles as the status-lozenge exemplar.
    /// </summary>
    public const string ApprovalsAdf = """
    {
      "type": "doc",
      "version": 1,
      "content": [
        { "type": "heading", "attrs": { "level": 2 },
          "content": [ { "type": "text", "text": "I. Scope" } ] },
        { "type": "table",
          "attrs": { "localId": "decoy-table", "layout": "default" },
          "content": [
            { "type": "tableRow", "content": [
              { "type": "tableCell", "attrs": { "colwidth": [100] },
                "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "not approvals" } ] } ] } ] } ] },
        { "type": "heading", "attrs": { "level": 2 },
          "content": [ { "type": "text", "text": "IV. Approvals" } ] },
        { "type": "table",
          "attrs": { "localId": "approvals-table", "layout": "default", "width": 900 },
          "content": [
            { "type": "tableRow", "content": [
              { "type": "tableHeader", "attrs": { "background": "#f4f5f7" },
                "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Ticket" } ] } ] },
              { "type": "tableHeader", "attrs": { "background": "#f4f5f7" },
                "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Developer Assigned" } ] } ] },
              { "type": "tableHeader", "attrs": { "background": "#f4f5f7" },
                "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Requested By" } ] } ] },
              { "type": "tableHeader", "attrs": { "background": "#f4f5f7" },
                "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "PR Approved By" } ] } ] },
              { "type": "tableHeader", "attrs": { "background": "#f4f5f7" },
                "content": [
                  { "type": "paragraph", "content": [ { "type": "text", "text": "PR Approved Status (LW)" } ] },
                  { "type": "paragraph", "content": [
                    { "type": "status", "attrs": { "text": "REJECTED", "color": "red", "localId": "legend-rejected", "style": "" } } ] },
                  { "type": "paragraph", "content": [
                    { "type": "status", "attrs": { "text": "APPROVED", "color": "green", "localId": "legend-approved", "style": "" } } ] } ] },
              { "type": "tableHeader", "attrs": { "background": "#f4f5f7" },
                "content": [
                  { "type": "paragraph", "content": [ { "type": "text", "text": "Merged to Deployment Branch" } ] },
                  { "type": "paragraph", "content": [
                    { "type": "status", "attrs": { "text": "MERGED", "color": "green", "localId": "legend-merged", "style": "" } } ] } ] } ] },
            { "type": "tableRow", "content": [
              { "type": "tableCell", "attrs": { "colwidth": [220] },
                "content": [ { "type": "paragraph", "content": [
                  { "type": "inlineCard", "attrs": { "url": "https://your-domain.atlassian.net/browse/PROJECT-1814" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] } ] },
            { "type": "tableRow", "content": [
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [
                  { "type": "inlineCard", "attrs": { "url": "https://your-domain.atlassian.net/browse/OTHER_PROJECT-9001" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] } ] },
            { "type": "tableRow", "content": [
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [
                  { "type": "inlineCard", "attrs": { "url": "https://your-domain.atlassian.net/browse/PROJECT-1835" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Someone Else" } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [
                  { "type": "status", "attrs": { "text": "APPROVED", "color": "green", "localId": "existing-status", "style": "" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] } ] } ] }
      ]
    }
    """;

    public static JsonObject Approvals() => (JsonObject)JsonNode.Parse(ApprovalsAdf)!;

    /// <summary>
    /// The v2 page envelope. The ADF arrives as a JSON *string*, which is the
    /// shape the service has to unwrap.
    /// </summary>
    public static string PageEnvelope(string pageId, string title, int version, string adf) =>
        new JsonObject
        {
            ["id"] = pageId,
            ["title"] = title,
            ["version"] = new JsonObject { ["number"] = version },
            ["body"] = new JsonObject
            {
                ["atlas_doc_format"] = new JsonObject
                {
                    ["representation"] = "atlas_doc_format",
                    ["value"] = adf
                }
            }
        }.ToJsonString();

    public static string PageEnvelope(int version = 42) =>
        PageEnvelope("PAGE_ID", "Copy of Release 1.2.3 - 13 August 2026", version, ApprovalsAdf);

    /// <summary>A search/jql response carrying one comment and a reporter per ticket.</summary>
    public static string JqlResponse(params (string Key, string AuthorName, string AuthorId, string CommentText)[] issues)
        => JqlResponse("Reporting Person", "acc-reporter", issues);

    public static string JqlResponse(
        string? reporterName,
        string? reporterId,
        params (string Key, string AuthorName, string AuthorId, string CommentText)[] issues)
    {
        var array = new JsonArray();

        foreach (var (key, authorName, authorId, text) in issues)
        {
            array.Add(new JsonObject
            {
                ["key"] = key,
                ["fields"] = new JsonObject
                {
                    ["reporter"] = reporterName is null || reporterId is null
                        ? null
                        : new JsonObject
                        {
                            ["displayName"] = reporterName,
                            ["accountId"] = reporterId
                        },
                    ["comment"] = new JsonObject
                    {
                        ["total"] = 1,
                        ["comments"] = new JsonArray(
                            new JsonObject
                            {
                                ["created"] = "2026-08-01T10:00:00.000+0800",
                                ["author"] = new JsonObject
                                {
                                    ["displayName"] = authorName,
                                    ["accountId"] = authorId
                                },
                                ["body"] = new JsonObject
                                {
                                    ["type"] = "doc",
                                    ["version"] = 1,
                                    ["content"] = new JsonArray(
                                        new JsonObject
                                        {
                                            ["type"] = "paragraph",
                                            ["content"] = new JsonArray(
                                                new JsonObject { ["type"] = "text", ["text"] = text })
                                        })
                                }
                            })
                    }
                }
            });
        }

        return new JsonObject { ["issues"] = array }.ToJsonString();
    }

    /// <summary>
    /// A search/jql response carrying each ticket's workflow status, and no
    /// resolution - which is what an unresolved issue actually looks like.
    /// </summary>
    public static string StatusResponse(params (string Key, string Status)[] issues)
        => StatusResponse([.. issues.Select(i => (i.Key, i.Status, (string?)null))]);

    public static string StatusResponse(params (string Key, string Status, string? Resolution)[] issues)
    {
        var array = new JsonArray();

        foreach (var (key, status, resolution) in issues)
        {
            array.Add(new JsonObject
            {
                ["key"] = key,
                ["fields"] = new JsonObject
                {
                    ["status"] = new JsonObject { ["name"] = status },
                    ["resolution"] = resolution is null
                        ? null
                        : new JsonObject { ["name"] = resolution }
                }
            });
        }

        return new JsonObject { ["issues"] = array }.ToJsonString();
    }

    /// <summary>The site's resolutions, as /rest/api/3/resolution returns them.</summary>
    public static string ResolutionsResponse(params (string Id, string Name)[] resolutions)
    {
        var array = new JsonArray();

        foreach (var (id, name) in resolutions)
        {
            array.Add(new JsonObject { ["id"] = id, ["name"] = name });
        }

        return array.ToJsonString();
    }

    /// <summary>
    /// The moves Jira offers on an issue. The destination is what a target
    /// status is matched against, so it is carried separately from the name.
    /// </summary>
    public static string TransitionsResponse(params (string Id, string Name, string To)[] transitions)
    {
        var array = new JsonArray();

        foreach (var (id, name, to) in transitions)
        {
            array.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["to"] = new JsonObject { ["name"] = to }
            });
        }

        return new JsonObject { ["transitions"] = array }.ToJsonString();
    }

    /// <summary>
    /// One transition whose screen carries the Resolution dropdown, the way
    /// expand=transitions.fields reports it. Its absence is what tells the tool
    /// the resolution has to be a separate edit.
    /// </summary>
    public static string TransitionWithResolution(
        string id,
        string name,
        string to,
        bool required,
        params (string Id, string Name)[] allowed)
    {
        var values = new JsonArray();

        foreach (var (valueId, valueName) in allowed)
        {
            values.Add(new JsonObject { ["id"] = valueId, ["name"] = valueName });
        }

        return new JsonObject
        {
            ["transitions"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["to"] = new JsonObject { ["name"] = to },
                    ["fields"] = new JsonObject
                    {
                        ["resolution"] = new JsonObject
                        {
                            ["required"] = required,
                            ["allowedValues"] = values
                        }
                    }
                })
        }.ToJsonString();
    }

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
