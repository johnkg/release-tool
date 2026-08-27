using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ReleaseTool.Api.Atlassian;
using ReleaseTool.Api.Configuration;
using ReleaseTool.Api.Contracts;

namespace ReleaseTool.Api.Jira;

/// <summary>Everything one resolve run produced.</summary>
public sealed record ResolutionResult(
    IReadOnlyList<DeveloperAssignment> Assignments,
    IReadOnlyList<PullRequestRef> PullRequests,
    IReadOnlyList<FixedByNote> FixedByNotes);

/// <summary>
/// Works out who fixed each ticket from its comments, and collects the pull
/// request links found along the way.
/// </summary>
public sealed partial class DeveloperResolver(
    JiraService jira,
    IOptions<AtlassianOptions> options,
    ILogger<DeveloperResolver> logger)
{
    /// <summary>
    /// Handles both the legacy '{org}.visualstudio.com' host and the current
    /// 'dev.azure.com/{org}' form, since older tickets carry the old links.
    /// </summary>
    [GeneratedRegex(
        @"https://(?:(?<org1>[\w.-]+)\.visualstudio\.com|dev\.azure\.com/(?<org2>[\w.-]+))/(?<project>[^/\s]+)/_git/(?<repo>[^/\s]+)/pullrequest/(?<id>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PullRequestPattern { get; }

    [GeneratedRegex(@"fixed on|included in|prs:", RegexOptions.IgnoreCase)]
    private static partial Regex ReferencePhrasePattern { get; }

    [GeneratedRegex(@"\b([A-Z][A-Z0-9]*-\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TicketKeyPattern { get; }

    public async Task<ResolutionResult> ResolveAsync(
        IReadOnlyList<string> ticketKeys,
        AtlassianCredentials credentials,
        CancellationToken ct)
    {
        var issues = await jira.GetIssuesAsync(ticketKeys, credentials, ct);

        var assignments = new List<DeveloperAssignment>(ticketKeys.Count);
        var pullRequests = new List<PullRequestRef>();
        var fixedByNotes = new List<FixedByNote>();

        string? fallbackAccountId = null;
        var fallbackName = options.Value.FallbackDeveloperName;

        foreach (var key in ticketKeys)
        {
            var issue = issues.GetValueOrDefault(key);
            var comments = issue?.Comments ?? [];

            var found = CollectPullRequests(key, comments);
            pullRequests.AddRange(found);

            var reference = FindReferenceComment(key, comments);

            // No pull request of its own - the comment is the only record of
            // where the work actually happened.
            if (found.Count == 0 && reference is not null)
            {
                fixedByNotes.Add(new FixedByNote(
                    key, Condense(reference.Text), reference.AuthorName, reference.Created));
            }

            var match = Derive(key, comments, found, reference);

            if (match is null)
            {
                // Resolved once, then reused for every defaulted ticket.
                fallbackAccountId ??= await jira.FindAccountIdAsync(fallbackName, credentials, ct);

                logger.LogInformation("{Key} has no PR or reference comment; defaulting to {Developer}", key, fallbackName);

                match = new DeveloperAssignment(key, fallbackName, fallbackAccountId, DeveloperSource.Defaulted);
            }

            assignments.Add(match with
            {
                ReporterName = issue?.Reporter?.DisplayName,
                ReporterAccountId = issue?.Reporter?.AccountId
            });
        }

        return new ResolutionResult(assignments, pullRequests, fixedByNotes);
    }

    /// <summary>Every pull request link on the ticket, in comment order.</summary>
    private static List<PullRequestRef> CollectPullRequests(string ticketKey, IReadOnlyList<JiraComment> comments)
    {
        var found = new List<PullRequestRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var comment in comments)
        {
            foreach (Match match in PullRequestPattern.Matches(comment.Text))
            {
                // The same PR is often pasted more than once in a thread.
                if (!seen.Add(match.Value))
                {
                    continue;
                }

                var organization = match.Groups["org1"].Success
                    ? match.Groups["org1"].Value
                    : match.Groups["org2"].Value;

                found.Add(new PullRequestRef(
                    TicketKey: ticketKey,
                    Url: match.Value,
                    Organization: organization,
                    Project: Uri.UnescapeDataString(match.Groups["project"].Value),
                    Repository: Uri.UnescapeDataString(match.Groups["repo"].Value),
                    PullRequestId: int.Parse(match.Groups["id"].Value),
                    CommentAuthor: comment.AuthorName,
                    CommentedAt: comment.Created));
            }
        }

        return found;
    }

    private JiraComment? FindReferenceComment(string ticketKey, IReadOnlyList<JiraComment> comments) =>
        comments.FirstOrDefault(c =>
            ReferencePhrasePattern.IsMatch(c.Text) && ReferencesAnotherTicket(c.Text, ticketKey));

    private DeveloperAssignment? Derive(
        string ticketKey,
        IReadOnlyList<JiraComment> comments,
        IReadOnlyList<PullRequestRef> pullRequests,
        JiraComment? reference)
    {
        // Primary: whoever posted the pull request link did the work.
        if (pullRequests.Count > 0)
        {
            var author = pullRequests[0].CommentAuthor;

            WarnIfAmbiguous(ticketKey, pullRequests);

            var comment = comments.First(c => c.AuthorName == author && PullRequestPattern.IsMatch(c.Text));

            return new DeveloperAssignment(
                ticketKey, comment.AuthorName, comment.AuthorAccountId, DeveloperSource.PullRequest);
        }

        // Secondary: "Fixed on PROJECT-1834" / "Prs: Included in PROJECT-1853". The key
        // sits inside a smartlink, so it can be far downstream of the phrase -
        // presence in the same comment is the test, not proximity.
        return reference is null
            ? null
            : new DeveloperAssignment(
                ticketKey, reference.AuthorName, reference.AuthorAccountId, DeveloperSource.Reference);
    }

    private static bool ReferencesAnotherTicket(string text, string ticketKey) =>
        TicketKeyPattern.Matches(text)
            .Any(m => !string.Equals(m.Groups[1].Value, ticketKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Comment bodies keep their line breaks; a table cell does not want them.</summary>
    private static string Condense(string text) =>
        string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    private void WarnIfAmbiguous(string ticketKey, IReadOnlyList<PullRequestRef> pullRequests)
    {
        var authors = pullRequests
            .Select(pr => pr.CommentAuthor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (authors.Count > 1)
        {
            logger.LogWarning(
                "{Key} has PR comments from {Count} people ({Authors}); taking the earliest",
                ticketKey, authors.Count, string.Join(", ", authors));
        }
    }
}
