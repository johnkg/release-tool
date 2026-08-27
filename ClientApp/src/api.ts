import type { AppSettings, RepositoryRef } from './settings'

export type DeveloperSource = 'PullRequest' | 'Reference' | 'Defaulted'

export type ApprovalColumn =
  | 'DeveloperAssigned'
  | 'RequestedBy'
  | 'PrApprovedBy'
  | 'PrApprovedStatus'
  | 'MergedToDeploymentBranch'

export const COLUMN_LABELS: Record<ApprovalColumn, string> = {
  DeveloperAssigned: 'Developer Assigned',
  RequestedBy: 'Requested By',
  PrApprovedBy: 'PR Approved By',
  PrApprovedStatus: 'PR Approved Status',
  MergedToDeploymentBranch: 'Merged to Deployment Branch',
}

export interface Credentials {
  email: string
  token: string
}

/**
 * Whether the server already holds a credential. Never carries the credential
 * itself - a token in this response would be readable by any script on the
 * origin, which is exactly what keeping it server-side avoids.
 */
export interface StoredCredentialStatus {
  configured: boolean
  email: string | null
}

/**
 * What the Jira workflow calls the two release statuses and the resolution.
 * Read from the server so the buttons are labelled with the same words the
 * match will be made against, rather than a second copy that can drift.
 */
export interface ReleaseWorkflowNames {
  deployedToProduction: string
  readyForDeployment: string
  resolution: string
}

export interface ConfigResponse {
  atlassian: StoredCredentialStatus
  devOps: StoredCredentialStatus
  defaultSpaceKey: string
  workflow: ReleaseWorkflowNames

  /** The host machine's light/dark setting, and only for a local caller. */
  osTheme: string | null
}

/** Blank credentials are legitimate: the server falls back to its own. */
export const NO_CREDENTIALS: Credentials = { email: '', token: '' }

export interface VerifyResponse {
  accountId: string
  displayName: string
  email: string | null
}

export interface JiraUser {
  accountId: string
  displayName: string
  email: string | null
}

export interface UserSearchResponse {
  users: JiraUser[]
}

export interface PageListItem {
  pageId: string
  title: string
  createdAt: string | null
}

export interface PageListResponse {
  spaceId: string
  pages: PageListItem[]
}

export interface ApprovalRow {
  ticketKey: string
  rowIndex: number
  values: Partial<Record<ApprovalColumn, string | null>>
}

export interface ApprovalsResponse {
  pageId: string
  version: number
  availableColumns: ApprovalColumn[]
  rows: ApprovalRow[]
}

export interface DeveloperAssignment {
  ticketKey: string
  developerName: string
  accountId: string | null
  source: DeveloperSource
  reporterName: string | null
  reporterAccountId: string | null
}

export interface PullRequestRef {
  ticketKey: string
  url: string
  organization: string
  project: string
  repository: string
  pullRequestId: number
  commentAuthor: string
  commentedAt: string
}

export interface FixedByNote {
  ticketKey: string
  comment: string
  author: string
  commentedAt: string
}

export interface ResolveResponse {
  assignments: DeveloperAssignment[]
  pullRequests: PullRequestRef[]
  fixedByNotes: FixedByNote[]
}

export interface DevOpsPullRequest {
  ticketKey: string
  project: string
  repository: string
  pullRequestId: number
  title: string
  status: string
  author: string | null
  createdAt: string | null
  completedAt: string | null
  targetBranch: string | null
  webUrl: string

  /** Sign-in name, which the "only mine" filter matches on before the display name. */
  authorEmail: string | null
  sourceBranch: string | null

  /** Head of the source branch - the commit a replay merges in. Full SHA. */
  sourceCommit: string | null

  /** For a squashed PR this is the single commit it produced on its target. */
  mergeCommit: string | null
  squashed: boolean
  organization: string
}

/**
 * The commit to replay onto a deployment branch. A squashed pull request landed
 * as one commit, so that is what should be merged; using the source branch head
 * would bring in the individual commits the squash exists to collapse.
 */
export function replayCommit(pr: DevOpsPullRequest): string | null {
  return pr.squashed && pr.mergeCommit ? pr.mergeCommit : pr.sourceCommit
}

/**
 * Whether a pull request is the connected user's own.
 *
 * Matches on the sign-in name first: display names are not reliably the same
 * string in the profile API and on a repository identity. With no identity
 * resolved every pull request counts, so the filter disables itself rather than
 * silently hiding everything.
 */
export function isAuthoredBy(pr: DevOpsPullRequest, identity: DevOpsIdentity | null): boolean {
  if (identity === null) {
    return true
  }

  const email = identity.email?.trim().toLowerCase()
  const name = identity.displayName.trim().toLowerCase()

  return (
    (email !== undefined && pr.authorEmail?.trim().toLowerCase() === email) ||
    pr.author?.trim().toLowerCase() === name
  )
}

export interface DevOpsRepositoryGroup {
  repository: string
  pullRequests: DevOpsPullRequest[]
}

export interface DevOpsLookupFailure {
  ticketKey: string
  url: string
  reason: string
}

export interface DevOpsLookupResponse {
  repositories: DevOpsRepositoryGroup[]
  failures: DevOpsLookupFailure[]
}

export interface PrApproval {
  displayName: string
  accountId: string
  ticketKeys: string[]
}

export interface ApplyRequest {
  expectedVersion: number
  assignments: DeveloperAssignment[]
  writeDeveloper: boolean
  writeRequestedBy: boolean
  prApproval: PrApproval | null
}

export interface ApplyResponse {
  pageId: string
  newVersion: number
  cellsUpdated: number
}

// ---- Jira ticket status ----------------------------------------------------

/** Which of the two release states is meant; the names come from the server. */
export type ReleaseStatus = 'DeployedToProduction' | 'ReadyForDeployment'

/**
 * `status` is null when Jira did not return the ticket at all; `resolution` is
 * null when the ticket is unresolved — "Unresolved" is the absence of a value,
 * not a value.
 */
export interface TicketStatus {
  ticketKey: string
  status: string | null
  resolution: string | null
}

export interface TicketStatusResponse {
  tickets: TicketStatus[]
}

export interface TransitionResult {
  ticketKey: string
  fromStatus: string | null
  success: boolean

  /** Already in the target status, so nothing was sent to Jira. */
  unchanged: boolean
  message: string
}

export interface TransitionResponse {
  results: TransitionResult[]
}

/** What happened to one ticket's resolution. Null resolution means unresolved. */
export interface ResolutionOutcome {
  ticketKey: string
  resolution: string | null
  success: boolean
  unchanged: boolean
  message: string
}

export interface ResolutionResponse {
  results: ResolutionOutcome[]
}

/** Both operations report per ticket, and the table shows either the same way. */
export type RunOutcome = TransitionResult | ResolutionOutcome

// ---- Deployment branches ---------------------------------------------------

export interface BranchRequest {
  branchName: string
  sourceBranch: string
  repositories: RepositoryRef[]
}

export interface BranchResult {
  repository: string
  project: string
  success: boolean
  message: string
}

export interface BranchOperationResponse {
  results: BranchResult[]
}

export interface BranchStatus {
  repository: string
  project: string
  exists: boolean
  sourceExists: boolean
  error: string | null
}

export interface BranchStatusResponse {
  repositories: BranchStatus[]
}

export interface DevOpsIdentity {
  displayName: string
  email: string | null
}

// ---- Changed files ---------------------------------------------------------

export interface ChangedFile {
  path: string
  ticketKeys: string[]
  pullRequestIds: number[]
  overlapping: boolean
}

export interface RepositoryChanges {
  repository: string
  project: string
  files: ChangedFile[]
  hasOverlap: boolean
}

export interface ChangedFilesResponse {
  repositories: RepositoryChanges[]
  failures: DevOpsLookupFailure[]
}

// ---- Merging ---------------------------------------------------------------

export interface MergeCandidate {
  pullRequestId: number
  ticketKey: string
  sourceCommit: string
  sourceBranch: string | null
}

export interface MergeRequest {
  repository: RepositoryRef
  targetBranch: string
  pullRequests: MergeCandidate[]
}

export interface MergeResult {
  pullRequestId: number
  ticketKey: string
  success: boolean
  message: string
  commitId: string | null
}

export interface MergeResponse {
  results: MergeResult[]
}

/** Shape of the ProblemDetails the API returns on failure. */
interface Problem {
  title?: string
  detail?: string
}

async function request<T>(path: string, credentials: Credentials, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = {}

  // Omitted rather than sent empty, so the server falls back to its configured
  // credentials. Sending a credential always overrides one held server-side.
  if (credentials.email && credentials.token) {
    headers['X-Atlassian-Email'] = credentials.email
    headers['X-Atlassian-Token'] = credentials.token
  }

  if (init?.body) {
    headers['Content-Type'] = 'application/json'
  }

  const response = await fetch(path, { ...init, headers })
  const text = await response.text()

  if (!response.ok) {
    throw new Error(describe(response.status, text))
  }

  return text ? (JSON.parse(text) as T) : (undefined as T)
}

/** The API's problem responses carry the actionable message, so prefer them. */
function describe(status: number, body: string): string {
  try {
    const problem = JSON.parse(body) as Problem

    if (problem.title) {
      return problem.detail ? `${problem.title} (${problem.detail})` : problem.title
    }
  } catch {
    // Not JSON - fall through to the status.
  }

  return `Request failed with status ${status}.`
}

/**
 * Azure DevOps calls use their own personal access token, not the Atlassian
 * one, so they carry a different header entirely.
 */
async function devOpsRequest<T>(path: string, personalAccessToken: string, body: unknown): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }

  // Same rule as the Atlassian headers: omitted means "use the server's PAT".
  if (personalAccessToken) {
    headers['X-DevOps-Token'] = personalAccessToken
  }

  const response = await fetch(path, { method: 'POST', headers, body: JSON.stringify(body) })
  const text = await response.text()

  if (!response.ok) {
    throw new Error(describe(response.status, text))
  }

  return JSON.parse(text) as T
}

function spaceQuery(spaceKey: string): string {
  const query = new URLSearchParams()

  if (spaceKey.trim()) {
    query.set('spaceKey', spaceKey.trim())
  }

  return query.toString() ? `?${query}` : ''
}

export const api = {
  /** Readable without credentials - it is what tells the UI whether it needs any. */
  config: async (): Promise<ConfigResponse> => {
    const response = await fetch('/api/config')
    const text = await response.text()

    if (!response.ok) {
      throw new Error(describe(response.status, text))
    }

    return JSON.parse(text) as ConfigResponse
  },

  verify: (credentials: Credentials) =>
    request<VerifyResponse>('/api/auth/verify', credentials, { method: 'POST' }),

  /** Type-ahead for the approver picker. Fewer than 2 characters returns nothing. */
  users: (credentials: Credentials, query: string) =>
    request<UserSearchResponse>(
      `/api/jira/users?query=${encodeURIComponent(query)}`,
      credentials,
    ),

  /** Newest page first, so the default selection is the latest release page. */
  pages: (credentials: Credentials, spaceKey: string) =>
    request<PageListResponse>(`/api/confluence/pages${spaceQuery(spaceKey)}`, credentials),

  approvals: (credentials: Credentials, pageId: string) =>
    request<ApprovalsResponse>(`/api/approvals/${pageId}`, credentials),

  resolve: (credentials: Credentials, pageId: string, ticketKeys: string[]) =>
    request<ResolveResponse>(`/api/approvals/${pageId}/resolve`, credentials, {
      method: 'POST',
      body: JSON.stringify({ ticketKeys }),
    }),

  apply: (credentials: Credentials, pageId: string, body: ApplyRequest) =>
    request<ApplyResponse>(`/api/approvals/${pageId}/apply`, credentials, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  clear: (
    credentials: Credentials,
    pageId: string,
    expectedVersion: number,
    columns: ApprovalColumn[],
  ) =>
    request<ApplyResponse>(`/api/approvals/${pageId}/clear`, credentials, {
      method: 'POST',
      body: JSON.stringify({ expectedVersion, columns }),
    }),

  /** Read-only: where each ticket sits in the Jira workflow right now. */
  ticketStatuses: (credentials: Credentials, ticketKeys: string[]) =>
    request<TicketStatusResponse>('/api/jira/statuses', credentials, {
      method: 'POST',
      body: JSON.stringify({ ticketKeys }),
    }),

  /** Moves tickets between the two release statuses. Reports per ticket. */
  transition: (credentials: Credentials, ticketKeys: string[], target: ReleaseStatus) =>
    request<TransitionResponse>('/api/jira/transition', credentials, {
      method: 'POST',
      body: JSON.stringify({ ticketKeys, target }),
    }),

  /**
   * Sets or clears the resolution without touching the status. `resolved: false`
   * clears the field, which is what Unresolved actually is.
   */
  resolution: (credentials: Credentials, ticketKeys: string[], resolved: boolean) =>
    request<ResolutionResponse>('/api/jira/resolution', credentials, {
      method: 'POST',
      body: JSON.stringify({ ticketKeys, resolved }),
    }),

  /**
   * Azure DevOps uses its own personal access token, not the Atlassian one, so
   * this call carries a different header entirely.
   */
  devOpsPullRequests: (personalAccessToken: string, pullRequests: PullRequestRef[]) =>
    devOpsRequest<DevOpsLookupResponse>('/api/devops/pull-requests', personalAccessToken, {
      pullRequests,
    }),

  /** Every file the release touches, labelled with the tickets behind it. */
  changedFiles: (personalAccessToken: string, pullRequests: PullRequestRef[]) =>
    devOpsRequest<ChangedFilesResponse>('/api/devops/changed-files', personalAccessToken, {
      pullRequests,
    }),

  /** Replays pull requests onto a branch, in order, stopping at the first failure. */
  merge: (personalAccessToken: string, body: MergeRequest) =>
    devOpsRequest<MergeResponse>('/api/devops/merge', personalAccessToken, body),

  /** Who the Azure DevOps token acts as. */
  devOpsMe: async (): Promise<DevOpsIdentity> => {
    const response = await fetch('/api/devops/me')
    const text = await response.text()

    if (!response.ok) {
      throw new Error(describe(response.status, text))
    }

    return JSON.parse(text) as DevOpsIdentity
  },

  /** Read-only: which repositories already have the branch. */
  branchStatus: (personalAccessToken: string, body: BranchRequest) =>
    devOpsRequest<BranchStatusResponse>('/api/devops/branches/status', personalAccessToken, body),

  createBranches: (personalAccessToken: string, body: BranchRequest) =>
    devOpsRequest<BranchOperationResponse>('/api/devops/branches', personalAccessToken, body),

  deleteBranches: (personalAccessToken: string, body: BranchRequest) =>
    devOpsRequest<BranchOperationResponse>('/api/devops/branches/delete', personalAccessToken, body),

  /** Settings carry no secrets, so they need no credentials either way. */
  settings: async (): Promise<AppSettings> => {
    const response = await fetch('/api/settings')
    const text = await response.text()

    if (!response.ok) {
      throw new Error(describe(response.status, text))
    }

    return JSON.parse(text) as AppSettings
  },

  /** Returns the settings as stored, which is what the UI should then show. */
  saveSettings: async (settings: AppSettings): Promise<AppSettings> => {
    const response = await fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settings),
    })

    const text = await response.text()

    if (!response.ok) {
      throw new Error(describe(response.status, text))
    }

    return JSON.parse(text) as AppSettings
  },
}
