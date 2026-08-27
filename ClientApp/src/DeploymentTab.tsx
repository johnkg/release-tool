import { useMemo, useState } from 'react'
import {
  api,
  isAuthoredBy,
  replayCommit,
  type BranchResult,
  type BranchStatus,
  type ChangedFilesResponse,
  type ConfigResponse,
  type DevOpsIdentity,
  type DevOpsLookupResponse,
  type DevOpsPullRequest,
  type MergeResult,
  type PullRequestRef,
} from './api'
import Sha from './Sha'
import {
  formatBranchName,
  formatCandidateName,
  repositoryKey,
  todayIso,
  type AppSettings,
  type RepositoryRef,
} from './settings'

interface Props {
  settings: AppSettings
  config: ConfigResponse | null
  identity: DevOpsIdentity | null

  /** From the Atlassian tab's resolve. Empty is fine - this tab stands alone. */
  pullRequests: PullRequestRef[]

  /** From the Azure DevOps tab's fetch, when it has been run. */
  devOpsResult: DevOpsLookupResponse | null

  /** Gates the bulk merge: a repository with overlapping files is held back. */
  changes: ChangedFilesResponse | null

  /** The Azure DevOps tab's scope, so both tabs offer the same pull requests. */
  onlyMine: boolean
}

/** Which branch a merge lands on. */
type Target = 'deployment' | 'candidate'

/**
 * Creates - and removes - the deployment and candidate branches, then replays
 * the release's pull requests onto whichever of the two is chosen. Everything it
 * needs comes from Settings, so it works on its own; the other two tabs only
 * pre-tick the boxes and supply the pull requests to merge.
 */
export default function DeploymentTab({
  settings,
  config,
  identity,
  pullRequests,
  devOpsResult,
  changes,
  onlyMine,
}: Props) {
  const [date, setDate] = useState(todayIso())

  // null means "follow the date"; typing pins it until the link is used.
  const [branchName, setBranchName] = useState<string | null>(null)
  const [candidateName, setCandidateName] = useState<string | null>(null)

  // Which repositories the user has overridden, either way. Everything absent
  // from here follows the pull requests found in the release.
  const [picked, setPicked] = useState<Record<string, boolean>>({})

  const [target, setTarget] = useState<Target>('deployment')

  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [results, setResults] = useState<BranchResult[] | null>(null)
  const [statuses, setStatuses] = useState<BranchStatus[] | null>(null)
  const [merges, setMerges] = useState<Record<number, MergeResult>>({})

  const hasToken = config?.devOps.configured === true

  const deploymentBranch = branchName ?? formatBranchName(settings.branchNameFormat, date)
  const candidateBranch =
    candidateName ?? formatCandidateName(settings.candidateBranchNameFormat, deploymentBranch, date)

  const source = settings.branches.prod.trim()
  const mergeTarget = target === 'deployment' ? deploymentBranch : candidateBranch

  /**
   * Repositories with work in this release. The Azure DevOps tab's fetch is the
   * better answer when it has been run; the Atlassian resolve is enough on its
   * own, and neither is required.
   */
  const withPullRequests = useMemo(() => {
    const names = devOpsResult
      ? devOpsResult.repositories.map((group) => group.repository)
      : pullRequests.map((pr) => pr.repository)

    return new Set(names.map((name) => name.toLowerCase()))
  }, [devOpsResult, pullRequests])

  const isChecked = (repo: RepositoryRef) =>
    picked[repositoryKey(repo)] ?? withPullRequests.has(repo.name.toLowerCase())

  const selected = settings.repositories.filter(isChecked)

  const statusFor = (repo: RepositoryRef) =>
    statuses?.find(
      (status) =>
        status.repository.toLowerCase() === repo.name.toLowerCase() &&
        status.project.toLowerCase() === repo.project.toLowerCase(),
    ) ?? null

  const resultFor = (repo: RepositoryRef) =>
    results?.find(
      (result) =>
        result.repository.toLowerCase() === repo.name.toLowerCase() &&
        result.project.toLowerCase() === repo.project.toLowerCase(),
    ) ?? null

  const toggle = (repo: RepositoryRef) =>
    setPicked((current) => ({ ...current, [repositoryKey(repo)]: !isChecked(repo) }))

  const setAll = (checked: boolean) =>
    setPicked(Object.fromEntries(settings.repositories.map((repo) => [repositoryKey(repo), checked])))

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

  const branchRequest = (name: string, from: string) => ({
    branchName: name.trim(),
    sourceBranch: from,
    repositories: selected,
  })

  const check = () =>
    run('check', async () => {
      setResults(null)
      setStatuses((await api.branchStatus('', branchRequest(deploymentBranch, source))).repositories)
    })

  const create = (name: string, from: string, label: string) =>
    run(label, async () => {
      const confirmed = window.confirm(
        `Create "${name}" from "${from}" in ${selected.length} repository(s)?`,
      )

      if (!confirmed) return

      setResults((await api.createBranches('', branchRequest(name, from))).results)
      setStatuses(null)
    })

  // Deleting is the destructive one, so it names the branch and the count.
  const remove = (name: string, label: string) =>
    run(label, async () => {
      const confirmed = window.confirm(
        `Delete "${name}" from ${selected.length} repository(s)? This cannot be undone from here.`,
      )

      if (!confirmed) return

      setResults((await api.deleteBranches('', branchRequest(name, ''))).results)
      setStatuses(null)
    })

  // ---- Merging ------------------------------------------------------------

  /**
   * The pull requests in this release, per repository, in the order they landed
   * - narrowed to the same scope the Azure DevOps tab is showing, so the two
   * tabs cannot disagree about what is in the release.
   */
  const prsByRepository = useMemo(() => {
    const map = new Map<string, DevOpsPullRequest[]>()

    for (const group of devOpsResult?.repositories ?? []) {
      const mine = onlyMine
        ? group.pullRequests.filter((pr) => isAuthoredBy(pr, identity))
        : group.pullRequests

      // One entry per pull request. The lookup returns one per *ticket*, so a
      // PR cited by two tickets arrives twice - and replaying it twice would
      // merge the same commit twice. The ticket keys are kept below.
      const once = [...new Map(mine.map((pr) => [pr.pullRequestId, pr])).values()]

      if (once.length > 0) {
        map.set(group.repository.toLowerCase(), once)
      }
    }

    return map
  }, [devOpsResult, onlyMine, identity])

  /** Every ticket that cited a given pull request, so nothing is lost to the dedupe. */
  const ticketsByPullRequest = useMemo(() => {
    const map = new Map<number, string[]>()

    for (const group of devOpsResult?.repositories ?? []) {
      for (const pr of group.pullRequests) {
        const seen = map.get(pr.pullRequestId) ?? []

        if (!seen.includes(pr.ticketKey)) {
          map.set(pr.pullRequestId, [...seen, pr.ticketKey])
        }
      }
    }

    return map
  }, [devOpsResult])

  const changesFor = (repo: RepositoryRef) =>
    changes?.repositories.find(
      (group) => group.repository.toLowerCase() === repo.name.toLowerCase(),
    ) ?? null

  /**
   * Pull requests that touched a file another ticket also touched, and the files
   * in question. These are the ones to merge deliberately and check afterwards,
   * so they are called out per row rather than only per repository.
   */
  const overlapsIn = (repo: RepositoryRef) => {
    const map = new Map<number, string[]>()

    for (const file of changesFor(repo)?.files ?? []) {
      if (!file.overlapping) {
        continue
      }

      for (const id of file.pullRequestIds) {
        map.set(id, [...(map.get(id) ?? []), file.path])
      }
    }

    return map
  }

  const mergeSome = (repo: RepositoryRef, prs: DevOpsPullRequest[], label: string) =>
    run(label, async () => {
      const missing = prs.filter((pr) => !replayCommit(pr))

      if (missing.length > 0) {
        setError(
          `No commit to replay for PR !${missing[0].pullRequestId}. Fetch the pull requests again.`,
        )
        return
      }

      const confirmed = window.confirm(
        prs.length === 1
          ? `Cherry-pick PR !${prs[0].pullRequestId} (${prs[0].ticketKey}) onto "${mergeTarget}" in ${repo.name}?`
          : `Cherry-pick all ${prs.length} pull request(s) onto "${mergeTarget}" in ${repo.name}, one at a time?`,
      )

      if (!confirmed) return

      const response = await api.merge('', {
        repository: repo,
        targetBranch: mergeTarget,
        pullRequests: prs.map((pr) => ({
          pullRequestId: pr.pullRequestId,
          ticketKey: pr.ticketKey,

          // The squashed commit where there is one, so a squashed PR replays as
          // the single commit it became rather than the commits it collapsed.
          sourceCommit: replayCommit(pr)!,
          sourceBranch: pr.sourceBranch,
        })),
      })

      setMerges((current) => ({
        ...current,
        ...Object.fromEntries(response.results.map((result) => [result.pullRequestId, result])),
      }))
    })

  const noRepositories = settings.repositories.length === 0

  return (
    <>
      <p className="lede">
        Cuts the deployment and candidate branches across the release&rsquo;s repositories, then
        replays the pull requests onto whichever of the two you choose.
      </p>

      <section className="panel">
        <h2>Deployment</h2>

        {hasToken ? (
          <p className="summary">
            Acting as{' '}
            <strong>{identity ? identity.displayName : 'the account configured on the server'}</strong>.
            Branches are cut from <strong>{source === '' ? 'PROD' : source}</strong> in every ticked
            repository; repositories with a pull request in this release are ticked for you.
          </p>
        ) : (
          <p className="warn">
            No Azure DevOps token is configured on the server. Set{' '}
            <code>Credentials:DevOpsPersonalAccessToken</code> and restart.
          </p>
        )}

        <div className="fields">
          <label>
            Deployment date
            <input
              type="date"
              value={date}
              onChange={(e) => {
                setDate(e.target.value)

                // An edited name is deliberate, so only untouched ones follow.
                if (branchName !== null) {
                  setBranchName(formatBranchName(settings.branchNameFormat, e.target.value))
                }

                if (candidateName !== null) {
                  setCandidateName(
                    formatCandidateName(
                      settings.candidateBranchNameFormat,
                      formatBranchName(settings.branchNameFormat, e.target.value),
                      e.target.value,
                    ),
                  )
                }
              }}
            />
          </label>

          <label>
            Deployment branch <span className="hint">cut from {source || 'PROD'}</span>
            <input
              value={deploymentBranch}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => setBranchName(e.target.value)}
            />
          </label>

          <label>
            Candidate branch <span className="hint">cut from the deployment branch</span>
            <input
              value={candidateBranch}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => setCandidateName(e.target.value)}
            />
          </label>
        </div>

        {(branchName !== null || candidateName !== null) && (
          <p className="summary">
            Using a name you typed.
            <button
              className="link"
              onClick={() => {
                setBranchName(null)
                setCandidateName(null)
              }}
            >
              follow the date again
            </button>
          </p>
        )}

        {source === '' && (
          <p className="warn">
            No PROD branch set on the Settings tab. That is the branch a deployment branch is cut
            from, so nothing can be created until it is filled in.
          </p>
        )}
      </section>

      <section className="panel">
        <h2>Repositories</h2>

        {noRepositories ? (
          <p className="summary">
            No repositories on the Settings tab yet. Add them there and they will appear here.
          </p>
        ) : (
          <>
            <div className="actions">
              <button onClick={() => setAll(true)}>Select all</button>
              <button onClick={() => setAll(false)}>Select none</button>
              <span className="connected">
                {selected.length} of {settings.repositories.length} selected
              </span>
            </div>

            <div className="scroller">
              <table>
                <thead>
                  <tr>
                    <th />
                    <th>Repository</th>
                    <th>Project</th>
                    <th>In this release</th>
                    <th>Branch</th>
                  </tr>
                </thead>
                <tbody>
                  {settings.repositories.map((repo) => {
                    const status = statusFor(repo)
                    const result = resultFor(repo)
                    const inRelease = withPullRequests.has(repo.name.toLowerCase())

                    return (
                      <tr key={repositoryKey(repo)}>
                        <td>
                          <input
                            type="checkbox"
                            checked={isChecked(repo)}
                            aria-label={`Include ${repo.name}`}
                            onChange={() => toggle(repo)}
                          />
                        </td>
                        <td>{repo.name}</td>
                        <td>{repo.project}</td>
                        <td className={inRelease ? undefined : 'empty'}>
                          {inRelease ? 'has a pull request' : '—'}
                        </td>
                        <td className={result || status ? undefined : 'empty'}>
                          {result ? (
                            <span className={result.success ? undefined : 'warn'}>{result.message}</span>
                          ) : status ? (
                            status.error ? (
                              <span className="warn">{status.error}</span>
                            ) : status.exists ? (
                              'exists'
                            ) : status.sourceExists ? (
                              'not created'
                            ) : (
                              <span className="warn">no source branch</span>
                            )
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
          </>
        )}
      </section>

      {error && <p className="error">{error}</p>}

      <section className="panel">
        <h2>Branches</h2>

        <div className="actions">
          <button onClick={check} disabled={busy !== null || !hasToken || selected.length === 0}>
            {busy === 'check' ? 'Checking...' : 'Check branches'}
          </button>

          <button
            className="primary"
            onClick={() => create(deploymentBranch, source, 'create')}
            disabled={
              busy !== null || !hasToken || selected.length === 0 || source === '' ||
              deploymentBranch.trim() === ''
            }
          >
            {busy === 'create' ? 'Creating...' : 'Create deployment branch'}
          </button>

          <button
            className="destructive"
            onClick={() => remove(deploymentBranch, 'delete')}
            disabled={busy !== null || !hasToken || selected.length === 0 || deploymentBranch.trim() === ''}
          >
            {busy === 'delete' ? 'Deleting...' : 'Delete deployment branch'}
          </button>
        </div>

        <div className="actions">
          <button
            onClick={() => create(candidateBranch, deploymentBranch, 'create-candidate')}
            disabled={
              busy !== null || !hasToken || selected.length === 0 ||
              deploymentBranch.trim() === '' || candidateBranch.trim() === ''
            }
          >
            {busy === 'create-candidate' ? 'Creating...' : 'Create candidate branch'}
          </button>

          <button
            className="destructive"
            onClick={() => remove(candidateBranch, 'delete-candidate')}
            disabled={busy !== null || !hasToken || selected.length === 0 || candidateBranch.trim() === ''}
          >
            {busy === 'delete-candidate' ? 'Deleting...' : 'Delete candidate branch'}
          </button>
        </div>

        <p className="summary">
          Each repository is reported on separately — one that refuses does not stop the others.
        </p>

        {results && (
          <p className={results.every((r) => r.success) ? 'notice' : 'warn'}>
            {results.filter((r) => r.success).length} of {results.length} succeeded.
          </p>
        )}
      </section>

      <section className="panel">
        <h2>Cherry-pick pull requests</h2>

        {devOpsResult === null ? (
          <p className="summary">
            Fetch the pull requests on the Azure DevOps tab first — cherry-picking replays those
            onto the branch below.
          </p>
        ) : (
          <>
            <div className="fields">
              <label>
                Cherry-pick onto
                <select value={target} onChange={(e) => setTarget(e.target.value as Target)}>
                  <option value="deployment">Deployment — {deploymentBranch}</option>
                  <option value="candidate">Candidate — {candidateBranch}</option>
                </select>
              </label>
            </div>

            <p className="summary">
              Each pull request&rsquo;s commit is cherry-picked onto <code>{mergeTarget}</code>, one
              at a time, so only that change is replayed and a conflict stops the run where you can
              see it. The branch must exist first.
              {onlyMine && (
                <>
                  {' '}
                  Showing only the pull requests you authored, matching the Azure DevOps tab &mdash;
                  untick <em>only pull requests I authored</em> there to see the rest.
                </>
              )}
            </p>
          </>
        )}

        {selected.map((repo) => {
          const prs = prsByRepository.get(repo.name.toLowerCase()) ?? []

          if (prs.length === 0) {
            return null
          }

          const repoChanges = changesFor(repo)
          const blocked = repoChanges?.hasOverlap ?? true
          const label = `merge-all-${repositoryKey(repo)}`
          const overlaps = overlapsIn(repo)

          return (
            <div key={repositoryKey(repo)} className="options">
              <h3>{repo.name}</h3>

              <div className="scroller">
                <table>
                  <thead>
                    <tr>
                      <th>PR</th>
                      <th>Ticket</th>
                      <th>Commit to replay</th>
                      <th>Shared files</th>
                      <th>Result</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {prs.map((pr) => {
                      const merged = merges[pr.pullRequestId]
                      const shared = overlaps.get(pr.pullRequestId) ?? []

                      return (
                        <tr
                          key={pr.pullRequestId}
                          className={shared.length > 0 ? 'overlap' : undefined}
                        >
                          <td>
                            <a href={pr.webUrl} target="_blank" rel="noreferrer">
                              !{pr.pullRequestId}
                            </a>
                          </td>
                          <td>{(ticketsByPullRequest.get(pr.pullRequestId) ?? [pr.ticketKey]).join(', ')}</td>
                          <td>
                            <Sha
                              value={replayCommit(pr)}
                              label={pr.squashed ? 'squashed commit' : 'source commit'}
                            />
                            {pr.squashed && <span className="hint"> squashed</span>}
                          </td>
                          <td className={shared.length > 0 ? undefined : 'empty'}>
                            {shared.length > 0 ? (
                              <span title={shared.join('\n')}>
                                {shared.length} file(s) another ticket also changed
                              </span>
                            ) : (
                              '—'
                            )}
                          </td>
                          <td className={merged ? undefined : 'empty'}>
                            {merged ? (
                              <span className={merged.success ? undefined : 'warn'}>
                                {merged.message}
                              </span>
                            ) : (
                              'not picked'
                            )}
                            {merged?.commitId && (
                              <>
                                {' '}
                                <Sha value={merged.commitId} label="new commit" />
                              </>
                            )}
                          </td>
                          <td>
                            <button
                              onClick={() => mergeSome(repo, [pr], `merge-${pr.pullRequestId}`)}
                              disabled={busy !== null || !hasToken || !replayCommit(pr)}
                            >
                              {busy === `merge-${pr.pullRequestId}` ? 'Picking...' : 'Cherry-pick'}
                            </button>
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>

              <div className="actions">
                <button
                  onClick={() => mergeSome(repo, prs, label)}
                  disabled={busy !== null || !hasToken || blocked}
                >
                  {busy === label ? 'Picking one by one...' : `Cherry-pick all ${prs.length}`}
                </button>

                {repoChanges === null ? (
                  <span className="connected">
                    Fetch changed files on the Azure DevOps tab to enable picking all.
                  </span>
                ) : blocked ? (
                  <span className="warn">
                    Two tickets changed the same file here, so these have to be picked one at a
                    time.
                  </span>
                ) : (
                  <span className="connected">No file is touched by more than one ticket.</span>
                )}
              </div>
            </div>
          )
        })}
      </section>
    </>
  )
}
