using ReleaseTool.Api.Adf;

namespace ReleaseTool.Api.Contracts;

/// <summary>Who the supplied token belongs to.</summary>
public sealed record VerifyResponse(string AccountId, string DisplayName, string? Email);

/// <summary>
/// Whether the server holds a credential of its own - never the credential
/// itself. <paramref name="Email"/> is the account the tool will act as, which
/// the UI has to show: it is the name that lands in the page history.
/// </summary>
public sealed record StoredCredentialStatus(bool Configured, string? Email);

/// <summary>
/// What the Jira workflow calls the things the tool moves tickets between: the
/// two release statuses, and the resolution a live release carries. Configured
/// rather than compiled in, and reported to the UI so the buttons are labelled
/// with the very words the match will be made against.
/// </summary>
public sealed record ReleaseWorkflowNames(
    string DeployedToProduction,
    string ReadyForDeployment,
    string Resolution);

/// <summary>
/// What the UI needs before it can ask for anything. Deliberately readable
/// without credentials, and deliberately free of secrets.
/// </summary>
public sealed record ConfigResponse(
    StoredCredentialStatus Atlassian,
    StoredCredentialStatus DevOps,
    string DefaultSpaceKey,
    ReleaseWorkflowNames Workflow,

    /// <summary>
    /// The host machine's light/dark setting, and only when the caller is on
    /// that same machine. Null otherwise - see <see cref="Configuration.OsTheme"/>.
    /// </summary>
    string? OsTheme = null);

/// <summary>A located Confluence page. Version is needed for the write-back.</summary>
public sealed record PageSummary(string PageId, string Title, string SpaceId, int Version);

/// <summary>An Atlassian account offered in the approver picker.</summary>
public sealed record JiraUser(string AccountId, string DisplayName, string? Email);

public sealed record UserSearchResponse(IReadOnlyList<JiraUser> Users);

/// <summary>A page in the space picker, newest first.</summary>
public sealed record PageListItem(string PageId, string Title, DateTimeOffset? CreatedAt);

public sealed record PageListResponse(string SpaceId, IReadOnlyList<PageListItem> Pages);

/// <summary>How a developer was arrived at. Drives the UI highlight.</summary>
public enum DeveloperSource
{
    /// <summary>Author of a comment containing an Azure DevOps PR URL.</summary>
    PullRequest,

    /// <summary>Author of a "fixed on" / "included in" comment pointing at another ticket.</summary>
    Reference,

    /// <summary>No PR and no reference - fell back to the configured developer.</summary>
    Defaulted
}

/// <summary>
/// One row of the Approvals table. <paramref name="RowIndex"/> is the row's
/// position in the ADF table node, so apply can target it without re-matching.
/// </summary>
public sealed record ApprovalRow(
    string TicketKey,
    int RowIndex,
    IReadOnlyDictionary<ApprovalColumn, string?> Values);

/// <summary>The Approvals table as read from the page.</summary>
public sealed record ApprovalsResponse(
    string PageId,
    int Version,
    IReadOnlyList<ApprovalColumn> AvailableColumns,
    IReadOnlyList<ApprovalRow> Rows);

public sealed record ResolveRequest(IReadOnlyList<string> TicketKeys);

/// <summary>
/// A ticket mapped to the people who will be mentioned. The reporter comes
/// straight off the Jira issue rather than being derived.
/// </summary>
public sealed record DeveloperAssignment(
    string TicketKey,
    string DeveloperName,
    string? AccountId,
    DeveloperSource Source,
    string? ReporterName = null,
    string? ReporterAccountId = null);

/// <summary>
/// A pull request found on a ticket's comments. Everything here comes from the
/// URL and the comment, so it is available without calling Azure DevOps.
/// </summary>
public sealed record PullRequestRef(
    string TicketKey,
    string Url,
    string Organization,
    string Project,
    string Repository,
    int PullRequestId,
    string CommentAuthor,
    DateTimeOffset CommentedAt);

/// <summary>
/// A ticket fixed elsewhere: no pull request of its own, just a comment saying
/// where the work happened.
/// </summary>
public sealed record FixedByNote(
    string TicketKey,
    string Comment,
    string Author,
    DateTimeOffset CommentedAt);

public sealed record ResolveResponse(
    IReadOnlyList<DeveloperAssignment> Assignments,
    IReadOnlyList<PullRequestRef> PullRequests,
    IReadOnlyList<FixedByNote> FixedByNotes);

/// <summary>
/// Records a PR approval against specific tickets. The caller decides which
/// rows qualify, so the preview and the write cannot disagree.
/// </summary>
public sealed record PrApproval(
    string DisplayName,
    string AccountId,
    IReadOnlyList<string> TicketKeys);

/// <summary>
/// Apply is a separate, explicit step. <paramref name="ExpectedVersion"/> is the
/// version the user previewed; a mismatch means someone edited the page since.
/// </summary>
public sealed record ApplyRequest(
    int ExpectedVersion,
    IReadOnlyList<DeveloperAssignment> Assignments,
    bool WriteDeveloper = true,
    bool WriteRequestedBy = true,
    PrApproval? PrApproval = null);

public sealed record ApplyResponse(string PageId, int NewVersion, int CellsUpdated);

/// <summary>Empties the chosen columns on every PROJECT row. Intended for testing.</summary>
public sealed record ClearRequest(int ExpectedVersion, IReadOnlyList<ApprovalColumn> Columns);

// ---- Jira ticket status ----------------------------------------------------

/// <summary>
/// Which of the two release states is meant. The status <em>names</em> live in
/// configuration, since a workflow can be renamed without a rebuild, so this
/// carries only the intent.
/// </summary>
public enum ReleaseStatus
{
    /// <summary>The release is live.</summary>
    DeployedToProduction,

    /// <summary>Back to waiting for a deployment - undoes the above.</summary>
    ReadyForDeployment
}

/// <summary>
/// Where one ticket sits right now. <paramref name="Status"/> is null when Jira
/// did not return the ticket at all; <paramref name="Resolution"/> is null when
/// the ticket is unresolved, which is the far more common case - "Unresolved"
/// is not a resolution value, it is the absence of one.
/// </summary>
public sealed record TicketStatus(string TicketKey, string? Status, string? Resolution);

public sealed record TicketStatusRequest(IReadOnlyList<string> TicketKeys);

public sealed record TicketStatusResponse(IReadOnlyList<TicketStatus> Tickets);

/// <summary>
/// Moves the given tickets into one of the two release statuses. The caller
/// sends the keys it previewed - the same rule the PR approval follows - so the
/// preview and the write cannot disagree about which tickets are in scope.
/// </summary>
public sealed record TransitionRequest(IReadOnlyList<string> TicketKeys, ReleaseStatus Target);

/// <summary>
/// What happened to one ticket. Failures are per ticket and never abort the
/// batch: tickets are independent, so one refusal must not strand the rest.
/// </summary>
public sealed record TransitionResult(
    string TicketKey,
    string? FromStatus,
    bool Success,

    /// <summary>Already in the target status, so nothing was sent to Jira.</summary>
    bool Unchanged,

    string Message);

public sealed record TransitionResponse(IReadOnlyList<TransitionResult> Results);

/// <summary>
/// Sets or clears the resolution without touching the status.
/// <paramref name="Resolved"/> false means <em>Unresolved</em>, which is a
/// cleared field rather than a value named "Unresolved".
/// </summary>
public sealed record ResolutionRequest(IReadOnlyList<string> TicketKeys, bool Resolved);

/// <summary>
/// What happened to one ticket's resolution. Named an outcome rather than a
/// result because <see cref="Jira.ResolutionResult"/> already means the very
/// different "who resolved as the developer".
/// </summary>
public sealed record ResolutionOutcome(
    string TicketKey,

    /// <summary>The resolution after the run. Null means unresolved.</summary>
    string? Resolution,

    bool Success,

    /// <summary>Already where it was asked to be, so nothing was sent.</summary>
    bool Unchanged,

    string Message);

public sealed record ResolutionResponse(IReadOnlyList<ResolutionOutcome> Results);

// ---- DevOps tab ------------------------------------------------------------

public sealed record DevOpsLookupRequest(IReadOnlyList<PullRequestRef> PullRequests);

/// <summary>A pull request as Azure DevOps reports it.</summary>
public sealed record DevOpsPullRequest(
    string TicketKey,
    string Project,
    string Repository,
    int PullRequestId,
    string Title,
    string Status,
    string? Author,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? CompletedAt,
    string? TargetBranch,
    string WebUrl,

    /// <summary>
    /// The author's sign-in name. Display names are not reliably comparable
    /// between the profile API and a repository identity, so this is what the
    /// "only mine" filter matches on first.
    /// </summary>
    string? AuthorEmail = null,

    string? SourceBranch = null,

    /// <summary>
    /// Head of the source branch: the commit a replay onto a deployment branch
    /// merges in. Full SHA, never shortened - the UI decides how much to show.
    /// </summary>
    string? SourceCommit = null,

    /// <summary>
    /// The commit this PR produced on its own target. For a PR completed with
    /// squash this is the single squashed commit, which is what should be
    /// replayed rather than the source branch's individual commits.
    /// </summary>
    string? MergeCommit = null,

    /// <summary>Whether the pull request was completed with a squash merge.</summary>
    bool Squashed = false,

    /// <summary>Organisation, kept so later calls can rebuild the API address.</summary>
    string Organization = "");

/// <summary>One repository's pull requests, oldest completion first.</summary>
public sealed record DevOpsRepositoryGroup(
    string Repository,
    IReadOnlyList<DevOpsPullRequest> PullRequests);

/// <summary>A pull request that could not be read, and why.</summary>
public sealed record DevOpsLookupFailure(string TicketKey, string Url, string Reason);

public sealed record DevOpsLookupResponse(
    IReadOnlyList<DevOpsRepositoryGroup> Repositories,
    IReadOnlyList<DevOpsLookupFailure> Failures);

// ---- Settings --------------------------------------------------------------

/// <summary>
/// One repository the tool can act on. Organisation and project are carried per
/// repository rather than globally, so a repo living somewhere else still works;
/// the UI fills them from the configured defaults when adding one.
/// </summary>
public sealed record RepositoryRef(string Organization, string Project, string Name);

/// <summary>
/// The branch each environment deploys from. PROD is the branch a new
/// deployment branch is cut from; the other three only filter the DevOps list.
/// </summary>
public sealed record DeploymentBranches(
    string Dev = "",
    string Sit = "",
    string Uat = "",
    string Prod = "");

/// <summary>
/// Everything the Settings tab owns. Persisted as a JSON file - no database, and
/// no secrets: tokens live in the Credentials section, never here.
/// </summary>
public sealed record AppSettings(
    DeploymentBranches Branches,
    string BranchNameFormat,
    string DefaultOrganization,
    string DefaultProject,
    IReadOnlyList<RepositoryRef> Repositories,
    string CandidateBranchNameFormat = AppSettings.DefaultCandidateBranchNameFormat)
{
    /// <summary>The format the team uses today, and the shape of the tokens.</summary>
    public const string DefaultBranchNameFormat = "dev/release/feat/PROJECT-RELEASE-{DDMMYYYY}";

    /// <summary>
    /// The candidate branch is cut from the deployment branch, so its name is
    /// built from that one - hence the extra {DEPLOYMENT} token.
    /// </summary>
    public const string DefaultCandidateBranchNameFormat = "{DEPLOYMENT}-candidate";

    public static AppSettings Empty { get; } = new(
        new DeploymentBranches(),
        DefaultBranchNameFormat,
        DefaultOrganization: "your-organization",
        DefaultProject: "Platform",
        Repositories: [],
        DefaultCandidateBranchNameFormat);
}

// ---- Deployment branches ---------------------------------------------------

/// <summary>
/// Create or delete one branch across many repositories.
/// <paramref name="SourceBranch"/> is ignored when deleting.
/// </summary>
public sealed record BranchRequest(
    string BranchName,
    string SourceBranch,
    IReadOnlyList<RepositoryRef> Repositories);

/// <summary>
/// What happened in one repository. Failures are per repository and never abort
/// the rest - one locked-down repo must not strand the others half-done.
/// </summary>
public sealed record BranchResult(
    string Repository,
    string Project,
    bool Success,
    string Message);

public sealed record BranchOperationResponse(IReadOnlyList<BranchResult> Results);

/// <summary>
/// Whether the branch already exists in each repository, and whether the source
/// branch it would be cut from exists at all.
/// </summary>
public sealed record BranchStatus(
    string Repository,
    string Project,
    bool Exists,
    bool SourceExists,
    string? Error);

public sealed record BranchStatusResponse(IReadOnlyList<BranchStatus> Repositories);

// ---- Who the PAT belongs to ------------------------------------------------

/// <summary>The Azure DevOps account the configured PAT acts as.</summary>
public sealed record DevOpsIdentity(string DisplayName, string? Email);

// ---- Changed files ---------------------------------------------------------

/// <summary>
/// One file in a release, and every ticket that touched it. More than one ticket
/// on the same file is what makes merging the repository in one go unsafe.
/// </summary>
public sealed record ChangedFile(
    string Path,
    IReadOnlyList<string> TicketKeys,
    IReadOnlyList<int> PullRequestIds,
    bool Overlapping);

public sealed record RepositoryChanges(
    string Repository,
    string Project,
    IReadOnlyList<ChangedFile> Files,
    bool HasOverlap);

public sealed record ChangedFilesResponse(
    IReadOnlyList<RepositoryChanges> Repositories,
    IReadOnlyList<DevOpsLookupFailure> Failures);

// ---- Merging into a deployment or candidate branch --------------------------

/// <summary>
/// One pull request to replay. <paramref name="SourceCommit"/> is the head of
/// the PR's source branch, taken from the lookup so this never has to re-read
/// the pull request.
/// </summary>
public sealed record MergeCandidate(
    int PullRequestId,
    string TicketKey,
    string SourceCommit,
    string? SourceBranch);

/// <summary>
/// Merges are done in the order given and stop at the first failure: each merge
/// moves the target branch, so everything after a conflict would be built on a
/// head that no longer exists.
/// </summary>
public sealed record MergeRequest(
    RepositoryRef Repository,
    string TargetBranch,
    IReadOnlyList<MergeCandidate> PullRequests);

public sealed record MergeResult(
    int PullRequestId,
    string TicketKey,
    bool Success,
    string Message,
    string? CommitId);

public sealed record MergeResponse(IReadOnlyList<MergeResult> Results);
