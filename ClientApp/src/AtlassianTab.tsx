import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  api,
  COLUMN_LABELS,
  type ApprovalColumn,
  type ApprovalsResponse,
  type ConfigResponse,
  type Credentials,
  type DeveloperAssignment,
  type DeveloperSource,
  type FixedByNote,
  type JiraUser,
  type PageListItem,
  type PullRequestRef,
  type ReleaseStatus,
  type RunOutcome,
  type TicketStatus,
  type VerifyResponse,
} from './api'

const SOURCE_LABELS: Record<DeveloperSource, string> = {
  PullRequest: 'PR comment',
  Reference: 'Referenced ticket',
  Defaulted: 'Defaulted',
}

const CLEARABLE: ApprovalColumn[] = [
  'DeveloperAssigned',
  'RequestedBy',
  'PrApprovedBy',
  'PrApprovedStatus',
  'MergedToDeploymentBranch',
]

interface Props {
  onResolved: (pullRequests: PullRequestRef[], fixedByNotes: FixedByNote[]) => void
  config: ConfigResponse | null
}

export default function AtlassianTab({ onResolved, config }: Props) {
  // Held in memory only. Persisting a personal API token to storage would leave
  // it readable by any script on the origin - and with the token configured
  // server-side there is nothing to persist in the first place.
  const [email, setEmail] = useState('')
  const [token, setToken] = useState('')
  const [me, setMe] = useState<VerifyResponse | null>(null)

  // Set when someone wants to act as themselves on an instance that has its own
  // credentials, which is what keeps the page history honest.
  const [overriding, setOverriding] = useState(false)

  // null means untouched, so the configured default can fill in once it arrives.
  const [spaceKey, setSpaceKey] = useState<string | null>(null)
  const [pages, setPages] = useState<PageListItem[]>([])
  const [pageId, setPageId] = useState('')

  const [approvals, setApprovals] = useState<ApprovalsResponse | null>(null)
  const [assignments, setAssignments] = useState<DeveloperAssignment[] | null>(null)

  const [writeDeveloper, setWriteDeveloper] = useState(true)
  const [writeRequestedBy, setWriteRequestedBy] = useState(true)

  // The approver is looked up in Jira rather than typed, so the mention always
  // resolves to a real account.
  const [approverQuery, setApproverQuery] = useState('')
  const [approverResults, setApproverResults] = useState<JiraUser[]>([])
  const [approver, setApprover] = useState<JiraUser | null>(null)
  const [searching, setSearching] = useState(false)

  const [clearColumns, setClearColumns] = useState<ApprovalColumn[]>([])

  // Jira workflow state for the rows this person owns, and what the last run
  // did to each of them - a status move and a resolution change report alike.
  const [statuses, setStatuses] = useState<Record<string, TicketStatus>>({})
  const [runs, setRuns] = useState<Record<string, RunOutcome>>({})

  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  // Blank when the server holds the credentials: api.ts then sends no headers
  // and the server falls back to its own.
  const usingServer = config?.atlassian.configured === true && !overriding
  const credentials: Credentials = usingServer
    ? { email: '', token: '' }
    : { email: email.trim(), token: token.trim() }

  const hasCredentials = usingServer || (credentials.email !== '' && credentials.token !== '')

  const effectiveSpaceKey = spaceKey ?? config?.defaultSpaceKey ?? ''

  const selectedPage = pages.find((page) => page.pageId === pageId) ?? null

  const proposed = useMemo(
    () => new Map((assignments ?? []).map((a) => [a.ticketKey, a])),
    [assignments],
  )

  const counts = useMemo(() => {
    const tally: Record<DeveloperSource, number> = { PullRequest: 0, Reference: 0, Defaulted: 0 }
    for (const assignment of assignments ?? []) tally[assignment.source]++
    return tally
  }, [assignments])

  // The approval is recorded only against rows this person is the developer on.
  const myTickets = useMemo(
    () =>
      me === null
        ? []
        : (assignments ?? [])
            .filter((assignment) => assignment.accountId === me.accountId)
            .map((assignment) => assignment.ticketKey),
    [assignments, me],
  )

  const run = useCallback(async (label: string, action: () => Promise<void>) => {
    setBusy(label)
    setError(null)

    try {
      await action()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(null)
    }
  }, [])

  const verify = () =>
    run('verify', async () => {
      setNotice(null)
      setMe(await api.verify(credentials))
    })

  const loadPages = useCallback(
    (key: string) =>
      run('pages', async () => {
        const listing = await api.pages(credentials, key)
        setPages(listing.pages)

        // Newest first from Confluence, so the head is the latest page added.
        setPageId(listing.pages[0]?.pageId ?? '')

        if (listing.pages.length === 0) {
          setNotice('That space has no published pages.')
        }
      }),
    [credentials, run],
  )

  // With the credentials already on the server there is nothing to type, so
  // connect without waiting for a click.
  useEffect(() => {
    if (!usingServer || me !== null) return

    // Deferred a tick: verify() sets state, and doing that straight from an
    // effect body cascades a second render.
    const timer = setTimeout(verify, 0)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [usingServer])

  // Fill the picker once connected, and refill when the space changes.
  useEffect(() => {
    if (me === null) return

    const timer = setTimeout(() => void loadPages(effectiveSpaceKey), 300)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [effectiveSpaceKey, me])

  // Type-ahead against Jira, debounced so a keystroke is not a request.
  useEffect(() => {
    if (me === null || approver !== null || approverQuery.trim().length < 2) {
      setApproverResults([])
      return
    }

    let cancelled = false
    setSearching(true)

    const timer = setTimeout(async () => {
      try {
        const found = await api.users(credentials, approverQuery)
        if (!cancelled) setApproverResults(found.users)
      } catch {
        // A failed lookup should not replace the page's error banner.
        if (!cancelled) setApproverResults([])
      } finally {
        if (!cancelled) setSearching(false)
      }
    }, 300)

    return () => {
      cancelled = true
      clearTimeout(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [approverQuery, approver, me])

  // Read-only, so it needs no button of its own: the statuses appear as soon as
  // the resolve says which rows are this person's.
  const loadStatuses = async (keys: string[]) => {
    const response = await api.ticketStatuses(credentials, keys)
    setStatuses(Object.fromEntries(response.tickets.map((t) => [t.ticketKey, t])))
  }

  // Joined, because myTickets is a fresh array every render and depending on it
  // directly would re-run the effect below forever.
  const myTicketsKey = myTickets.join(',')

  useEffect(() => {
    if (me === null || myTicketsKey === '') return

    // Deferred a tick for the same reason verify() is: setting state straight
    // from an effect body cascades a second render.
    const timer = setTimeout(
      () => void run('statuses', () => loadStatuses(myTicketsKey.split(','))),
      0,
    )

    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [myTicketsKey, me])

  const selectApprover = (user: JiraUser) => {
    setApprover(user)
    setApproverQuery(user.displayName)
    setApproverResults([])
  }

  const clearApprover = () => {
    setApprover(null)
    setApproverQuery('')
    setApproverResults([])
  }

  const load = () =>
    run('load', async () => {
      setNotice(null)
      setAssignments(null)
      setApprovals(null)
      setRuns({})
      onResolved([], [])

      const table = await api.approvals(credentials, pageId)
      setApprovals(table)

      if (table.rows.length === 0) {
        setNotice('No PROJECT rows found in the Approvals table.')
      }
    })

  const resolve = () =>
    run('resolve', async () => {
      if (!approvals) return

      setNotice(null)
      setRuns({})

      const result = await api.resolve(
        credentials,
        approvals.pageId,
        approvals.rows.map((row) => row.ticketKey),
      )

      setAssignments(result.assignments)

      // Hand the pull requests to the DevOps tab.
      onResolved(result.pullRequests, result.fixedByNotes)
    })

  const apply = () =>
    run('apply', async () => {
      if (!approvals || !assignments || !selectedPage || !me) return

      const approving = approver !== null && myTickets.length > 0

      const summary = [
        writeDeveloper && 'Developer Assigned',
        writeRequestedBy && 'Requested By',
        approving && `PR approved by ${approver!.displayName} on ${myTickets.length} of your rows`,
      ]
        .filter(Boolean)
        .join(', ')

      if (!summary) {
        setError('Nothing selected to write.')
        return
      }

      const confirmed = window.confirm(
        `Write ${summary} to "${selectedPage.title}" (version ${approvals.version})?`,
      )

      if (!confirmed) return

      const result = await api.apply(credentials, approvals.pageId, {
        expectedVersion: approvals.version,
        assignments,
        writeDeveloper,
        writeRequestedBy,
        prApproval: approving
          ? {
              displayName: approver!.displayName,
              accountId: approver!.accountId,
              ticketKeys: myTickets,
            }
          : null,
      })

      setNotice(
        `Updated ${result.cellsUpdated} cell(s). The page is now at version ${result.newVersion}.`,
      )

      // The version we previewed is stale now, so force a reload before another write.
      setApprovals(null)
      setAssignments(null)
    })

  /**
   * Moves the tickets this person is the developer on. Nobody else's rows are
   * touched, and the scope is the same list the table above shows. Going live
   * carries the resolution with it, since Jira asks for both on one screen.
   */
  const transition = (target: ReleaseStatus) =>
    run(target, async () => {
      if (!config || myTickets.length === 0) return

      const live = target === 'DeployedToProduction'

      const name = live
        ? config.workflow.deployedToProduction
        : config.workflow.readyForDeployment

      const confirmed = window.confirm(
        `Set ${myTickets.length} Jira ticket(s) to "${name}"` +
          (live ? `, resolution "${config.workflow.resolution}"` : '') +
          `?\n\n${myTickets.join(', ')}`,
      )

      if (!confirmed) return

      setNotice(null)
      const response = await api.transition(credentials, myTickets, target)

      report(response.results, `moved to ${name}`, 'moved')
    })

  /**
   * The way back for the resolution. Unresolved is the field cleared rather
   * than a status to transition to, so it is its own action - and it leaves the
   * status alone, which is what makes it usable on its own.
   */
  const setResolution = (resolved: boolean) =>
    run(resolved ? 'resolve' : 'unresolve', async () => {
      if (!config || myTickets.length === 0) return

      const name = resolved ? config.workflow.resolution : 'Unresolved'

      const confirmed = window.confirm(
        `Set the resolution of ${myTickets.length} Jira ticket(s) to "${name}"?\n\n` +
          `Their status is not changed.\n\n${myTickets.join(', ')}`,
      )

      if (!confirmed) return

      setNotice(null)
      const response = await api.resolution(credentials, myTickets, resolved)

      report(response.results, `set to ${name}`, 'changed')
    })

  /** Both operations report per ticket, so they are summarised the same way. */
  const report = async (results: RunOutcome[], what: string, verb: string) => {
    setRuns(Object.fromEntries(results.map((r) => [r.ticketKey, r])))

    const done = results.filter((r) => r.success && !r.unchanged).length
    const failed = results.filter((r) => !r.success).length

    setNotice(
      `${done} ticket(s) ${what}` +
        (failed > 0 ? `. ${failed} could not be ${verb} — see the table.` : '.'),
    )

    // Show where they actually landed, not what was asked for.
    await loadStatuses(myTickets)
  }

  const clear = () =>
    run('clear', async () => {
      if (!approvals || !selectedPage) return

      const confirmed = window.confirm(
        `Erase ${clearColumns.map((c) => COLUMN_LABELS[c]).join(', ')} from every PROJECT row of "${selectedPage.title}"?`,
      )

      if (!confirmed) return

      const result = await api.clear(credentials, approvals.pageId, approvals.version, clearColumns)

      setNotice(
        `Cleared ${result.cellsUpdated} cell(s). The page is now at version ${result.newVersion}.`,
      )

      setApprovals(null)
      setAssignments(null)
      setClearColumns([])
    })

  const toggleClearColumn = (column: ApprovalColumn) =>
    setClearColumns((current) =>
      current.includes(column) ? current.filter((c) => c !== column) : [...current, column],
    )

  const canApply =
    assignments !== null &&
    assignments.length > 0 &&
    (!writeDeveloper || assignments.every((a) => a.accountId))

  return (
    <>
      <p className="lede">
        Fills the Approvals table on a Confluence release page from each ticket&rsquo;s Jira
        comments &mdash; who wrote the code, who raised the ticket, and who approved the pull
        request.
      </p>

      <section className="panel">
        <h2>Connect</h2>

        {usingServer ? (
          <p className="summary">
            Using the Atlassian credentials configured on the server
            {config?.atlassian.email && (
              <>
                {' '}
                as <strong>{config.atlassian.email}</strong>
              </>
            )}
            . The token stays on the server and is never sent to this page — which also means
            every edit lands in the page history under that name.
            <button className="link" onClick={() => setOverriding(true)}>
              use a different account
            </button>
          </p>
        ) : (
          <>
            <div className="fields">
              <label>
                Atlassian email
                <input
                  type="email"
                  value={email}
                  autoComplete="username"
                  placeholder="you@example.com"
                  onChange={(e) => setEmail(e.target.value)}
                />
              </label>

              <label>
                API token
                <input
                  type="password"
                  value={token}
                  autoComplete="off"
                  placeholder="Kept in memory only"
                  onChange={(e) => setToken(e.target.value)}
                />
              </label>
            </div>

            {config?.atlassian.configured && (
              <p className="summary">
                Acting as yourself instead of the configured account.
                <button
                  className="link"
                  onClick={() => {
                    setOverriding(false)
                    setEmail('')
                    setToken('')
                    setMe(null)
                  }}
                >
                  use the server credentials
                </button>
              </p>
            )}
          </>
        )}

        <div className="actions">
          <button onClick={verify} disabled={!hasCredentials || busy !== null}>
            {busy === 'verify' ? 'Checking...' : 'Verify token'}
          </button>

          {me && <span className="connected">Connected as {me.displayName}</span>}
        </div>
      </section>

      <section className="panel">
        <h2>Choose the page</h2>

        <div className="fields">
          <label>
            Space key <span className="hint">from config, editable</span>
            <input
              value={effectiveSpaceKey}
              placeholder="Blank uses the configured default"
              onChange={(e) => setSpaceKey(e.target.value)}
            />
          </label>

          <label>
            Page <span className="hint">newest first</span>
            <select
              value={pageId}
              disabled={pages.length === 0 || busy !== null}
              onChange={(e) => setPageId(e.target.value)}
            >
              {pages.length === 0 && <option value="">Verify your token first</option>}
              {pages.map((page) => (
                <option key={page.pageId} value={page.pageId}>
                  {page.title}
                </option>
              ))}
            </select>
          </label>
        </div>

        <div className="actions">
          <button onClick={load} disabled={pageId === '' || busy !== null}>
            {busy === 'load' ? 'Loading...' : 'Load'}
          </button>

          {approvals && (
            <span className="connected">
              page {approvals.pageId}, version {approvals.version}
            </span>
          )}
        </div>
      </section>

      {error && <p className="error">{error}</p>}
      {notice && <p className="notice">{notice}</p>}

      {approvals && approvals.rows.length > 0 && (
        <>
          <section className="panel">
            <h2>Review</h2>

            {assignments && (
              <p className="summary">
                {counts.PullRequest} from PR comments, {counts.Reference} from a referenced ticket,{' '}
                <strong>{counts.Defaulted} defaulted</strong>. Check the highlighted rows before
                applying.
              </p>
            )}

            <div className="scroller">
              <table>
                <thead>
                  <tr>
                    <th>Ticket</th>
                    <th>Current developer</th>
                    <th>Proposed developer</th>
                    <th>Source</th>
                    <th>Requested by</th>
                    <th>PR approved by</th>
                  </tr>
                </thead>
                <tbody>
                  {approvals.rows.map((row) => {
                    const assignment = proposed.get(row.ticketKey)
                    const mine = assignment?.accountId === me?.accountId

                    return (
                      <tr
                        key={row.ticketKey}
                        className={assignment?.source === 'Defaulted' ? 'defaulted' : undefined}
                      >
                        <td>{row.ticketKey}</td>
                        <td className={row.values.DeveloperAssigned ? undefined : 'empty'}>
                          {row.values.DeveloperAssigned ?? 'empty'}
                        </td>
                        <td>
                          {assignment ? (
                            assignment.developerName
                          ) : (
                            <span className="empty">not resolved</span>
                          )}
                          {assignment && !assignment.accountId && (
                            <span className="warn"> no account found</span>
                          )}
                        </td>
                        <td>{assignment ? SOURCE_LABELS[assignment.source] : '—'}</td>
                        <td className={assignment?.reporterName ? undefined : 'empty'}>
                          {assignment?.reporterName ?? '—'}
                        </td>
                        <td className={approver && mine ? undefined : 'empty'}>
                          {approver && mine && assignment ? approver.displayName : '—'}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>

            <fieldset className="options">
              <legend>Write</legend>

              <label className="check">
                <input
                  type="checkbox"
                  checked={writeDeveloper}
                  onChange={(e) => setWriteDeveloper(e.target.checked)}
                />
                Developer Assigned
              </label>

              <label className="check">
                <input
                  type="checkbox"
                  checked={writeRequestedBy}
                  onChange={(e) => setWriteRequestedBy(e.target.checked)}
                />
                Requested By <span className="hint">from the Jira reporter</span>
              </label>

              <div className="picker">
                <label>
                  PR approved by <span className="hint">search Jira, then pick</span>
                  <input
                    value={approverQuery}
                    placeholder="Start typing a name or email"
                    autoComplete="off"
                    onChange={(e) => {
                      setApprover(null)
                      setApproverQuery(e.target.value)
                    }}
                  />
                </label>

                {approver ? (
                  <p className="picked">
                    Will tag <strong>{approver.displayName}</strong>
                    {approver.email && <span className="hint"> {approver.email}</span>} on the{' '}
                    {myTickets.length} row(s) where you are the developer, and set Approved and
                    Merged.
                    <button className="link" onClick={clearApprover}>
                      clear
                    </button>
                  </p>
                ) : (
                  <>
                    {searching && <p className="hint">Searching...</p>}

                    {approverResults.length > 0 && (
                      <ul className="results">
                        {approverResults.map((user) => (
                          <li key={user.accountId}>
                            <button onClick={() => selectApprover(user)}>
                              {user.displayName}
                              {user.email && <span className="hint"> {user.email}</span>}
                            </button>
                          </li>
                        ))}
                      </ul>
                    )}

                    {!searching && approverQuery.trim().length >= 2 && approverResults.length === 0 && (
                      <p className="hint">No matching Atlassian account.</p>
                    )}
                  </>
                )}
              </div>
            </fieldset>

            <div className="actions">
              <button onClick={resolve} disabled={busy !== null}>
                {busy === 'resolve' ? 'Retrieving...' : 'Retrieve From Jira'}
              </button>

              <button className="primary" onClick={apply} disabled={!canApply || busy !== null}>
                {busy === 'apply' ? 'Applying...' : 'Apply to page'}
              </button>
            </div>

            {assignments && !canApply && (
              <p className="warn">
                Some rows have no Atlassian account and cannot be written as a mention.
              </p>
            )}

            {approver && assignments && myTickets.length === 0 && (
              <p className="warn">
                No resolved row has you as the developer, so no PR approval will be written.
              </p>
            )}
          </section>

          {assignments && config && (
            <section className="panel">
              <h2>Jira status</h2>

              <p className="summary">
                Moves the tickets where <strong>you</strong> are the resolved developer between{' '}
                <strong>{config.workflow.readyForDeployment}</strong> and{' '}
                <strong>{config.workflow.deployedToProduction}</strong>, and sets the resolution to{' '}
                <strong>{config.workflow.resolution}</strong> on the way live. Rows belonging to
                anyone else are left alone. This writes to the Jira tickets themselves &mdash; the
                Confluence page only decides which tickets are in scope, so a copy of the release
                page still means the real tickets.
              </p>

              {myTickets.length === 0 ? (
                <p className="warn">
                  No resolved row has you as the developer, so there is nothing to move.
                </p>
              ) : (
                <>
                  <div className="scroller">
                    <table>
                      <thead>
                        <tr>
                          <th>Ticket</th>
                          <th>Current status</th>
                          <th>Resolution</th>
                          <th>Last run</th>
                        </tr>
                      </thead>
                      <tbody>
                        {myTickets.map((ticketKey) => {
                          const ticket = statuses[ticketKey]
                          const result = runs[ticketKey]

                          return (
                            <tr key={ticketKey}>
                              <td>{ticketKey}</td>
                              <td className={ticket?.status ? undefined : 'empty'}>
                                {ticket?.status ?? (busy === 'statuses' ? 'reading...' : 'unknown')}
                              </td>
                              {/* No resolution is the normal state, not missing data. */}
                              <td className={ticket?.resolution ? undefined : 'empty'}>
                                {ticket?.resolution ?? 'Unresolved'}
                              </td>
                              <td className={result ? undefined : 'empty'}>
                                {result ? (
                                  <span className={result.success ? undefined : 'warn'}>
                                    {result.message}
                                  </span>
                                ) : (
                                  '—'
                                )}
                              </td>
                            </tr>
                          )
                        })}
                      </tbody>
                    </table>
                  </div>

                  <div className="actions">
                    <button
                      className="primary"
                      onClick={() => transition('DeployedToProduction')}
                      disabled={busy !== null}
                    >
                      {busy === 'DeployedToProduction'
                        ? 'Moving...'
                        : `Set to ${config.workflow.deployedToProduction}`}
                    </button>

                    <button
                      onClick={() => transition('ReadyForDeployment')}
                      disabled={busy !== null}
                    >
                      {busy === 'ReadyForDeployment'
                        ? 'Moving...'
                        : `Set back to ${config.workflow.readyForDeployment}`}
                    </button>

                    <button onClick={() => setResolution(false)} disabled={busy !== null}>
                      {busy === 'unresolve' ? 'Clearing...' : 'Set resolution to Unresolved'}
                    </button>

                    <button
                      onClick={() => run('statuses', () => loadStatuses(myTickets))}
                      disabled={busy !== null}
                    >
                      {busy === 'statuses' ? 'Reading...' : 'Refresh status'}
                    </button>
                  </div>

                  <p className="hint">
                    A ticket already where it is being sent is left alone. One the workflow will not
                    move from where it stands is reported with the transitions Jira does offer, and
                    the rest still go through. <strong>Unresolved</strong> clears the resolution
                    field rather than setting a value, and leaves the status untouched &mdash; so
                    putting a ticket back takes both buttons.
                  </p>
                </>
              )}
            </section>
          )}

          <section className="panel danger">
            <h2>Clear columns</h2>

            <p className="summary">
              Empties the chosen columns on every PROJECT row. Intended for resetting a test page.
            </p>

            <fieldset className="options">
              <legend>Columns to erase</legend>

              {CLEARABLE.map((column) => (
                <label key={column} className="check">
                  <input
                    type="checkbox"
                    checked={clearColumns.includes(column)}
                    disabled={!approvals.availableColumns.includes(column)}
                    onChange={() => toggleClearColumn(column)}
                  />
                  {COLUMN_LABELS[column]}
                  {!approvals.availableColumns.includes(column) && (
                    <span className="hint"> not on this page</span>
                  )}
                </label>
              ))}
            </fieldset>

            <div className="actions">
              <button
                className="destructive"
                onClick={clear}
                disabled={clearColumns.length === 0 || busy !== null}
              >
                {busy === 'clear' ? 'Clearing...' : 'Clear selected columns'}
              </button>
            </div>
          </section>
        </>
      )}
    </>
  )
}
