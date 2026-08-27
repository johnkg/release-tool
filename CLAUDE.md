# Release Tool — Project Context

Everything below was verified against a live Atlassian site and Azure DevOps
organisation, not assumed. The specifics of that site have been replaced with
placeholders — `your-domain.atlassian.net`, `your-organization`, `PROJECT-####` —
so configure your own in `appsettings.json` before anything works.

## Goal

A tool that fills in the **Developer Assigned** column of the `IV. Approvals`
table on a release page in Confluence, by deriving each developer from the
PR / "fixed on" comments on the corresponding Jira ticket.

Rules:

- Only process tickets matching `^PROJECT-\d+`. **Skip OTHER_PROJECT tickets entirely.**
- Developer = author of the ticket comment containing an Azure DevOps pull
  request URL.
- If there is no PR comment, but there is a comment referencing another ticket
  ("Fixed on PROJECT-1834", "Prs: Included in PROJECT-1853"), use that comment's author.
- If neither exists, **default to the configured `FallbackDeveloperName`**.
- Write the developer as a real **mention node**, not plain text.

### Other managed columns (added after the first build)

- **Requested By** — the Jira **reporter**, as a mention. Comes free on the same
  `search/jql` call (`fields: ["comment", "reporter"]`). A ticket with no
  reporter is left alone rather than defaulted.
- **PR Approved By** — the approver is **searched in Jira**, not typed:
  `GET /api/jira/users?query=` wraps `/rest/api/3/user/search`, and the UI is a
  debounced type-ahead (300 ms, minimum 2 characters). Picking from the list is
  what guarantees the mention has a real `accountId`. Deactivated accounts and
  `accountType != "atlassian"` (apps and bots) are filtered out, since
  mentioning either leaves a dead link on the page.
  It is written **only to rows whose resolved developer is the connected user** —
  approver and scope are independent, so you can record that a colleague approved
  your own PRs. The client sends the qualifying ticket keys, so the preview and
  the write cannot disagree.
- **PR Approved Status** → "Approved" and **Merged to Deployment Branch** →
  "Merged" are written together whenever a PR approver is recorded.
  GOTCHA: on the real page these columns hold **status lozenges**, and the
  allowed values are listed as a legend **in the header cell** (`REJECTED`/
  `APPROVED`, `MERGED`) while the data cells sit empty. Searching only data rows
  for an example therefore finds nothing and writes plain text into a column of
  pills — which is exactly what happened on the first live run.
  So `StatusNodesIn` scans the whole column, header included, and:
  1. a lozenge whose text matches the value is copied **whole**, keeping the
     page's own wording and colour (so a run writes `APPROVED`, not `Approved`);
  2. otherwise, if the column has any lozenge, a new green one is created —
     never a clone of a different value, since copying red `REJECTED` to write
     "Approved" would state the opposite;
  3. only a column with no lozenge at all gets plain text.
  `localId` is dropped from any copy, since it must be unique per node.
  Header text also carries the legend words, and often a team suffix, so column
  matching must tolerate both: a real header reads "PR Approved Status (XY)".
- **Clear** — `POST /api/approvals/{pageId}/clear` empties chosen columns on
  every in-scope row. For resetting a test page; the UI has a checkbox per column.

Columns are located by header text, never by position. Exact header matches are
taken first: "PR Approved By" and "PR Approved Status" share a prefix, so a
contains-match alone puts both on the same column.

## Stack decision

- Backend: ASP.NET Core 10 minimal API (`src/ReleaseTool.Api`), `net10.0`.
  SDK pinned in `global.json` — the IIS Hosting Bundle on the target server
  must match (ASP.NET Core 10).
- Frontend: React + TypeScript via Vite (`ClientApp/`), built into `wwwroot`
  on publish, served same-origin
- Auth: **Atlassian API tokens** for now. Each user supplies their own token via
  the UI; never commit a shared token. OAuth 3LO is a later migration and does
  not change the ADF logic.
- Target: eventually IIS-hosted (Azure Pipelines → ASP.NET Core Hosting Bundle,
  app pool = No Managed Code), but build and validate locally first.

## Atlassian API notes

Base URL is the site itself — **no cloud ID needed on the API-token route**:

```
https://your-domain.atlassian.net/rest/api/3/...     (Jira)
https://your-domain.atlassian.net/wiki/api/v2/...    (Confluence)
Authorization: Basic base64(email:apiToken)
```

A cloud ID is only required for the `api.atlassian.com/ex/...` gateway, which is
the OAuth path. If needed later, fetch your site's unauthenticated from
`https://your-domain.atlassian.net/_edge/tenant_info`.

Note: `https://api.atlassian.com/oauth/token/accessible-resources` requires an
OAuth bearer token and will **not** answer a Basic-auth request.

## Pipeline (order matters)

1. **Resolve space** — `GET /wiki/api/v2/spaces?keys={key}` → numeric `id`.
   GOTCHA: the space *key* (`~sandbox-space`) is not the *ID*
   (`9000001`). The pages endpoint rejects the key with
   `Expected type is long`.
2. **Find page** — `GET /wiki/api/v2/spaces/{id}/pages?title={title}`.
   Unpublished drafts do not appear in the API or in CQL search. Surface a
   clear "not found — is it published?" message rather than an empty result.
3. **Fetch body as ADF** — `GET /wiki/api/v2/pages/{id}?body-format=atlas_doc_format`.
   Keep `version.number`; you need it for the write.
4. **Locate the table** — walk the ADF `content` array for a `heading` whose
   text contains "Approvals", then take the next `table` node.
   Do NOT index tables positionally — the page has 8 tables and their order
   varies between releases.
5. **Extract tickets** — cell index 0 contains an `inlineCard` node whose
   `attrs.url` is the Jira link. It is NOT text.
   GOTCHA: this is why the column looks empty in markdown/plain-text views.
   Regex the key from the URL, then filter to `^PROJECT-\d+`.
6. **Query Jira once** — `POST /rest/api/3/search/jql` with
   `key in (...)` and `fields: ["comment"]`. One call for all tickets,
   not one call per ticket.
7. **Derive developer** — comment bodies are ADF; flatten to text first.
   - Primary: body matches
     `https://your-organization\.visualstudio\.com/([^/]+)/_git/([^/]+)/pullrequest/(\d+)`
     → developer = comment author.
   - Secondary: body matches `fixed on|included in|prs:` and references another
     ticket key → developer = that comment's author.
     Allow generous distance between the phrase and the key: the ticket link is
     wrapped in a smartlink node, so the key can be 80+ chars downstream.
   - Fallback: the configured `FallbackDeveloperName`.
8. **Resolve account IDs** — `GET /rest/api/3/user/search?query={name}`, cached.
   Do not hardcode them anywhere; they rot, and they identify real people.
   The comment author already carries one, so the search is only needed for the
   configured fallback developer.
9. **Mutate** — replace the contents of cell index 1 with:
   ```json
   [{ "type": "paragraph",
      "content": [{ "type": "mention",
                    "attrs": { "id": "<accountId>", "text": "@Display Name" } }] }]
   ```
   Touch nothing else in the tree.
10. **Write back** — `PUT /wiki/api/v2/pages/{id}` with
    `version.number = current + 1`, a version message, and
    `body.representation = "atlas_doc_format"`.
    A 409 means a concurrent edit — refetch and retry.

### Implementation notes (Phase 5, built)

- `Adf/AdfText.cs` flattens ADF. It must emit `inlineCard`/`blockCard` URLs,
  `link` mark hrefs, and `status`/`mention`/`emoji` attr text — not just text
  nodes. The PR link and the "fixed on" ticket key live in card attrs, and a
  status lozenge holds its word in `attrs.text`, so a text-only flatten reports
  a populated status column as empty.
- `Adf/ApprovalsTable.cs` locates the table, reads rows, writes the mention.
  The developer column is read from the header ("Developer") and falls back to
  index 1. Header rows are detected as all-`tableHeader` cells and skipped.
- Account IDs come free: the comment author *is* the developer, so
  `author.accountId` is already on the comment. The user-search lookup is only
  needed for the configured fallback name.
- `search/jql` truncates long comment threads. When
  `fields.comment.total` exceeds the returned count, refetch that issue via
  `/issue/{key}/comment` or a PR link deep in the thread is missed.
- v2 returns and accepts the ADF body as a JSON **string**, not an object.
- GOTCHA: Confluence answers an unauthenticated request with **404, not 401**,
  so an invalid token looks exactly like a missing page. Call
  `POST /api/auth/verify` first to tell the two apart.

### Tests

`dotnet test` — 140 tests, sequential, no network. `tests/ReleaseTool.Api.Tests`.
Integration tests host the real app via `WebApplicationFactory` and swap only the
outbound HTTP handler (`StubAtlassian`), so middleware order, the credentials
filter, options validation and the endpoints are all exercised for real.

Gotchas found while writing them, all still true:

- `DeveloperSource` needs `JsonStringEnumConverter`, or it crosses the wire as
  0/1/2 and an incoming `"PullRequest"` fails to bind on apply.
- Serilog's request logging writes through the **static** `Log.Logger`, which
  every hosted app overwrites at startup. Test classes running in parallel mix
  their request logs together, hence
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
- Serilog.Settings.Configuration finds sinks by scanning the entry assembly's
  dependencies, which is not this app under `dotnet test`. `appsettings.json`
  names them in `Serilog:Using` instead of relying on the scan.
- Tests observe logs through an `ILogEventSink` registered in DI, picked up by
  the existing `ReadFrom.Services` call — no files, no config.
- `TestApp` clears the `Credentials` section and points `Settings:FilePath` at a
  temp file. Both are load-bearing: an unset settings path would have a test run
  overwrite the settings the tool is actually using, and a credential the
  developer has configured makes every "no credentials → 401" test pass through
  as authenticated.
  GOTCHA: the clearing must be a **configuration source added last**, not
  `UseSetting`. The test host's content root is the API project directory, so
  `Program.cs` finds the developer's real `appsettings.Local.json` there and adds
  it at the end of the chain — where it beats any host setting. `UseSetting` was
  enough until that file existed; adding it broke nine tests at once. Tests that
  want configured credentials pass them as `TestApp(settings: …)`, which lands in
  the same late source.

## Jira ticket status

Bottom of the Atlassian tab, after Review. Moves **only the tickets where the
connected user is the resolved developer** between the two release statuses:
`YOUR_DEPLOYED_STATUS` and back to `YOUR_READY_STATUS`. Scope is
the same `myTickets` list the PR approval uses, and the client sends those keys,
so the preview and the write cannot disagree.

- `POST /api/jira/statuses` — current status **and resolution** per ticket, one
  `search/jql` call with `fields: ["status", "resolution"]`. Read-only, so the
  table fills itself once the resolve says which rows are this person's.
- `POST /api/jira/transition` — `{ ticketKeys, target }` where target is
  `DeployedToProduction` | `ReadyForDeployment`. `StatusTransitioner` does it.
- `POST /api/jira/resolution` — `{ ticketKeys, resolved }`. The way back for the
  resolution alone; see below.

GOTCHA: **Jira has no "set the status" call.** A status is only reachable
through a *transition*, and which transitions exist depends on where the ticket
stands and on the caller's permissions. So per ticket:

1. already in the target status → nothing is sent at all (`Unchanged: true`);
2. otherwise `GET /rest/api/3/issue/{key}/transitions`, and the chosen move is
   the one whose **`to.name`** matches — a transition's own name is usually a
   verb ("Deploy"), not the destination, so the name is only a fallback;
3. `POST /rest/api/3/issue/{key}/transitions` with `{"transition":{"id":...}}`.
   Jira answers **204 with no body**.

A ticket the workflow will not move is reported with the transitions Jira *does*
offer — that message is the only actionable part of the failure. Failures are
per ticket and never abort the batch: unlike a cherry-pick run, tickets are
independent, so one refusal must not strand the rest.

### Resolution

Going live, Jira also asks for a **Resolution** — the dropdown on the transition
screen, defaulted to unresolved — so the tool fills it in as part of the same
move. `Atlassian:ResolutionName`, default `Done`.

GOTCHA: **the dropdown belongs to the transition, not to the issue**, and
sending a field the transition's screen does not have is a hard 400 ("Field
'resolution' cannot be set. It is not on the appropriate screen, or unknown").
So `GET .../transitions?expand=transitions.fields` — without that expand there
is no way to know — and then:

- screen **has** the dropdown → the resolution goes in the transition's `fields`,
  one call, using the id from its `allowedValues`;
- screen **has not** → transition first, then `PUT /rest/api/3/issue/{key}` with
  `fields.resolution`. That needs Resolution on the *edit* screen, so it can
  fail where the transition would not; the status moved, so the message says so,
  but the row is reported as a failure because a person still has to finish it.
- the configured resolution is **not among the dropdown's `allowedValues`** →
  nothing is moved at all. A required field would refuse the call anyway, and a
  status change without its resolution is worse than no change.

GOTCHA: **there is no resolution called "Unresolved"** — that state is the field
holding nothing. So the way back is `fields.resolution = null` on the issue, not
a transition, which is why *Set resolution to Unresolved* is its own button and
leaves the status alone. Putting a ticket fully back therefore takes both
buttons; `ReadyForDeployment` deliberately does not touch the resolution, since
the user asked for the two to be separate controls.

A ticket **already** in `YOUR_DEPLOYED_STATUS` but still unresolved does get
its resolution set — it reached that status by another route, or by an earlier
run that only moved the status. Only a ticket already both moved *and* resolved
is left completely alone.

`GET /rest/api/3/resolution` turns the configured name into an id and is cached
12 hours. A name the site does not define fails with the ones it does have, per
ticket rather than as a whole-batch 400 — that endpoint can 403 on a locked-down
site, and one refusal must not sink a run whose transition screens would have
worked anyway.

### Configuration

The three names are **configuration**, not constants:
`Atlassian:DeployedToProductionStatus`, `Atlassian:ReadyForDeploymentStatus` and
`Atlassian:ResolutionName`. `/api/config` reports them under `workflow` so the
buttons are labelled with the very words the match is made on, rather than a
second copy in the client that can drift.
Matching ignores case *and whitespace*, so a workflow spelling it
"UAT Done / Ready for Deployment" still matches the configured
"YOUR_READY_STATUS"; two genuinely different statuses cannot collide
on that.

GOTCHA: this writes to the **Jira tickets themselves**. Loading the sandbox copy
of the release page changes nothing about that — the page only decides which
tickets are in scope. The panel says so out loud.

## Azure DevOps tab

Second tab, fed by the Atlassian tab's resolve — no second trip to Jira.

- `DeveloperResolver` now returns a `ResolutionResult`: assignments, **every**
  pull request link found (deduped; the same PR is often pasted twice), and a
  `FixedByNote` for each ticket that has a "fixed on / included in / prs:"
  comment but **no PR of its own**.
- The PR regex handles both `{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}`
  and `dev.azure.com/{org}/{project}/_git/...` — older tickets carry the legacy
  host. Named groups (`org1`/`org2`) keep both forms in one pattern.
- `POST /api/devops/pull-requests` reads each PR from Azure DevOps. It sits in
  its own route group with `DevOpsCredentialsFilter`, since it needs a **PAT**
  (`X-DevOps-Token`), not the Atlassian headers. Basic auth with an **empty
  username**: `base64(":" + pat)`.
- The API address is rebuilt from the link itself
  (`https://{org}.visualstudio.com/{project}/_apis/git/repositories/{repo}/pullrequests/{id}`),
  so no per-repository configuration exists.
- Grouped by repository, and within a repo ordered by **completion date** —
  the order a deployment replays them. Anything still open has no completion
  date and sorts last.
- One unreadable PR must not lose the release: failures are collected per PR and
  returned alongside the results, with the reason (403 → "lacks Code (read)").
- GOTCHA: a wrong PAT gets an HTML sign-in page with **200**, not a 401, so the
  response is checked for being JSON at all.

## Settings tab

Last tab. Holds the deployment branch names, the deployment branch name format,
and the repository list.

**Persisted server-side as a JSON file** — `SettingsStore`, default
`App_Data/settings.json` under the content root, overridable with
`Settings:FilePath`. No database: this is a handful of branch names and
repositories for one team, and a file is something an admin can read, edit and
back up without tooling. Notes:

- `GET`/`PUT /api/settings` need **no credentials** — settings hold no secrets,
  and the Deployment tab has to work without anyone touching the Atlassian tab.
- The store is a **singleton** holding a write lock, and writes to a `.tmp` file
  then moves it into place, so two browser tabs saving at once cannot leave a
  half-written file.
- `PUT` returns what was actually stored, and the UI takes that back: the store
  trims, drops blanks, dedupes repositories case-insensitively and fills a blank
  format with the default. Saving is debounced 600 ms in `App.tsx`, or it would
  be one PUT per keystroke.
- Corrupt JSON logs an error and yields defaults rather than failing the app.
- `App_Data/` is gitignored — it is per-installation runtime state.
- Superseded the old `localStorage` (`releasetool.branches`) entirely. The rule
  that **no token is ever written to storage** is unchanged.

**PROD is not a filter.** DEV/SIT/UAT filter the Azure DevOps list;
`FILTER_ENVIRONMENTS` is the subset used for that. PROD is the branch a
deployment branch is **cut from**, and is only used by the Deployment tab.

- Filtering is **client-side** on the already-fetched list, so changing the
  dropdown is instant and costs no Azure DevOps calls.
- The dropdown defaults to **UAT** (`DEFAULT_ENVIRONMENT` in `settings.ts`),
  since that is the environment a release is checked against most often.
  GOTCHA: the default cannot be a `useState` seed — branch settings are read
  from `localStorage` in an effect, so at first render UAT still looks unset.
  The tab keeps `selection` as `null` until someone picks, and derives the
  effective value each render; that also lets it fall back to *all* while no
  UAT branch is configured, instead of filtering the list down to nothing and
  looking like a failed fetch. Once the user chooses, their choice stands
  even if that environment has no branch — the existing warning covers it.
- `deploy-scripts` (`UNFILTERED_REPOSITORY`) is exempt and always shown — it targets
  its own branches. Move this to Settings if a second such repo ever appears.
- With **no** branch names set, filtering is off entirely rather than hiding
  everything, which would look like a broken fetch.
- Repositories left with no matching PR are dropped from the list.
### Repository list

Free-form, because the Deployment tab needs repositories the release's pull
requests never mention. `parseRepository` accepts either a bare name (which
inherits the configured default organisation and project) or a **pasted Azure
DevOps URL**, in both the legacy `{org}.visualstudio.com` and `dev.azure.com`
forms — adding a repo is a copy-paste from the browser. Organisation and project
are stored **per repository**, so one living elsewhere still works.

### Branch name format

`branchNameFormat`, default `dev/release/feat/PROJECT-RELEASE-{DDMMYYYY}`. Tokens:
`{DDMMYYYY}` `{YYYYMMDD}` `{DD}` `{MM}` `{YYYY}` `{YY}`.
GOTCHA: `formatBranchName` takes the date as the `YYYY-MM-DD` **string** an
`<input type="date">` gives, never a `Date` — parsing that string into a `Date`
drags the browser's timezone into a purely calendar decision and can shift the
day by one, which would name the branch after the wrong day.

### One fetch, not two

*Fetch pull requests and files* is a single action: the pull requests, then the
files they changed. They were two buttons and there was never a reason to press
only one — the file list is what unlocks merging a repository in one go. The file
lists are folded into each repository's panel as a collapsed `<details>` rather
than sitting in panels of their own.

### Only my pull requests

A checkbox, **on by default**: most of a release is someone else's work. It
filters the already-fetched list, so toggling costs no calls, and it narrows
which pull requests the *file* lookup reads — two requests per PR, so that is
the expensive half. It cannot narrow the first call: an author is only known
once the pull request has been read.

Matching is on `authorEmail` (`createdBy.uniqueName`) first and the display name
second — display names are not reliably the same string in the profile API and
on a repository identity. With no identity resolved, the filter is disabled
rather than silently hiding everything. Changing the checkbox after a fetch
warns that the file list was gathered under the other scope.

The checkbox lives on the Azure DevOps tab but the **state is held in `App.tsx`**
and shared with the Deployment tab, which offers the same pull requests to merge.
Two tabs disagreeing about what is in the release is worse than either scope.
`isAuthoredBy` in `api.ts` is the single predicate both use.

It scopes the *merge* list only. Repository selection still follows the whole
release, because cutting the deployment branch is a release-wide act — a repo
can be ticked for branch creation while showing nothing to merge.

### Changed files

`POST /api/devops/changed-files` takes the same PR references the lookup does and
returns, per repository, every file the release touches with **the tickets that
touched it**. Read from the PR's *latest iteration*
(`/pullRequests/{id}/iterations` → highest id → `/changes`), so it is the PR as
it stands rather than the sum of every revision.

A file carrying more than one ticket is `overlapping`, and a repository with any
such file has `hasOverlap`. That flag is the only thing that unlocks the
Deployment tab's **Merge all** button — overlapping files are exactly the ones
that make a bulk replay conflict-prone. Overlapping files sort first.

### Commit ids

The lookup carries `sourceCommit` (head of the PR's source branch),
`mergeCommit`, `squashed` and `organization`. Full SHAs cross the wire; `Sha.tsx`
shows the first eight and expands to the whole thing on click, copying it at the
same time. Carrying them on the lookup is what stops the Deployment tab having
to re-read every pull request.

**Which commit gets replayed** is `replayCommit()` in `api.ts`: the squashed
commit when `completionOptions.squashMerge` was set, otherwise the source branch
head. A squashed pull request landed on its own target as a *single* commit, so
replaying the source branch head would bring back the individual commits the
squash exists to collapse. On a real release roughly a third of the pull requests
were squashed, and for those the two ids genuinely differ — so this is a real
distinction, not a theoretical one.

## Deployment tab

Third tab, between Azure DevOps and Settings. Creates — and removes — the
deployment branch across the repositories in a release.

**It stands alone.** Repositories come from Settings, the source branch is
Settings' PROD, and the PAT is the configured one or its own field. The other
two tabs only *pre-tick* boxes: `withPullRequests` prefers the Azure DevOps
fetch when it has been run and falls back to the Atlassian resolve, and either
being empty is fine.

- The DevOps lookup result was lifted into `App.tsx` (`result` / `onResult`) so
  this tab can see which repositories are in the release. It is set from the
  fetch handler, not an effect.
- Checkbox state is an **override map** keyed by `org/project/name`, not a list
  of selections — a repo absent from it follows the pull requests, so a later
  resolve still moves the defaults.
- The branch name field holds `null` until typed, meaning "follow the date";
  once typed it is pinned, with a link back. Same untouched-default pattern as
  the UAT dropdown.
- Two branches: the **deployment** branch, cut from PROD, and the **candidate**
  branch, cut from the deployment branch. The candidate's name comes from
  `candidateBranchNameFormat` (default `{DEPLOYMENT}-candidate`), which takes the
  deployment branch as a `{DEPLOYMENT}` token plus all the date tokens, so the
  two names stay in step when the date changes. Each has Create and Delete.
- **Check** is read-only status per repo. Create and Delete both confirm, naming
  the branch and the repository count.
- The merge table is **deduplicated by pull request id**. The lookup returns one
  entry per *ticket*, so a PR cited by two tickets arrives twice — which meant
  duplicate React keys and, worse, *Merge all* replaying the same commit twice.
  The citing ticket keys are joined into the Ticket column instead.
- The merge table highlights a pull request whose files **another ticket also
  changed**, with a count per row and the paths on hover. The repository badge
  says a repo has overlaps; the row says which pull requests carry them, which
  is what a person needs when merging one at a time.

### Cherry-picking pull requests onto a branch

`POST /api/devops/merge` (route kept; the operation changed).

**It was a merge and that was wrong.** `merges` with
`parents: [targetHead, prCommit]` merges two *histories*. The deployment branch
is cut from PROD and the commit sits on UAT, so their merge base is wherever
those diverged — the merge drags in everything that landed on UAT since, and
conflicts on that rather than on the change being replayed. Proved on
13/08/2026: `a1b2c3d4` came back "Operation resulted in a conflict" while
`git cherry-pick a1b2c3d4` applied cleanly by hand. A cherry-pick applies only
`diff(commit^, commit)`, which is what "replay this pull request" means.

Per pull request, in the order given:

1. resolve the target branch head;
2. `POST /_apis/git/repositories/{repo}/cherryPicks?api-version=7.1-preview.1`
   with `ontoRefName` = the target branch and a `generatedRefName` scratch
   branch — Azure DevOps lands the result on a branch of its own, it does not
   return a commit;
3. poll until it settles — it is **queued**, so the first response is usually not
   the answer;
4. read the scratch branch's head, move the target ref to it, and delete the
   scratch branch.

Step 4 matters: the cherry-pick alone does not touch the target branch.
The scratch branch is `release-tool/cherry-pick/{prId}-{shortTargetHead}` and is
deleted in a `finally`, including after a failure — Azure DevOps may have created
it before hitting the conflict.

GOTCHAS, all found only when live runs failed:

- The status enum is `GitAsyncOperationStatus`: `notSet`, `queued`,
  `inProgress`, **`completed`**, `failed`, `abandoned`. There is no
  `succeeded` — checking for one rejected every good operation.
- The operation is polled at `/cherryPicks/{cherryPickId}`. The id belongs in
  the path; hanging it off the collection URL as a query parameter is not a
  route.
- A conflict arrives as `failed` with the explanation in
  `detailedStatus.failureMessage` — there is no `conflict` status. That message
  is the only useful part of a failure, so it is always carried into the result
  along with the short id of the commit. Discarding it leaves the user with
  "did not succeed (failed)" and nowhere to go.

Every attempt is logged: the commit, both branches, the target head, and the
request and response verbatim on failure. That is what makes a failure
reproducible by hand.

GOTCHA: the run **stops at the first failure**. Every cherry-pick moves the
target, so anything after a conflict would be built on a head that is no longer
there. "Cherry-pick all" is therefore one at a time until the last one lands, not
a batched call. The UI reports per PR so it is obvious where to pick up by hand.
A commit equal to the target head is reported as "already up to date".

### Branch operations (`DevOpsService`)

Azure DevOps has no "create branch" call — a branch is a ref update:

```
POST /{org}/{project}/_apis/git/repositories/{repo}/refs?api-version=7.1
[{ "name": "refs/heads/<branch>", "oldObjectId": <old>, "newObjectId": <new> }]
```

Create is `oldObjectId` = 40 zeros and `newObjectId` = the source commit; delete
is the reverse. Both answer 200 with `value[0].success` — a refusal is **not** an
HTTP error, so `updateStatus` carries the reason
(`createBranchPermissionRequired` → the token lacks branch-create rights).

GOTCHA: `GET refs?filter=heads/{branch}` is a **prefix** match, so asking for
`release/prod` also returns `release/prod-old`. The exact `refs/heads/{name}` is
picked out of the results; without that, a branch would be cut from whatever
similarly-named branch happened to come back first. Covered by
`A_similarly_named_branch_is_not_mistaken_for_the_source`.

Failures are per repository and never abort the batch — one locked-down repo
must not leave a release half-created. An existing branch is reported, never
force-updated; a missing branch on delete is a success, not a failure.
Branch names are validated once at the endpoint (no spaces, no `..`, none of
`~ ^ : ? * [ \`), so a bad name is one message rather than N identical refusals.

## Credentials

Two ways in, in this order of precedence:

1. **Request headers** — `X-Atlassian-Email` / `X-Atlassian-Token`, and
   `X-DevOps-Token`. What the UI sends when someone types their own.
2. **Configuration** — the `Credentials` section, bound to
   `StoredCredentialsOptions`. Used only when the headers are absent.

Headers win on purpose: a sent token means the caller acts as themselves, so the
Confluence page history stays honest on an instance that has a credential of its
own. `AtlassianCredentialsFilter` and `DevOpsCredentialsFilter` both resolve in
that order and 401 when neither exists — the message names both routes.

Where the values live is a deployment choice, not a code one, because they are
bound from `IConfiguration`:

```
dotnet user-secrets set "Credentials:AtlassianEmail" "you@example.com" --project src/ReleaseTool.Api
dotnet user-secrets set "Credentials:AtlassianApiToken" "<token>"           --project src/ReleaseTool.Api
dotnet user-secrets set "Credentials:DevOpsPersonalAccessToken" "<pat>"     --project src/ReleaseTool.Api
```

GOTCHA: user-secrets are loaded **only when the environment is Development** —
that is where `CreateBuilder` adds the provider. `dotnet watch` / `dotnet run`
get it from `launchSettings.json`; a published IIS site does not, and will
silently see no credentials however correct the secrets file is. On a server use
the same keys as environment variables (`Credentials__AtlassianApiToken`, double
underscore) or a vault. The keys are listed **empty** in `appsettings.json` so
they are discoverable; filling them in there commits a live credential.

### What ships, and what must not

Proved by publishing to a scratch folder and grepping it, not assumed:

- `appsettings.json` **is copied into the publish output**. Credentials left in
  it are handed to everyone the folder is given to — which had already happened.
  A `GuardPublishedSecrets` target in the csproj now **fails the publish** if the
  `Credentials` section has any non-empty value.
- `appsettings.Local.json` is the per-machine credential file. It is gitignored,
  and the csproj sets `CopyToOutputDirectory`/`CopyToPublishDirectory` to `Never`
  — without that the Web SDK's `appsettings*.json` glob would ship it too.
- `App_Data/settings.json` **ships as it stands**, so the developer's branch
  names and repository list become every recipient's defaults. Convenient, and
  worth checking before a publish.
- `run.cmd` ships. The content root follows the **working directory**, so
  starting the `.exe` from elsewhere serves a blank page and writes settings to
  the wrong folder; the script cd's to its own directory first.
- `Urls` in `appsettings.json` pins the published app to port 5000 so it runs
  with no arguments. `launchSettings.json` still decides in development.

Rules that hold this together:

- **Nothing is typed in the browser any more.** The Azure DevOps tab and the
  Deployment tab have no PAT field at all — they read `config.devOps.configured`
  and show who the token belongs to, the way the Atlassian tab does. Only the
  Atlassian tab still offers a per-user override, because that one changes whose
  name lands in the Confluence page history.
  `GET /api/devops/me` supplies the name.
  GOTCHA: the profile API is **organisation-scoped and preview-only**. The
  un-scoped `app.vssps.visualstudio.com/_apis/profile/profiles/me` answers a PAT
  with **401**; it has to be
  `https://vssps.dev.azure.com/{org}/_apis/profile/profiles/me?api-version=7.1-preview.3`,
  with the org taken from Settings' default organisation.
- **No token is ever sent to the browser.** `GET /api/config` reports
  `configured: true/false` plus the Atlassian *email* — never a token. The email
  is deliberate: it is the name that lands in the page history, so the UI has to
  be able to show whose account is about to write. Covered by
  `The_config_endpoint_reports_existence_but_never_the_secrets`.
- `/api/config` sits **outside** the credentials-guarded group, since the UI
  reads it before it has any credentials. It is the only such endpoint.
- Startup logs a **warning** when Atlassian credentials are configured, naming
  the account. Server-held credentials plus no sign-in means anyone who reaches
  the URL writes as that person — hence Entra ID before the URL is shared.
- Still nothing in `localStorage` but branch names.
- `StoredCredentialsOptions` is **not** `ValidateOnStart`-validated: empty is a
  legitimate state, and the app must still boot for per-user entry.

## Why ADF and not HTML

The page body is ~124k characters of HTML (23 media nodes, 8 tables).
Rewriting it as HTML loses `data-local-id` (inline comment anchors),
`data-colwidth` and `data-background` unless every attribute is reproduced
exactly. Surgical ADF node replacement preserves all of it. **Formatting
preservation is a hard requirement.**

## UI

The page is titled **Release Tool**. There is no global strapline: each tab
carries its own one-line summary above its panels, since what the tool does
differs per tab.

Single page: token, space key, page title, Load → preview table
(ticket / current developer / proposed developer / source) → Apply.
Resolve and Apply must be separate steps; highlight rows where the developer was
**defaulted**, since those are the ones a human should check.

Built in `ClientApp/src` — `api.ts` (typed client, unwraps ProblemDetails into
the message shown to the user) and `App.tsx` (the page). Notes:

- The email is part of the credential, not just the token: auth is
  `Basic base64(email:apiToken)`, so the UI collects both.
- Typed credentials live in React state only. Nothing is written to local or
  session storage, where any script on the origin could read the token. When the
  server has its own (see Credentials), the UI sends **no** credential headers,
  auto-verifies on load, and shows which account it is acting as, with a "use a
  different account" toggle that reveals the fields again.
- `App.tsx` fetches `/api/config` once and passes it to both working tabs — it
  cannot change while the page is open, and both need it.
- Load → Resolve → Apply are three explicit steps. Apply confirms with the page
  title and version, and clears the preview afterwards so a second apply cannot
  run against a version that is now stale.
- Apply is disabled when any assignment has no `accountId`, since a mention
  without one would write a broken node.
- `BackToTop.tsx` is a **lower-right** floating button, mounted once in `App.tsx`
  so it serves every tab. It appears only past one viewport of scroll, carries a
  real `aria-label`, and honours `prefers-reduced-motion` by jumping instead of
  animating.
- **Theme** is `light` / `dark` / `system`, persisted in `localStorage` under
  `releasetool.theme` and applied as `data-theme` on the root element.
  Three things make it work:
  1. an inline script in `index.html` stamps the attribute **before the first
     paint**, or a saved dark theme shows a white page while the bundle loads;
     React seeds its state from `localStorage` with a lazy initialiser, so there
     is no effect and no correcting render;
  2. the dark palette is declared **twice** in `index.css` — once under
     `@media (prefers-color-scheme: dark)` guarded by
     `:root:not([data-theme="light"])`, and once under `:root[data-theme="dark"]`.
     Declaring it only inside the media query would leave the toggle unable to
     turn dark on under a light OS. `color-scheme` is set per theme in both, or
     scrollbars, selects and the date picker follow the browser while the page
     follows the choice;
  3. **`system` does not mean `prefers-color-scheme`.** Chrome and Edge have an
     appearance setting of their own, and when it is set to Light the page is
     told "light" however Windows is configured — which is exactly what made
     "follow system" look broken with Windows in dark mode. So `/api/config`
     reports `osTheme`, read from
     `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`
     → `AppsUseLightTheme` (0 = dark), and `system` prefers it, falling back to
     the media query when it is absent.
     GOTCHA: it is reported **only to loopback callers**. The host's setting is
     the user's setting only when they are the same machine; a hosted instance
     must not push its server's theme at everyone who opens it.
     `Microsoft.Win32.Registry` needs no PackageReference on `net10.0`.

## Test target

**Always work against a copy of a release page, never the live one.** Duplicate
the page into a personal space, point `Atlassian:DefaultSpaceKey` at that space,
and only move to the real one once a run has been checked end to end.

Verification: after a run, the Confluence page history diff should show
**only** the managed columns changed. That is the regression test that the ADF
round-trip preserved everything else — if anything else moved, the write is
rebuilding the document rather than editing it.

Work out the expected answer by hand first, from the tickets' own PR and
"fixed on" comments, and compare. The three sources
(`PullRequest` / `Reference` / `Defaulted`) should account for every row, and the
defaulted ones are the rows worth reading twice.

## Scaffolding steps not yet done

Nothing has been created yet. Build order:

1. `dotnet new sln`, `dotnet new web -o src/ReleaseTool.Api`, `npm create vite@latest ClientApp -- --template react-ts`
2. Add `Serilog.AspNetCore` only. Do NOT add `Microsoft.Extensions.Http` —
   it ships inside the ASP.NET Core shared framework, so `AddHttpClient` works
   with no PackageReference and adding one only risks a version conflict.
   Restore needs the repo-local `NuGet.config`: the machine-level
   `private` Azure DevOps feed 401s without credentials.
3. `dotnet user-secrets init` — done, `UserSecretsId` is in the csproj.
   Non-secret config lives in the `Atlassian` section of `appsettings.json`
   (`BaseUrl`, `DefaultSpaceKey`, `FallbackDeveloperName`), bound to
   `AtlassianOptions` with `ValidateDataAnnotations().ValidateOnStart()` so bad
   config fails at boot rather than on the first Atlassian call.
   `DefaultSpaceKey` deliberately points at the sandbox personal space.
   CREDENTIALS: the API token is never stored server-side and never in config.
   The client sends it per request as `X-Atlassian-Email` / `X-Atlassian-Token`
   (see `AtlassianCredentials`), chosen over server-side session so nothing
   holds a personal credential at rest and IIS needs no session state.
4. Endpoints: `POST /api/auth/verify`, `GET /api/confluence/page`,
   `GET /api/approvals/{pageId}`, `POST /api/approvals/{pageId}/resolve`,
   `POST /api/approvals/{pageId}/apply` — all mapped in `Endpoints/ApiEndpoints.cs`
   under an `/api` group guarded by `AtlassianCredentialsFilter`. Only
   `auth/verify` is implemented; the rest return 501 until Phase 5.
   The typed `AtlassianClient` has `BaseAddress = {BaseUrl}` (the site itself —
   NOT `api.atlassian.com/ex/...`, that is the OAuth gateway) and sets Basic
   auth per request. Call it with relative paths and no leading slash.
   GOTCHA: `UseSerilogRequestLogging()` must be registered BEFORE
   `UseExceptionHandler()`, otherwise every handled Atlassian failure is logged
   as a 500 while the client is correctly sent 401/404/409.
5. Vite dev proxy `/api` → `http://localhost:5000`; run `dotnet watch` +
   `npm run dev` — done. The API is pinned to port 5000 in `launchSettings.json`
   (it was 5015 from the template, which would have made the proxy 404 with no
   obvious cause). Both are also defined in `.claude/launch.json`.

   ```
   dotnet watch --project src/ReleaseTool.Api     # http://localhost:5000
   npm run dev --prefix ClientApp                 # http://localhost:5173
   ```

   Browse to 5173, not 5000 — in dev the SPA is served by Vite and `/api` is
   proxied. They only share an origin once Phase 8 builds the SPA into wwwroot.
6. MSBuild `BuildSpa` target → `wwwroot`; `UseDefaultFiles()`,
   `UseStaticFiles()`, `MapFallbackToFile("index.html")` — done.
   `BuildSpa` runs `AfterTargets="ComputeFilesToPublish"` and adds the Vite
   output as `ResolvedFileToPublish` under `wwwroot\`, so it lands in the
   publish folder without ever writing generated files into `src/`.
   It runs on **publish only** — `dotnet build` stays fast and does not need node.
   `RestoreSpa` runs `npm ci` first when `node_modules` is missing, so the build
   agent needs **node on PATH** (a Phase 9 prerequisite).
   GOTCHA: `MapFallbackToFile` would answer a mistyped `/api/...` with 200 and
   the SPA shell, so `app.Map("/api/{**path}", …)` returns 404 ahead of it.
   Covered by `An_unmatched_api_path_404s_instead_of_serving_the_spa`.
7. Validate `dotnet publish -c Release` produces a runnable folder before
   touching the deployment pipeline — done, verified end to end:
   `/` serves the SPA, `/health` 200, deep client routes fall back to
   `index.html`, assets serve, and the API answers on the same origin with no
   proxy. `web.config` is emitted for IIS.
