import { useEffect, useRef, useState } from 'react'
import {
  api,
  type ChangedFilesResponse,
  type ConfigResponse,
  type DevOpsIdentity,
  type DevOpsLookupResponse,
  type FixedByNote,
  type PullRequestRef,
} from './api'
import AtlassianTab from './AtlassianTab'
import AzureDevOpsTab from './AzureDevOpsTab'
import BackToTop from './BackToTop'
import DeploymentTab from './DeploymentTab'
import SettingsTab from './SettingsTab'
import ThemeToggle from './ThemeToggle'
import { EMPTY_SETTINGS, type AppSettings } from './settings'
import { applyTheme, loadTheme, saveTheme, type Theme } from './theme'
import './App.css'

type Tab = 'atlassian' | 'azureDevOps' | 'deployment' | 'settings'

export default function App() {
  const [tab, setTab] = useState<Tab>('atlassian')

  // Produced by the Atlassian tab's resolve, consumed by the Azure DevOps tab.
  // Each tab holds its own credentials; only this crosses between them.
  const [pullRequests, setPullRequests] = useState<PullRequestRef[]>([])
  const [fixedByNotes, setFixedByNotes] = useState<FixedByNote[]>([])
  const [resolved, setResolved] = useState(false)

  // Held here rather than in the tab, so the Deployment tab can tick the
  // repositories that actually have a pull request in this release.
  const [devOpsResult, setDevOpsResult] = useState<DevOpsLookupResponse | null>(null)

  // Also shared: the Deployment tab gates its bulk merge on the file overlaps.
  const [changes, setChanges] = useState<ChangedFilesResponse | null>(null)
  const [identity, setIdentity] = useState<DevOpsIdentity | null>(null)

  // Most of a release is someone else's work, so this starts on. Shared rather
  // than per-tab: the Deployment tab must offer the same pull requests the
  // Azure DevOps tab is showing, or the two disagree about what the release is.
  const [onlyMine, setOnlyMine] = useState(true)

  // The choice, read straight from storage at first render - a lazy initialiser
  // rather than an effect, so there is no render to correct afterwards.
  const [theme, setTheme] = useState<Theme>(loadTheme)

  // Which credentials the server already holds. Fetched once and shared, since
  // both working tabs need it and it never changes while the page is open.
  const [config, setConfig] = useState<ConfigResponse | null>(null)

  const [settings, setSettings] = useState<AppSettings>(EMPTY_SETTINGS)
  const [saving, setSaving] = useState(false)
  const [savedAt, setSavedAt] = useState<string | null>(null)
  const [settingsError, setSettingsError] = useState<string | null>(null)

  useEffect(() => {
    // A failure here is not fatal: the tabs fall back to asking for credentials.
    void api
      .config()
      .then((loaded) => {
        setConfig(loaded)

        // 'system' can only be resolved properly once the host's own setting has
        // arrived; until then the CSS media query has been in charge.
        applyTheme(loadTheme(), loaded.osTheme)

        // Only worth asking who the token belongs to once we know there is one.
        if (loaded.devOps.configured) {
          void api.devOpsMe().then(setIdentity).catch(() => setIdentity(null))
        }
      })
      .catch(() => setConfig(null))

    void api
      .settings()
      .then(setSettings)
      .catch((failure: unknown) =>
        setSettingsError(failure instanceof Error ? failure.message : String(failure)),
      )
  }, [])

  // Typing in Settings should not be one PUT per keystroke.
  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  const updateSettings = (next: AppSettings) => {
    setSettings(next)
    setSettingsError(null)

    if (saveTimer.current !== null) {
      clearTimeout(saveTimer.current)
    }

    saveTimer.current = setTimeout(() => {
      setSaving(true)

      void api
        .saveSettings(next)
        // The server normalises what it stores, so take back what it saved.
        .then((stored) => {
          setSettings(stored)
          setSavedAt(new Date().toLocaleTimeString())
        })
        .catch((failure: unknown) =>
          setSettingsError(failure instanceof Error ? failure.message : String(failure)),
        )
        .finally(() => setSaving(false))
    }, 600)
  }

  const chooseTheme = (next: Theme) => {
    setTheme(next)
    saveTheme(next)
    applyTheme(next, config?.osTheme)
  }

  const onResolved = (found: PullRequestRef[], notes: FixedByNote[]) => {
    setPullRequests(found)
    setFixedByNotes(notes)
    setResolved(found.length > 0 || notes.length > 0)
  }

  const tabs: { key: Tab; label: string; badge?: number }[] = [
    { key: 'atlassian', label: 'Atlassian' },
    { key: 'azureDevOps', label: 'Azure DevOps', badge: pullRequests.length },
    { key: 'deployment', label: 'Deployment' },
    { key: 'settings', label: 'Settings' },
  ]

  return (
    <main className="app">
      <header className="masthead">
        <h1>Release Tool</h1>

        <ThemeToggle theme={theme} osTheme={config?.osTheme} onChange={chooseTheme} />
      </header>

      <nav className="tabs" role="tablist">
        {tabs.map(({ key, label, badge }) => (
          <button
            key={key}
            role="tab"
            aria-selected={tab === key}
            className={tab === key ? 'tab active' : 'tab'}
            onClick={() => setTab(key)}
          >
            {label}
            {badge !== undefined && badge > 0 && <span className="badge">{badge}</span>}
          </button>
        ))}
      </nav>

      {/* Every tab stays mounted so switching does not discard a loaded page, a
          typed token, or a resolved preview. */}
      <div hidden={tab !== 'atlassian'}>
        <AtlassianTab onResolved={onResolved} config={config} />
      </div>

      <div hidden={tab !== 'azureDevOps'}>
        <AzureDevOpsTab
          pullRequests={pullRequests}
          fixedByNotes={fixedByNotes}
          resolved={resolved}
          branches={settings.branches}
          config={config}
          result={devOpsResult}
          onResult={setDevOpsResult}
          changes={changes}
          onChanges={setChanges}
          identity={identity}
          onlyMine={onlyMine}
          onOnlyMineChange={setOnlyMine}
        />
      </div>

      <div hidden={tab !== 'deployment'}>
        <DeploymentTab
          settings={settings}
          config={config}
          identity={identity}
          pullRequests={pullRequests}
          devOpsResult={devOpsResult}
          changes={changes}
          onlyMine={onlyMine}
        />
      </div>

      <div hidden={tab !== 'settings'}>
        <SettingsTab
          settings={settings}
          onChange={updateSettings}
          saving={saving}
          savedAt={savedAt}
          error={settingsError}
        />
      </div>

      <BackToTop />
    </main>
  )
}
