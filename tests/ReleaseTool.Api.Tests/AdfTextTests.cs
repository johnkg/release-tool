using System.Text.Json.Nodes;
using ReleaseTool.Api.Adf;

namespace ReleaseTool.Api.Tests;

public class AdfTextTests
{
    [Fact]
    public void Flatten_reads_text_nodes()
    {
        var node = JsonNode.Parse("""
        { "type": "paragraph", "content": [ { "type": "text", "text": "Fixed on" } ] }
        """);

        Assert.Contains("Fixed on", AdfText.Flatten(node));
    }

    /// <summary>
    /// The ticket reference lives in a smartlink's URL. Flattening that misses
    /// the URL is why the secondary rule looks like it never matches.
    /// </summary>
    [Fact]
    public void Flatten_includes_smartlink_urls()
    {
        var node = JsonNode.Parse("""
        { "type": "paragraph", "content": [
            { "type": "text", "text": "Fixed on " },
            { "type": "inlineCard", "attrs": { "url": "https://your-domain.atlassian.net/browse/PROJECT-1834" } } ] }
        """);

        var text = AdfText.Flatten(node);

        Assert.Contains("Fixed on", text);
        Assert.Contains("PROJECT-1834", text);
    }

    [Fact]
    public void Flatten_includes_link_mark_hrefs()
    {
        var node = JsonNode.Parse("""
        { "type": "paragraph", "content": [
            { "type": "text", "text": "PR",
              "marks": [ { "type": "link", "attrs": { "href": "https://your-organization.visualstudio.com/Platform/_git/sample-web/pullrequest/4821" } } ] } ] }
        """);

        Assert.Contains("pullrequest/4821", AdfText.Flatten(node));
    }

    [Fact]
    public void Flatten_includes_mention_text()
    {
        var node = JsonNode.Parse("""
        { "type": "paragraph", "content": [
            { "type": "mention", "attrs": { "id": "abc", "text": "@Jordan Lee" } } ] }
        """);

        Assert.Contains("@Jordan Lee", AdfText.Flatten(node));
    }

    /// <summary>
    /// Status columns hold a lozenge, not text. Without this the preview shows
    /// a filled-in column as empty.
    /// </summary>
    [Fact]
    public void Flatten_includes_status_lozenge_text()
    {
        var node = JsonNode.Parse("""
        { "type": "paragraph", "content": [
            { "type": "status", "attrs": { "text": "Approved", "color": "green" } } ] }
        """);

        Assert.Contains("Approved", AdfText.Flatten(node));
    }

    [Fact]
    public void Flatten_of_null_is_empty()
    {
        Assert.Equal(string.Empty, AdfText.Flatten(null));
    }
}
