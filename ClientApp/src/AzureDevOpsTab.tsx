import { useMemo, useState } from 'react'
import {
  api,
  isAuthoredBy,
  type ChangedFilesResponse,
  type ConfigResponse,
  type DevOpsIdentity,
  type DevOpsLookupResponse,
  type DevOpsPullRequest,
  type FixedByNote,
  type PullRequestRef,
} from './api'
import Sha from './Sha'
import {
  DEFAULT_ENVIRONMENT,
  FILTER_ENVIRONMENTS,
  UNFILTERED_REPOSITORY,
  configuredBranches,
  sameBranch,
  type DeploymentBranches,
  type Environment,
} from './settings'

interface Props {
  pullRequests: PullRequestRef[]
  fixedByNotes: FixedByNote[]
  resolved: boolean
  branches: DeploymentBranches
  config: ConfigResponse | null

  /** Held by App, so the Deployment tab can see which repos are in the release. */
  result: DevOpsLookupResponse | null
  onResult: (result: DevOpsLookupResponse | null) => void

  /** Also held by App - the Deployment tab gates its bulk merge on this. */
  changes: ChangedFilesResponse | null
  onChanges: (changes: ChangedFilesResponse | null) => void

  identity: DevOpsIdentity | null

  /** Held by App too, so the Deployment tab shows the same scope. */
  onlyMine: boolean
  onOnlyMineChange: (onlyMine: boolean) => void
}

type Selection = Environment | 'all'

/**
 * Reads the pull requests found on the release's Jira tickets, grouped by
 * repository and ordered by when each landed.
 */
export default function AzureDevOpsTab({
  pullRequests,
  fixedByNotes,
  resolved,
  branches,
  config,
  result,
  onResult,
  changes,
  onChanges,
  identity,
  onlyMine,
  onOnlyMineChange,
}: Props) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // null means nobody has picked yet, so the default below still applies.
  // Branch settings load a tick after mount, so this cannot be a useState seed.
  const [selection, setSelection] = useState<Selection | null>(null)

  // The scope the last fetch of changed files was gathered under.
  const [fetchedOnlyMine, setFetchedOnlyMine] = useState<boolean | null>(null)

  const configured = configuredBranches(branches)

  // The PAT is configured on the server and never typed here, so this tab only
  // needs to know that there is one.
  const hasToken = config?.devOps.configured === true

  const isMine = (pr: DevOpsPullRequest) => isAuthoredBy(pr, identity)

  // UAT by default, but only once it has a branch to filter on - otherwise the
  // list would come up empty and look like a failed fetch.
  const chosen: Selection =
    selection ?? (branches[DEFAULT_ENVIRONMENT].trim() === '' ? 'all' : DEFAULT_ENVIRONMENT)

  // Which branches count as "in the release" for the current selection.
  const wanted = useMemo(() => {
    if (chosen === 'all') {
      return configured
    }

    const branch = branches[chosen].trim()
    return branch === '' ? [] : [branch]

    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chosen, branches])

  // With nothing configured there is nothing to filter on, so show everything
  // rather than an inexplicably empty page.
  const filtering = configured.length > 0

  const filtered = useMemo(() => {
    if (result === null) {
      return null
    }

    if (!filtering) {
      return result.repositories
    }

    return result.repositories
      .map((group) => ({
        ...group,
        pullRequests: group.pullRequests.filter(
          (pr) =>
            sameBranch(group.repository, UNFILTERED_REPOSITORY) ||
            wanted.some((branch) => sameBranch(pr.targetBranch, branch)),
        ),
      }))
      .filter((group) => group.pullRequests.length > 0)
  }, [result, wanted, filtering])

  // Applied after the branch filter, and on the already-fetched list, so the
  // checkbox is instant and costs no Azure DevOps calls.
  const visible = useMemo(
    () =>
      (filtered ?? [])
        .map((group) => ({
          ...group,
          pullRequests: onlyMine ? group.pullRequests.filter(isMine) : group.pullRequests,
        }))
        .filter((group) => group.pullRequests.length > 0),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [filtered, onlyMine, identity],
  )

  const shown = visible.reduce((sum, group) => sum + group.pullRequests.length, 0)

  const run = async (label: string, action: () => Promise<void>) => {
    setBusy(label)
    setError(null)

    try {
      await action()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(null)
    }
  }

  /**
   * One trip: the pull requests, then the files they changed. The two were
   * separate buttons and there was never a reason to press only one - the file
   * list is what unlocks merging a repository in one go.
   *
   * The author is only known once a pull request has been read, so "only mine"
   * cannot narrow the first call. It does narrow the second, which is the
   * expensive one: two more requests per pull request.
   */
  const fetchEverything = () =>
    run('fetch', async () => {
      onChanges(null)

      const found = await api.devOpsPullRequests('', pullRequests)
      onResult(found)

      const mineOnly = onlyMine
        ? pullRequests.filter((reference) =>
            found.repositories.some((group) =>
              group.pullRequests.some(
                (pr) => pr.pullRequestId === reference.pullRequestId && isMine(pr),
              ),
            ),
          )
        : pullRequests

      // Everything filtered out means nothing left to ask about.
      onChanges(mineOnly.length === 0 ? { repositories: [], failures: [] } : await api.changedFiles('', mineOnly))

      // The scope the files were fetched under, so a later toggle can say so.
      setFetchedOnlyMine(onlyMine)
    })

  const total = result?.repositories.reduce((sum, group) => sum + group.pullRequests.length, 0) ?? 0

  return (
    <>
      <p className="lede">
        Lines up the pull requests behind the release &mdash; grouped by repository, in the order a
        deployment replays them &mdash; and the files each one changed.
      </p>

      <section className="panel">
        <h2>Connect</h2>

        {!resolved ? (
          <p className="summary">
            Load a page and press <strong>Retrieve From Jira</strong> on the Atlassian tab first —
            the pull requests come from those tickets&rsquo; comments.
          </p>
        ) : (
          <p className="summary">
            {pullRequests.length} pull request link(s) found across the release&rsquo;s tickets.
          </p>
        )}

        {hasToken ? (
          <p className="summary">
            Using the Azure DevOps token configured on the server
            {identity && (
              <>
                {' '}
                as <strong>{identity.displayName}</strong>
              </>
            )}
            . The token stays on the server and is never sent to this page.
          </p>
        ) : (
          <p className="warn">
            No Azure DevOps token is configured on the server. Set{' '}
            <code>Credentials:DevOpsPersonalAccessToken</code> and restart.
          </p>
        )}

        <label className="check">
          <input
            type="checkbox"
            checked={onlyMine}
            disabled={identity === null}
            onChange={(e) => onOnlyMineChange(e.target.checked)}
          />
          Only pull requests I authored
          {identity && <span className="hint"> {identity.displayName}</span>}
        </label>

        <div className="actions">
          <button
            onClick={fetchEverything}
            disabled={busy !== null || !hasToken || pullRequests.length === 0}
          >
            {busy === 'fetch' ? 'Reading Azure DevOps...' : 'Fetch pull requests and files'}
          </button>

          {result && (
            <span className="connected">
              {total} pull request(s) across {result.repositories.length} repo(s)
            </span>
          )}
        </div>

        {/* Files are fetched for the pull requests in scope at the time, so a
            later change of mind has to be re-fetched to be complete. */}
        {changes !== null && fetchedOnlyMine !== null && fetchedOnlyMine !== onlyMine && (
          <p className="warn">
            The file list was fetched for {fetchedOnlyMine ? 'your pull requests only' : 'every pull request'}.
            Fetch again to bring it in line.
          </p>
        )}
      </section>

      {result && (
        <section className="panel">
          <h2>Environment</h2>

          <div className="fields">
            <label>
              Target branch <span className="hint">from Settings</span>
              <select value={chosen} onChange={(e) => setSelection(e.target.value as Selection)}>
                <option value="all">
                  {filtering ? `All configured (${configured.join(', ')})` : 'All pull requests'}
                </option>

                {FILTER_ENVIRONMENTS.map(({ key, label }) => (
                  <option key={key} value={key} disabled={branches[key].trim() === ''}>
                    {label}
                    {branches[key].trim() === '' ? ' — not set' : ` — ${branches[key].trim()}`}
                  </option>
                ))}
              </select>
            </label>
          </div>

          <p className="summary">
            Showing {shown} of {total} pull request(s).{' '}
            {filtering ? (
              <>
                <code>{UNFILTERED_REPOSITORY}</code> is shown whatever its target branch.
              </>
            ) : (
              <>No branches set on the Settings tab, so nothing is filtered out.</>
            )}
          </p>

          {filtering && wanted.length === 0 && (
            <p className="warn">
              That environment has no branch set on the Settings tab, so only{' '}
              <code>{UNFILTERED_REPOSITORY}</code> can match.
            </p>
          )}
        </section>
      )}

      {error && <p className="error">{error}</p>}

      {result?.failures.map((failure) => (
        <p key={failure.url} className="warn">
          {failure.ticketKey}: {failure.reason} ({failure.url})
        </p>
      ))}

      {result !== null && visible.length === 0 && (
        <p className="notice">
          {onlyMine && (filtered?.length ?? 0) > 0
            ? 'No pull request in this release was authored by you. Untick "only pull requests I authored" to see the rest.'
            : `No pull request targets ${chosen === 'all' ? 'any configured branch' : 'that branch'}. Check the branch names on the Settings tab.`}
        </p>
      )}

      {visible.map((group) => {
        const files = changes?.repositories.find(
          (entry) => entry.repository.toLowerCase() === group.repository.toLowerCase(),
        )

        return (
        <section key={group.repository} className="panel">
          <h2>
            {group.repository}
            {files?.hasOverlap && <span className="badge">overlap</span>}
          </h2>

          <div className="scroller">
            <table>
              <thead>
                <tr>
                  <th>PR</th>
                  <th>Title</th>
                  <th>Ticket</th>
                  <th>Author</th>
                  <th>Target</th>
                  <th>Source commit</th>
                  <th>Status</th>
                  <th>Completed</th>
                </tr>
              </thead>
              <tbody>
                {group.pullRequests.map((pr) => (
                  <tr key={`${pr.repository}-${pr.pullRequestId}`}>
                    <td>
                      <a href={pr.webUrl} target="_blank" rel="noreferrer">
                        !{pr.pullRequestId}
                      </a>
                    </td>
                    <td>{pr.title}</td>
                    <td>{pr.ticketKey}</td>
                    <td>{pr.author ?? '—'}</td>
                    <td>{pr.targetBranch ?? '—'}</td>
                    <td>
                      <Sha value={pr.sourceCommit} label="source commit" />
                    </td>
                    <td>{pr.status}</td>
                    <td className={pr.completedAt ? undefined : 'empty'}>
                      {pr.completedAt ? new Date(pr.completedAt).toLocaleString() : 'not merged'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Folded into the repository rather than a section of its own: the
              files belong to these pull requests, and collapsed by default
              because the list is long and only consulted when merging. */}
          {files && (
            <details className="drawer">
              <summary>
                {files.files.length} file(s) changed
                {files.hasOverlap
                  ? ' — some touched by more than one ticket'
                  : ' — none shared between tickets'}
              </summary>

              <div className="scroller files">
                <table>
                  <thead>
                    <tr>
                      <th>File</th>
                      <th>Tickets</th>
                    </tr>
                  </thead>
                  <tbody>
                    {files.files.map((file) => (
                      <tr key={file.path} className={file.overlapping ? 'overlap' : undefined}>
                        <td>{file.path}</td>
                        <td>
                          <span className="tickets">
                            {file.ticketKeys.map((key) => (
                              <code key={key}>{key}</code>
                            ))}
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </details>
          )}
        </section>
        )
      })}

      {changes?.failures.map((failure) => (
        <p key={`${failure.url}-changes`} className="warn">
          {failure.ticketKey}: could not read changes — {failure.reason}
        </p>
      ))}

      {fixedByNotes.length > 0 && (
        <section className="panel">
          <h2>No pull request</h2>

          <p className="summary">
            These tickets have no PR link of their own — only a comment saying where the work
            happened.
          </p>

          <div className="scroller">
            <table>
              <thead>
                <tr>
                  <th>Ticket</th>
                  <th>Fixed by comment</th>
                  <th>Author</th>
                </tr>
              </thead>
              <tbody>
                {fixedByNotes.map((note) => (
                  <tr key={note.ticketKey}>
                    <td>{note.ticketKey}</td>
                    <td>{note.comment}</td>
                    <td>{note.author}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </>
  )
}
