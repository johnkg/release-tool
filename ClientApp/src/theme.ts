export type Theme = 'light' | 'dark' | 'system'

const STORAGE_KEY = 'releasetool.theme'

/**
 * The choice, not the appearance. 'system' is the default and means "decide at
 * apply time" - see applyTheme.
 */
export function loadTheme(): Theme {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    return stored === 'light' || stored === 'dark' ? stored : 'system'
  } catch {
    // Private browsing throws on access; the default is still usable.
    return 'system'
  }
}

export function saveTheme(theme: Theme): void {
  try {
    if (theme === 'system') {
      window.localStorage.removeItem(STORAGE_KEY)
    } else {
      window.localStorage.setItem(STORAGE_KEY, theme)
    }
  } catch {
    // Storage full or unavailable - the choice still applies for this session.
  }
}

/**
 * Applied to the root element rather than to a React tree, so the page
 * background and the native controls follow too.
 *
 * GOTCHA: 'system' cannot simply be left to `prefers-color-scheme`. Chrome and
 * Edge have an appearance setting of their own, and when it is set to Light the
 * page is told "light" however Windows is configured - which is exactly the
 * case where "follow system" looked broken. So when the server has told us the
 * host's real setting (osTheme, reported to loopback callers only), that wins;
 * with no answer we fall back to the media query by removing the attribute.
 */
export function applyTheme(theme: Theme, osTheme?: string | null): void {
  const root = document.documentElement

  if (theme !== 'system') {
    root.setAttribute('data-theme', theme)
    return
  }

  if (osTheme === 'light' || osTheme === 'dark') {
    root.setAttribute('data-theme', osTheme)
  } else {
    root.removeAttribute('data-theme')
  }
}

/** What 'system' actually resolved to, for the toggle to explain itself. */
export function describeSystem(osTheme?: string | null): string {
  if (osTheme === 'light' || osTheme === 'dark') {
    return `follows this machine (${osTheme})`
  }

  return 'follows the browser'
}
