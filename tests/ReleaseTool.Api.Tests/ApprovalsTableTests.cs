using System.Text.Json.Nodes;
using ReleaseTool.Api.Adf;
using ReleaseTool.Api.Tests.Infrastructure;

namespace ReleaseTool.Api.Tests;

/// <summary>
/// Exercises the ADF walking against a document shaped like the real release
/// page: several tables, an Approvals table that is not the first, tickets held
/// in inlineCard URLs rather than text, and an OTHER_PROJECT row that must be ignored.
/// </summary>
public class ApprovalsTableTests
{
    private static JsonObject Document() => SampleDocuments.Approvals();

    private static JsonObject Table() => ApprovalsTable.Locate(Document())!;

    [Fact]
    public void Locate_takes_the_table_after_the_approvals_heading_not_the_first_table()
    {
        var table = ApprovalsTable.Locate(Document());

        Assert.NotNull(table);
        Assert.Equal("approvals-table", table!["attrs"]?["localId"]?.GetValue<string>());
    }

    [Fact]
    public void Locate_returns_null_when_there_is_no_approvals_heading()
    {
        var document = (JsonObject)JsonNode.Parse("""
        { "type": "doc", "content": [
            { "type": "heading", "content": [ { "type": "text", "text": "I. Scope" } ] },
            { "type": "table", "content": [] } ] }
        """)!;

        Assert.Null(ApprovalsTable.Locate(document));
    }

    [Fact]
    public void Every_managed_column_is_found_from_the_header()
    {
        var columns = ApprovalsTable.ResolveColumns(Table());

        Assert.Equal(1, columns[ApprovalColumn.DeveloperAssigned]);
        Assert.Equal(2, columns[ApprovalColumn.RequestedBy]);
        Assert.Equal(3, columns[ApprovalColumn.PrApprovedBy]);
        Assert.Equal(4, columns[ApprovalColumn.PrApprovedStatus]);
        Assert.Equal(5, columns[ApprovalColumn.MergedToDeploymentBranch]);
    }

    /// <summary>
    /// "PR Approved By" and "PR Approved Status" share a prefix, so a loose
    /// contains-match would put both on whichever column came first.
    /// </summary>
    [Fact]
    public void Similar_headers_do_not_claim_each_others_column()
    {
        var columns = ApprovalsTable.ResolveColumns(Table());

        Assert.NotEqual(columns[ApprovalColumn.PrApprovedBy], columns[ApprovalColumn.PrApprovedStatus]);
    }

    [Fact]
    public void A_table_without_a_header_still_finds_the_developer_column()
    {
        var table = (JsonObject)JsonNode.Parse("""
        { "type": "table", "content": [
            { "type": "tableRow", "content": [
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [
                { "type": "inlineCard", "attrs": { "url": "https://x/browse/PROJECT-1" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] } ] } ] }
        """)!;

        var columns = ApprovalsTable.ResolveColumns(table);

        Assert.Equal(1, columns[ApprovalColumn.DeveloperAssigned]);
        Assert.Null(columns[ApprovalColumn.RequestedBy]);
    }

    [Fact]
    public void Extract_reads_keys_from_inline_cards_and_skips_assd_and_headers()
    {
        var table = Table();
        var rows = ApprovalsTable.ExtractRows(table, ApprovalsTable.ResolveColumns(table));

        Assert.Equal(["PROJECT-1814", "PROJECT-1835"], rows.Select(r => r.TicketKey));
        Assert.Null(rows[0].Values[ApprovalColumn.DeveloperAssigned]);
        Assert.Equal("Someone Else", rows[1].Values[ApprovalColumn.DeveloperAssigned]);
        Assert.Equal("APPROVED", rows[1].Values[ApprovalColumn.PrApprovedStatus]);
    }

    [Fact]
    public void SetMention_writes_a_mention_node_not_text()
    {
        var table = Table();
        var columns = ApprovalsTable.ResolveColumns(table);
        var row = ApprovalsTable.ExtractRows(table, columns).Single(r => r.TicketKey == "PROJECT-1814");

        Assert.True(ApprovalsTable.SetMention(
            table, row.RowIndex, columns[ApprovalColumn.DeveloperAssigned]!.Value,
            "0123456789abcdef01234567", "Alex Taylor"));

        var mention = table["content"]![row.RowIndex]!["content"]![1]!["content"]![0]!["content"]![0]!;

        Assert.Equal("mention", mention["type"]?.GetValue<string>());
        Assert.Equal("0123456789abcdef01234567", mention["attrs"]?["id"]?.GetValue<string>());
        Assert.Equal("@Alex Taylor", mention["attrs"]?["text"]?.GetValue<string>());
    }

    /// <summary>
    /// The page already uses status lozenges in that column, so a new value has
    /// to be a lozenge too rather than bare text sitting among pills.
    /// </summary>
    [Fact]
    public void SetStatusLike_copies_the_lozenge_already_used_in_the_column()
    {
        var table = Table();
        var columns = ApprovalsTable.ResolveColumns(table);
        var row = ApprovalsTable.ExtractRows(table, columns).Single(r => r.TicketKey == "PROJECT-1814");
        var column = columns[ApprovalColumn.PrApprovedStatus]!.Value;

        Assert.True(ApprovalsTable.SetStatusLike(table, row.RowIndex, column, "Approved"));

        var written = table["content"]![row.RowIndex]!["content"]![column]!["content"]![0]!["content"]![0]!;

        Assert.Equal("status", written["type"]?.GetValue<string>());
        Assert.Equal("green", written["attrs"]?["color"]?.GetValue<string>());

        // The page's own wording wins, so a run does not mix "Approved" rows in
        // among "APPROVED" ones.
        Assert.Equal("APPROVED", written["attrs"]?["text"]?.GetValue<string>());

        // localId must be unique per node, so the exemplar's is not copied.
        Assert.Null(written["attrs"]?["localId"]);
    }

    /// <summary>
    /// The real page lists the allowed values as lozenges in the header cell and
    /// leaves the data cells empty, so a data-rows-only search finds nothing and
    /// silently writes plain text into a column of pills.
    /// </summary>
    [Fact]
    public void SetStatusLike_finds_the_legend_lozenge_in_the_header()
    {
        var table = Table();
        var columns = ApprovalsTable.ResolveColumns(table);
        var row = ApprovalsTable.ExtractRows(table, columns).Single(r => r.TicketKey == "PROJECT-1814");
        var column = columns[ApprovalColumn.MergedToDeploymentBranch]!.Value;

        Assert.True(ApprovalsTable.SetStatusLike(table, row.RowIndex, column, "Merged"));

        var written = table["content"]![row.RowIndex]!["content"]![column]!["content"]![0]!["content"]![0]!;

        Assert.Equal("status", written["type"]?.GetValue<string>());
        Assert.Equal("MERGED", written["attrs"]?["text"]?.GetValue<string>());
        Assert.Equal("green", written["attrs"]?["color"]?.GetValue<string>());
    }

    /// <summary>
    /// Copying the nearest lozenge regardless of its text would write "Approved"
    /// in the red of "Rejected", stating the opposite of what is meant.
    /// </summary>
    [Fact]
    public void SetStatusLike_does_not_borrow_the_colour_of_a_different_value()
    {
        var table = (JsonObject)JsonNode.Parse("""
        { "type": "table", "content": [
            { "type": "tableRow", "content": [
              { "type": "tableHeader", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Ticket" } ] } ] },
              { "type": "tableHeader", "content": [
                { "type": "paragraph", "content": [ { "type": "text", "text": "PR Approved Status" } ] },
                { "type": "paragraph", "content": [
                  { "type": "status", "attrs": { "text": "REJECTED", "color": "red" } } ] } ] } ] },
            { "type": "tableRow", "content": [
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [
                { "type": "inlineCard", "attrs": { "url": "https://x/browse/PROJECT-1" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] } ] } ] }
        """)!;

        Assert.True(ApprovalsTable.SetStatusLike(table, 1, 1, "Approved"));

        var written = table["content"]![1]!["content"]![1]!["content"]![0]!["content"]![0]!;

        Assert.Equal("status", written["type"]?.GetValue<string>());
        Assert.Equal("Approved", written["attrs"]?["text"]?.GetValue<string>());
        Assert.NotEqual("red", written["attrs"]?["color"]?.GetValue<string>());
    }

    [Fact]
    public void SetStatusLike_falls_back_to_plain_text_when_the_column_has_no_lozenge()
    {
        var table = (JsonObject)JsonNode.Parse("""
        { "type": "table", "content": [
            { "type": "tableRow", "content": [
              { "type": "tableHeader", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Ticket" } ] } ] },
              { "type": "tableHeader", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Merged to Deployment Branch" } ] } ] } ] },
            { "type": "tableRow", "content": [
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [
                { "type": "inlineCard", "attrs": { "url": "https://x/browse/PROJECT-1" } } ] } ] },
              { "type": "tableCell", "content": [ { "type": "paragraph", "content": [] } ] } ] } ] }
        """)!;

        Assert.True(ApprovalsTable.SetStatusLike(table, 1, 1, "Merged"));

        var written = table["content"]![1]!["content"]![1]!["content"]![0]!["content"]![0]!;

        Assert.Equal("text", written["type"]?.GetValue<string>());
        Assert.Equal("Merged", written["text"]?.GetValue<string>());
    }

    [Fact]
    public void ClearCell_empties_the_cell_but_keeps_a_paragraph()
    {
        var table = Table();
        var columns = ApprovalsTable.ResolveColumns(table);
        var row = ApprovalsTable.ExtractRows(table, columns).Single(r => r.TicketKey == "PROJECT-1835");
        var column = columns[ApprovalColumn.DeveloperAssigned]!.Value;

        Assert.True(ApprovalsTable.ClearCell(table, row.RowIndex, column));

        var cell = table["content"]![row.RowIndex]!["content"]![column]!;

        Assert.Equal("paragraph", cell["content"]![0]!["type"]?.GetValue<string>());
        Assert.Empty(cell["content"]![0]!["content"]!.AsArray());
    }

    /// <summary>
    /// Formatting preservation is the hard requirement: the page history diff
    /// must show only the columns this tool writes.
    /// </summary>
    [Fact]
    public void SetMention_leaves_every_other_node_untouched()
    {
        var before = Document();
        var after = Document();

        var table = ApprovalsTable.Locate(after)!;
        var columns = ApprovalsTable.ResolveColumns(table);
        var target = ApprovalsTable.ExtractRows(table, columns).Single(r => r.TicketKey == "PROJECT-1814").RowIndex;

        ApprovalsTable.SetMention(table, target, columns[ApprovalColumn.DeveloperAssigned]!.Value, "acc-1", "Dev One");

        var beforeContent = before["content"]!.AsArray();
        var afterContent = after["content"]!.AsArray();

        // Every top-level node other than the Approvals table is byte-identical,
        // including the decoy table with its localId and colwidth.
        Assert.Equal(beforeContent.Count, afterContent.Count);

        for (var i = 0; i < beforeContent.Count; i++)
        {
            if (afterContent[i]?["attrs"]?["localId"]?.GetValue<string>() == "approvals-table")
            {
                continue;
            }

            Assert.Equal(beforeContent[i]!.ToJsonString(), afterContent[i]!.ToJsonString());
        }

        var beforeRows = beforeContent.Single(n => n?["attrs"]?["localId"]?.GetValue<string>() == "approvals-table")!["content"]!.AsArray();
        var afterRows = table["content"]!.AsArray();

        // Header row, the OTHER_PROJECT row and the other PROJECT row are all unchanged.
        for (var i = 0; i < beforeRows.Count; i++)
        {
            if (i == target)
            {
                continue;
            }

            Assert.Equal(beforeRows[i]!.ToJsonString(), afterRows[i]!.ToJsonString());
        }

        // Inside the changed row, only the developer cell moved.
        var beforeCells = beforeRows[target]!["content"]!.AsArray();
        var afterCells = afterRows[target]!["content"]!.AsArray();

        Assert.Equal(beforeCells.Count, afterCells.Count);
        Assert.NotEqual(beforeCells[1]!.ToJsonString(), afterCells[1]!.ToJsonString());

        for (var i = 0; i < beforeCells.Count; i++)
        {
            if (i == 1)
            {
                continue;
            }

            Assert.Equal(beforeCells[i]!.ToJsonString(), afterCells[i]!.ToJsonString());
        }

        // The cell's own attrs survive - only its content array is replaced.
        Assert.Equal("[220]", afterCells[0]!["attrs"]!["colwidth"]!.ToJsonString());
    }
}
