using System.Text;
using System.Text.Json.Nodes;

namespace ReleaseTool.Api.Adf;

/// <summary>
/// Flattens an ADF fragment to plain text for regex matching.
/// </summary>
public static class AdfText
{
    public static string Flatten(JsonNode? node)
    {
        var builder = new StringBuilder();
        Append(node, builder);
        return builder.ToString();
    }

    private static void Append(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    Append(item, builder);
                }
                return;

            case JsonObject obj:
                AppendObject(obj, builder);
                return;
        }
    }

    private static void AppendObject(JsonObject obj, StringBuilder builder)
    {
        switch (obj["type"]?.GetValue<string>())
        {
            case "text":
                builder.Append(obj["text"]?.GetValue<string>());
                break;

            // Smartlinks hold their URL in attrs and carry no text at all. A
            // ticket reference or PR link lives here, so flattening without
            // these silently loses the thing we are matching on.
            case "inlineCard":
            case "blockCard":
            case "embedCard":
                AppendPadded(builder, obj["attrs"]?["url"]?.GetValue<string>());
                break;

            // A status lozenge and a mention both carry their words in attrs.
            // Missing these makes a populated cell read as empty.
            case "mention":
            case "status":
            case "emoji":
                AppendPadded(builder, obj["attrs"]?["text"]?.GetValue<string>());
                break;

            case "hardBreak":
            case "paragraph":
            case "listItem":
                builder.Append(' ');
                break;
        }

        // A link's href lives on a mark, not the node.
        if (obj["marks"] is JsonArray marks)
        {
            foreach (var mark in marks)
            {
                if (mark?["type"]?.GetValue<string>() == "link")
                {
                    AppendPadded(builder, mark["attrs"]?["href"]?.GetValue<string>());
                }
            }
        }

        if (obj["content"] is { } content)
        {
            Append(content, builder);
            builder.Append(' ');
        }
    }

    private static void AppendPadded(StringBuilder builder, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.Append(' ').Append(value).Append(' ');
        }
    }
}
