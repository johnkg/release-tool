import { useState } from 'react'
import {
  DEFAULT_BRANCH_NAME_FORMAT,
  DEFAULT_CANDIDATE_BRANCH_NAME_FORMAT,
  ENVIRONMENTS,
  FORMAT_TOKENS,
  UNFILTERED_REPOSITORY,
  formatBranchName,
  formatCandidateName,
  parseRepository,
  repositoryKey,
  sameRepository,
  todayIso,
  type AppSettings,
  type DeploymentBranches,
  type RepositoryRef,
} from './settings'

interface Props {
  settings: AppSettings
  onChange: (settings: AppSettings) => void
  saving: boolean
  savedAt: string | null
  error: string | null
}

export default function SettingsTab({ settings, onChange, saving, savedAt, error }: Props) {
  const [adding, setAdding] = useState('')
  const [addError, setAddError] = useState<string | null>(null)

  const updateBranch = (key: keyof DeploymentBranches, value: string) =>
    onChange({ ...settings, branches: { ...settings.branches, [key]: value } })

  const addRepository = () => {
    const parsed = parseRepository(adding, settings)

    if (parsed === null) {
      setAddError('Enter a repository name, or paste an Azure DevOps repository URL.')
      return
    }

    if (settings.repositories.some((repo) => sameRepository(repo, parsed))) {
      setAddError(`${parsed.name} is already on the list.`)
      return
    }

    setAddError(null)
    setAdding('')
    onChange({ ...settings, repositories: [...settings.repositories, parsed] })
  }

  const removeRepository = (target: RepositoryRef) =>
    onChange({
      ...settings,
      repositories: settings.repositories.filter((repo) => !sameRepository(repo, target)),
    })

  // Shows the formats working against today rather than describing them.
  const preview = formatBranchName(settings.branchNameFormat, todayIso())
  const candidatePreview = formatCandidateName(
    settings.candidateBranchNameFormat,
    preview,
    todayIso(),
  )

  return (
    <>
      <p className="lede">
        The branch names, naming formats and repositories the other tabs work from. Stored on the
        server, so they follow the tool rather than this browser.
      </p>

      <section className="panel">
        <h2>Deployment branches</h2>

        <p className="summary">
          Name the branch each environment deploys from. The Azure DevOps tab uses DEV, SIT and UAT
          to filter pull requests by their target branch. <strong>PROD</strong> is different: it is
          the branch a new deployment branch is cut from on the Deployment tab.
        </p>

        <div className="fields">
          {ENVIRONMENTS.map(({ key, label, hint }) => (
            <label key={key}>
              {label} branch <span className="hint">{hint}</span>
              <input
                value={settings.branches[key]}
                placeholder="Branch name"
                autoComplete="off"
                onChange={(e) => updateBranch(key, e.target.value)}
              />
            </label>
          ))}
        </div>

        <p className="summary">
          Leave DEV, SIT and UAT blank to switch the filter off and see every pull request.{' '}
          <code>{UNFILTERED_REPOSITORY}</code> is always shown whatever its target branch.
        </p>
      </section>

      <section className="panel">
        <h2>Deployment branch name</h2>

        <p className="summary">
          The name proposed on the Deployment tab, filled in from the deployment date. It stays
          editable there, so this only sets the starting point.
        </p>

        <div className="fields">
          <label>
            Format <span className="hint">tokens are replaced with the date</span>
            <input
              value={settings.branchNameFormat}
              placeholder={DEFAULT_BRANCH_NAME_FORMAT}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => onChange({ ...settings, branchNameFormat: e.target.value })}
            />
          </label>
        </div>

        <div className="fields">
          <label>
            Candidate branch format <span className="hint">cut from the deployment branch</span>
            <input
              value={settings.candidateBranchNameFormat}
              placeholder={DEFAULT_CANDIDATE_BRANCH_NAME_FORMAT}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => onChange({ ...settings, candidateBranchNameFormat: e.target.value })}
            />
          </label>
        </div>

        <p className="summary">
          Today those read <code>{preview}</code> and <code>{candidatePreview}</code>.
        </p>

        <p className="hint">
          Tokens:{' '}
          {FORMAT_TOKENS.map((token, index) => (
            <span key={token}>
              {index > 0 && ' '}
              <code>{token}</code>
            </span>
          ))}
        </p>
      </section>

      <section className="panel">
        <h2>Repositories</h2>

        <p className="summary">
          The repositories the Deployment tab can create a branch in. Paste an Azure DevOps
          repository URL, or type a name to use the defaults below.
        </p>

        <div className="fields">
          <label>
            Add a repository
            <input
              value={adding}
              placeholder="sample-web, or a repository URL"
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => {
                setAdding(e.target.value)
                setAddError(null)
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  addRepository()
                }
              }}
            />
          </label>
        </div>

        <div className="actions">
          <button onClick={addRepository} disabled={adding.trim() === ''}>
            Add repository
          </button>
          <span className="connected">{settings.repositories.length} repository(s)</span>
        </div>

        {addError && <p className="warn">{addError}</p>}

        {settings.repositories.length === 0 ? (
          <p className="summary">Nothing yet — the Deployment tab will have no repositories to offer.</p>
        ) : (
          <div className="scroller">
            <table>
              <thead>
                <tr>
                  <th>Repository</th>
                  <th>Project</th>
                  <th>Organisation</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {settings.repositories.map((repo) => (
                  <tr key={repositoryKey(repo)}>
                    <td>{repo.name}</td>
                    <td>{repo.project}</td>
                    <td>{repo.organization}</td>
                    <td>
                      <button className="link" onClick={() => removeRepository(repo)}>
                        remove
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <fieldset className="options">
          <legend>Defaults for a repository added by name</legend>

          <div className="fields">
            <label>
              Organisation
              <input
                value={settings.defaultOrganization}
                placeholder="your-organization"
                autoComplete="off"
                onChange={(e) => onChange({ ...settings, defaultOrganization: e.target.value })}
              />
            </label>

            <label>
              Project
              <input
                value={settings.defaultProject}
                placeholder="Platform"
                autoComplete="off"
                onChange={(e) => onChange({ ...settings, defaultProject: e.target.value })}
              />
            </label>
          </div>
        </fieldset>
      </section>

      {error && <p className="error">{error}</p>}

      <p className="hint">
        {saving
          ? 'Saving…'
          : savedAt
            ? `Saved to the server at ${savedAt}. These settings follow the app, not this browser.`
            : 'Settings are stored on the server as a JSON file. Your API tokens never are.'}
      </p>
    </>
  )
}
