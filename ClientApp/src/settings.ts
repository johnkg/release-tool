export type Environment = 'dev' | 'sit' | 'uat' | 'prod'

export interface DeploymentBranches {
  dev: string
  sit: string
  uat: string
  prod: string
}

export interface RepositoryRef {
  organization: string
  project: string
  name: string
}

export interface AppSettings {
  branches: DeploymentBranches
  branchNameFormat: string
  candidateBranchNameFormat: string
  defaultOrganization: string
  defaultProject: string
  repositories: RepositoryRef[]
}

/**
 * PROD is listed apart from the others: it is not a filter like DEV/SIT/UAT,
 * it is the branch a new deployment branch is cut from.
 */
export const ENVIRONMENTS: { key: Environment; label: string; hint: string }[] = [
  { key: 'dev', label: 'DEV', hint: 'e.g. develop' },
  { key: 'sit', label: 'SIT', hint: 'e.g. release/sit' },
  { key: 'uat', label: 'UAT', hint: 'e.g. release/uat' },
  { key: 'prod', label: 'PROD', hint: 'deployment branches are cut from this' },
]

/** The three that filter the Azure DevOps list. PROD is not one of them. */
export const FILTER_ENVIRONMENTS = ENVIRONMENTS.filter((e) => e.key !== 'prod')

/**
 * What the Azure DevOps tab filters on before anyone touches the dropdown:
 * a release is checked against UAT far more often than the other two.
 */
export const DEFAULT_ENVIRONMENT: Environment = 'uat'

export const DEFAULT_BRANCH_NAME_FORMAT = 'dev/release/feat/PROJECT-RELEASE-{DDMMYYYY}'

/** The candidate branch is cut from the deployment branch, hence {DEPLOYMENT}. */
export const DEFAULT_CANDIDATE_BRANCH_NAME_FORMAT = '{DEPLOYMENT}-candidate'

export const EMPTY_BRANCHES: DeploymentBranches = { dev: '', sit: '', uat: '', prod: '' }

export const EMPTY_SETTINGS: AppSettings = {
  branches: EMPTY_BRANCHES,
  branchNameFormat: DEFAULT_BRANCH_NAME_FORMAT,
  candidateBranchNameFormat: DEFAULT_CANDIDATE_BRANCH_NAME_FORMAT,
  defaultOrganization: 'your-organization',
  defaultProject: 'Platform',
  repositories: [],
}

/**
 * This repository is exempt from the branch filter - its pull requests target
 * their own branches and would otherwise disappear from the release.
 */
export const UNFILTERED_REPOSITORY = 'deploy-scripts'

/** Branch names that actually filter something. PROD is excluded by design. */
export function configuredBranches(branches: DeploymentBranches): string[] {
  return FILTER_ENVIRONMENTS.map(({ key }) => branches[key].trim()).filter((name) => name !== '')
}

export function sameBranch(a: string | null, b: string): boolean {
  return a !== null && a.trim().toLowerCase() === b.trim().toLowerCase()
}

/** Tokens the branch name format understands, in the order they are replaced. */
export const FORMAT_TOKENS = ['{DDMMYYYY}', '{YYYYMMDD}', '{DD}', '{MM}', '{YYYY}', '{YY}'] as const

/**
 * Fills a branch name format from the deployment date. Takes the date as
 * 'YYYY-MM-DD' - the value an <input type="date"> gives - and never as a Date,
 * which would drag the browser's timezone into a purely calendar decision and
 * can shift the day by one.
 */
export function formatBranchName(format: string, isoDate: string): string {
  const [year, month, day] = isoDate.split('-')

  if (!year || !month || !day) {
    return format
  }

  const values: Record<(typeof FORMAT_TOKENS)[number], string> = {
    '{DDMMYYYY}': `${day}${month}${year}`,
    '{YYYYMMDD}': `${year}${month}${day}`,
    '{DD}': day,
    '{MM}': month,
    '{YYYY}': year,
    '{YY}': year.slice(-2),
  }

  return FORMAT_TOKENS.reduce((name, token) => name.split(token).join(values[token]), format)
}

/**
 * The candidate branch name. Takes the deployment branch as a token, so the two
 * names stay in step when the deployment date changes.
 */
export function formatCandidateName(format: string, deploymentBranch: string, isoDate: string): string {
  return formatBranchName(format.split('{DEPLOYMENT}').join(deploymentBranch), isoDate)
}

/** Today as 'YYYY-MM-DD' in local time, for seeding the date picker. */
export function todayIso(): string {
  const now = new Date()
  const pad = (value: number) => String(value).padStart(2, '0')

  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

/**
 * Accepts either a bare repository name or a pasted Azure DevOps URL, so adding
 * a repository is a copy-paste from the browser rather than three fields.
 * Recognises both the legacy '{org}.visualstudio.com/{project}/_git/{repo}' and
 * 'dev.azure.com/{org}/{project}/_git/{repo}'.
 */
export function parseRepository(input: string, settings: AppSettings): RepositoryRef | null {
  const text = input.trim()

  if (text === '') {
    return null
  }

  const legacy = /https?:\/\/([^./]+)\.visualstudio\.com\/([^/]+)\/_git\/([^/?#]+)/i.exec(text)
  const modern = /https?:\/\/dev\.azure\.com\/([^/]+)\/([^/]+)\/_git\/([^/?#]+)/i.exec(text)
  const match = legacy ?? modern

  if (match) {
    return {
      organization: decodeURIComponent(match[1]),
      project: decodeURIComponent(match[2]),
      name: decodeURIComponent(match[3]),
    }
  }

  // A URL that did not match is a mistake worth reporting, not a repo name.
  if (/^https?:\/\//i.test(text)) {
    return null
  }

  return {
    organization: settings.defaultOrganization.trim(),
    project: settings.defaultProject.trim(),
    name: text,
  }
}

export function sameRepository(a: RepositoryRef, b: RepositoryRef): boolean {
  return (
    a.name.toLowerCase() === b.name.toLowerCase() &&
    a.project.toLowerCase() === b.project.toLowerCase() &&
    a.organization.toLowerCase() === b.organization.toLowerCase()
  )
}

export function repositoryKey(repo: RepositoryRef): string {
  return `${repo.organization}/${repo.project}/${repo.name}`.toLowerCase()
}
