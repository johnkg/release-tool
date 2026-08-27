using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReleaseTool.Api.Adf;

/// <summary>Where each managed column sits in the table.</summary>
public sealed class ColumnMap
{
    private readonly Dictionary<ApprovalColumn, int> _indexes = [];

    public int? this[ApprovalColumn column] =>
        _indexes.TryGetValue(column, out var index) ? index : null;

    public IReadOnlyDictionary<ApprovalColumn, int> All => _indexes;

    internal void Add(ApprovalColumn column, int index) => _indexes[column] = index;

    internal bool IsTaken(int index) => _indexes.ContainsValue(index);
}

/// <summary>One extracted row, with the current text of each managed column.</summary>
public sealed record ApprovalTableRow(
    string TicketKey,
    int RowIndex,
    IReadOnlyDictionary<ApprovalColumn, string?> Values);

/// <summary>
/// Locates and edits the "IV. Approvals" table inside a page's ADF.
/// Everything here is surgical: nodes are mutated in place so the rest of the
/// document round-trips byte for byte.
/// </summary>
public static partial class ApprovalsTable
{
    /// <summary>
    /// Used when no project key is configured. The prefix is configuration
    /// (<c>Atlassian:TicketKeyPrefix</c>) rather than a constant, so a release
    /// page belonging to another project needs no code change.
    /// </summary>
    public const string DefaultTicketPrefix = "PROJECT";

    /// <summary>Any project key, used to read whatever a cell actually contains.</summary>
    [GeneratedRegex(@"\b([A-Z][A-Z0-9]*-\d+)\b")]
    private static partial Regex AnyKeyPattern { get; }

    /// <summary>
    /// Whether a ticket key belongs to the project in scope. Compared on the
    /// project part alone, so "PROJECT-12" matches "PROJECT" but "RELEASE-12" does not.
    /// </summary>
    public static bool IsInScope(string ticketKey, string ticketPrefix)
    {
        var dash = ticketKey.IndexOf('-');

        return dash > 0
            && ticketKey.AsSpan(dash + 1).Length > 0
            && ticketKey.AsSpan(0, dash).Equals(ticketPrefix, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }

    private const int DefaultDeveloperColumn = 1;

    /// <summary>
    /// Finds the table following the "Approvals" heading. Never index tables
    /// positionally - the page holds eight and their order moves per release.
    /// </summary>
    public static JsonObject? Locate(JsonNode document)
    {
        if (document["content"] is not JsonArray content)
        {
            return null;
        }

        var underApprovalsHeading = false;

        foreach (var node in content)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            switch (obj["type"]?.GetValue<string>())
            {
                case "heading":
                    // A later heading ends the section, so this both opens and closes.
                    underApprovalsHeading = AdfText.Flatten(obj)
                        .Contains("Approvals", StringComparison.OrdinalIgnoreCase);
                    break;

                case "table" when underApprovalsHeading:
                    return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps managed columns onto cell indexes by reading the header row. Exact
    /// header matches are taken first so that near-identical headers cannot
    /// steal each other's column.
    /// </summary>
    public static ColumnMap ResolveColumns(JsonObject table)
    {
        var map = new ColumnMap();
        var header = Rows(table)?.FirstOrDefault(row => row is not null && IsHeaderRow(row));

        if (header?["content"] is not JsonArray cells)
        {
            // No header to read: fall back to the documented developer position.
            map.Add(ApprovalColumn.DeveloperAssigned, DefaultDeveloperColumn);
            return map;
        }

        var texts = cells.Select(cell => Normalise(AdfText.Flatten(cell))).ToList();

        foreach (var (column, expected) in ApprovalColumns.Headers)
        {
            var index = texts.FindIndex(text => text == expected);

            if (index >= 0 && !map.IsTaken(index))
            {
                map.Add(column, index);
            }
        }

        foreach (var (column, expected) in ApprovalColumns.Headers)
        {
            if (map[column] is not null)
            {
                continue;
            }

            var index = texts.FindIndex(text => text.Contains(expected, StringComparison.Ordinal));

            if (index >= 0 && !map.IsTaken(index))
            {
                map.Add(column, index);
            }
        }

        // The developer column is the one thing this tool cannot work without.
        if (map[ApprovalColumn.DeveloperAssigned] is null)
        {
            var index = texts.FindIndex(text => text.Contains("developer", StringComparison.Ordinal));
            map.Add(ApprovalColumn.DeveloperAssigned, index >= 0 ? index : DefaultDeveloperColumn);
        }

        return map;
    }

    /// <summary>
    /// Extracts the in-scope rows. Header rows, and rows whose ticket belongs to
    /// another project, are dropped - a release page often lists both.
    /// </summary>
    public static IReadOnlyList<ApprovalTableRow> ExtractRows(
        JsonObject table,
        ColumnMap columns,
        string ticketPrefix = DefaultTicketPrefix)
    {
        var extracted = new List<ApprovalTableRow>();

        if (Rows(table) is not { } rows)
        {
            return extracted;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];

            if (row is null || IsHeaderRow(row) || row["content"] is not JsonArray cells || cells.Count == 0)
            {
                continue;
            }

            var key = ReadTicketKey(cells[0]);

            if (key is null || !IsInScope(key, ticketPrefix))
            {
                continue;
            }

            var values = new Dictionary<ApprovalColumn, string?>();

            foreach (var (column, index) in columns.All)
            {
                var text = index < cells.Count ? AdfText.Flatten(cells[index]).Trim() : null;
                values[column] = string.IsNullOrWhiteSpace(text) ? null : text;
            }

            extracted.Add(new ApprovalTableRow(key.ToUpperInvariant(), rowIndex, values));
        }

        return extracted;
    }

    /// <summary>Replaces a cell with a mention node.</summary>
    public static bool SetMention(JsonObject table, int rowIndex, int columnIndex, string accountId, string displayName) =>
        ReplaceCellContent(table, rowIndex, columnIndex, [
            new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "mention",
                        ["attrs"] = new JsonObject
                        {
                            ["id"] = accountId,
                            ["text"] = $"@{displayName}"
                        }
                    })
            }
        ]);

    /// <summary>Colour used when a column needs a lozenge but has none to copy.</summary>
    private const string AffirmativeColour = "green";

    /// <summary>
    /// Writes a word into a status column so it looks like the rows filled in by
    /// hand. Release pages keep a legend of the allowed values in the *header*
    /// cell, so the lozenge to copy is usually up there rather than in the data.
    /// </summary>
    public static bool SetStatusLike(JsonObject table, int rowIndex, int columnIndex, string text)
    {
        var lozenges = StatusNodesIn(table, columnIndex);

        // The same word is already used in this column - copy it whole, so the
        // wording and colour match the page's own convention exactly.
        var sameValue = lozenges.FirstOrDefault(node =>
            string.Equals(node["attrs"]?["text"]?.GetValue<string>(), text, StringComparison.OrdinalIgnoreCase));

        if (sameValue is not null)
        {
            var copy = sameValue.DeepClone().AsObject();

            // localId must be unique per node; drop it and let Confluence assign one.
            (copy["attrs"] as JsonObject)?.Remove("localId");

            return ReplaceCellContent(table, rowIndex, columnIndex, [Wrap(copy)]);
        }

        // The column uses lozenges, just not this word. Match the shape without
        // borrowing another value's colour: copying a red "Rejected" in order to
        // write "Approved" would say the opposite of what is meant.
        if (lozenges.Count > 0)
        {
            return ReplaceCellContent(table, rowIndex, columnIndex, [
                Wrap(new JsonObject
                {
                    ["type"] = "status",
                    ["attrs"] = new JsonObject
                    {
                        ["text"] = text,
                        ["color"] = AffirmativeColour
                    }
                })
            ]);
        }

        // No lozenge anywhere in the column, so it is a plain-text column.
        return ReplaceCellContent(table, rowIndex, columnIndex, [Paragraph(text)]);
    }

    private static JsonObject Wrap(JsonNode node) => new()
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray(node)
    };

    /// <summary>Empties a cell, leaving the empty paragraph a table cell needs.</summary>
    public static bool ClearCell(JsonObject table, int rowIndex, int columnIndex) =>
        ReplaceCellContent(table, rowIndex, columnIndex, [
            new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray() }
        ]);

    private static JsonObject Paragraph(string text) => new()
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
    };

    /// <summary>
    /// Every status lozenge in a column, header included - the header cell is
    /// where these pages list the allowed values.
    /// </summary>
    private static List<JsonObject> StatusNodesIn(JsonObject table, int columnIndex)
    {
        var found = new List<JsonObject>();

        foreach (var row in Rows(table) ?? [])
        {
            if (row?["content"] is JsonArray cells && columnIndex < cells.Count)
            {
                found.AddRange(FindNodes(cells[columnIndex], "status"));
            }
        }

        return found;
    }

    /// <summary>Only the cell's content array is replaced; its attrs survive.</summary>
    private static bool ReplaceCellContent(JsonObject table, int rowIndex, int columnIndex, JsonNode[] content)
    {
        if (Rows(table) is not { } rows
            || rowIndex < 0 || rowIndex >= rows.Count
            || rows[rowIndex]?["content"] is not JsonArray cells
            || columnIndex < 0 || columnIndex >= cells.Count
            || cells[columnIndex] is not JsonObject cell)
        {
            return false;
        }

        cell["content"] = new JsonArray(content);
        return true;
    }

    private static JsonArray? Rows(JsonObject table) => table["content"] as JsonArray;

    private static bool IsHeaderRow(JsonNode row) =>
        row["content"] is JsonArray cells
        && cells.Count > 0
        && cells.All(cell => cell?["type"]?.GetValue<string>() == "tableHeader");

    private static string Normalise(string value) =>
        Whitespace.Replace(value.Trim().ToLowerInvariant(), " ");

    /// <summary>
    /// The ticket lives in an inlineCard's URL, not as text - which is why this
    /// column looks empty in any plain-text rendering of the page.
    /// </summary>
    private static string? ReadTicketKey(JsonNode? cell)
    {
        if (FindNode(cell, "inlineCard", "blockCard", "embedCard")?["attrs"]?["url"]?.GetValue<string>() is { } url
            && AnyKeyPattern.Match(url) is { Success: true } fromUrl)
        {
            return fromUrl.Groups[1].Value;
        }

        // Some rows are typed by hand rather than pasted as a link.
        var match = AnyKeyPattern.Match(AdfText.Flatten(cell));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Depth-first search for every node of the given types.</summary>
    private static IEnumerable<JsonObject> FindNodes(JsonNode? node, params string[] types)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var found in FindNodes(item, types))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonObject obj:
                if (obj["type"]?.GetValue<string>() is { } type && types.Contains(type, StringComparer.Ordinal))
                {
                    yield return obj;
                }

                foreach (var found in FindNodes(obj["content"], types))
                {
                    yield return found;
                }

                break;
        }
    }

    /// <summary>Depth-first search for the first node of any of these types.</summary>
    private static JsonObject? FindNode(JsonNode? node, params string[] types)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (FindNode(item, types) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonObject obj:
                if (obj["type"]?.GetValue<string>() is { } type && types.Contains(type, StringComparer.Ordinal))
                {
                    return obj;
                }

                return FindNode(obj["content"], types);

            default:
                return null;
        }
    }
}
