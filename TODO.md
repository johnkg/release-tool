# TODO

Pending work for the Release Tool.

## Requested

### 1. Move credentials into a config file

**Done** — the Atlassian email and token, the Azure DevOps PAT and the space key
are all read from configuration when the caller sends none. See the
**Credentials** section of `CLAUDE.md` for the full contract.

Decisions made while building it:

- **Where:** `dotnet user-secrets`, because the values bind from
  `IConfiguration`. That makes the storage location swappable without touching
  code — user-secrets on a dev machine, environment variables or a vault on the
  server. `appsettings.json` lists the keys but leaves them empty.
- **Whose token:** both. Configuration is a *fallback*; request headers still
  win, so anyone who supplies their own token acts as themselves and the page
  history stays honest. The UI shows which account is in use and offers "use a
  different account".
- The token never reaches the browser. `/api/config` answers with
  `configured: true/false` and the email only.

Still open, and it matters before anyone else gets the URL: with credentials on
the server and no sign-in, whoever reaches the URL writes to Confluence as that
account. Startup logs a warning saying so. See **Sign-in in front of it** below.

### 2. Default Developer Assigned in config

Already done, and worth knowing before any work starts:
`Atlassian:FallbackDeveloperName` in `src/ReleaseTool.Api/appsettings.json` ships
empty and has to be set to a real display name. It is resolved to an account ID
at runtime rather than hardcoded, so changing the name is all that is needed.

Remaining question is only whether it should be editable from the UI rather than
by changing a file.

### 3. Back-to-top button

**Done** — `ClientApp/src/BackToTop.tsx`, rendered once from `App.tsx` so it
covers all three tabs. Built to the **lower left**, not the lower right this
entry originally asked for: that was the call made when the work was picked up.

As built:

- `position: fixed`, 1.25rem from the left and 1.75rem up, clear of the panels'
  horizontal scrollbars.
- Rendered only once `window.scrollY` passes one viewport height; it returns
  `null` below that rather than floating over a short page.
- `aria-label="Back to top"` and a matching `title`; the arrow is an
  `aria-hidden` SVG.
- Smooth scroll, except when `prefers-reduced-motion: reduce` matches, where it
  jumps. The check is made at click time, so changing the OS setting takes
  effect without a reload.

### 4. Settings persistence, repository list and the Deployment tab

**Done.** Settings moved from `localStorage` to a server-side JSON file
(`App_Data/settings.json`), gained a **PROD** branch, a repository list and a
configurable branch name format; a new **Deployment** tab creates and deletes the
deployment branch across repositories. See `CLAUDE.md` for the contract.

Left deliberately open:

- **No dry run.** Check branches reports what is there, but Create goes straight
  to Azure DevOps once confirmed.
- **No audit trail.** Who created or deleted a branch is only in Azure DevOps'
  own history, not in this tool.
- The repository list is **not** reconciled against Azure DevOps — a repo that
  is renamed or archived shows up as "no such repository" at check time.

### 5. Changed files, candidate branch, merging, theme

**Done.** Changed-files view labelled by ticket, candidate branch create/delete,
per-PR and bulk merge onto the deployment or candidate branch, PAT fields removed
in favour of the connected account, back-to-top moved to the lower right, and a
persistent light/dark/system theme. See `CLAUDE.md`.

Worth knowing before the first real run:

- **Merging has never been run against a live repository.** The merge path is
  covered by tests against a stub, and the Azure DevOps *Merges* API it uses is
  **preview-only** (`7.1-preview.1`). Do the first run on a throwaway repo.
- A conflict **stops the run**. That is deliberate, but it means a partial
  release: the branch holds everything up to the failure. Merge the rest by hand
  and carry on from the next pull request.
- **Merge all** is unlocked only after *Fetch changed files* has been run on the
  Azure DevOps tab, and only where no file carries two tickets.

### 6. Merging replays too much history

**Fixed 13 August 2026 — replaced with cherry-pick.** Kept here because the
reasoning is the whole justification for the design.

A merge of `parents: [deploymentHead, prCommit]` is a merge of two *histories*,
not of one change. The deployment branch is cut from PROD; the pull request's
commit sits on UAT. Their merge base is wherever those two last diverged, so the
merge drags in everything that has landed on UAT since — and conflicts on that,
not on the change being replayed.

Confirmed on 13 August 2026: `a1b2c3d4` came back as
"Operation resulted in a conflict" from the merges API, and
`git cherry-pick a1b2c3d4` applied the same commit cleanly by hand.

The fix is to cherry-pick rather than merge:
`POST /_apis/git/repositories/{repo}/cherryPicks` with `ontoRefName` set to the
deployment branch and a `generatedRefName` for the temporary branch, then
fast-forward the target ref to the generated one and delete the temporary ref.
That applies only `diff(commit^, commit)`, which is what "replay this pull
request" means.

Choosing merge over cherry-pick was my recommendation and it was wrong for this
workflow — the conflict-reporting and file-overlap arguments were real, but they
do not outweigh replaying the wrong content.

Still unverified against a real repository: the cherry-pick path uses the same
preview API family and has only been exercised against stubs. Try it on a
throwaway repo first, and check that the temporary
`release-tool/cherry-pick/*` branch is cleaned up.

### 7. Compare the candidate / deployment branch against UAT

**Not started.** After the cherry-picks, show which files differ between the
branch just built and the UAT branch — the check that the release branch actually
carries what UAT carries, before it goes anywhere.

Notes for whoever picks this up:

- Azure DevOps has a diff endpoint for exactly this:
  `GET /_apis/git/repositories/{repo}/diffs/commits?baseVersion={uat}&targetVersion={branch}&api-version=7.1`
  with `baseVersionType=branch` / `targetVersionType=branch`. It answers with
  `changes[]` (`item.path`, `changeType`) plus `aheadCount` / `behindCount`,
  which are worth showing on their own.
- Compare against **UAT**, not PROD: UAT is what the release was tested on, and
  the deployment branch is cut from PROD. So expect two kinds of difference and
  they mean opposite things — a file in UAT but not in the branch is a **missing
  cherry-pick**, and a file in the branch but not in UAT is either a PROD-side
  change or something that should not be in the release.
- Which branch to compare is the same deployment/candidate choice the
  cherry-pick section already has; reuse that selector rather than adding another.
- The changed-files machinery already exists (`ChangedFile`, the overlap
  highlighting, the `.files` drawer). Cross-referencing this diff against the
  release's own changed-files list is where the value is: a file the release
  touched that is *still* different from UAT is the one to look at.
- Read-only, so it can sit next to *Check branches* rather than behind a
  confirmation.

### 8. Clear the resolution automatically on the way back

**Not started — requested 13 August 2026, and it is the better design.**

Today going live is one action (status → `YOUR_DEPLOYED_STATUS`, resolution →
`Done`) but coming back is two: *Set back to YOUR_READY_STATUS*, then
*Set resolution to Unresolved*. That asymmetry is the problem. The reverse of a
single act should be a single act, and as it stands a run can stop half way and
leave tickets sitting in `YOUR_READY_STATUS` still marked `Done` —
a state nothing in the tool flags and nobody would think to look for.

Proposed: transitioning to `ReadyForDeployment` clears the resolution as part of
the same operation, and the separate button goes away.

Notes for whoever picks this up:

- The mechanics are already there — `StatusTransitioner.MoveAsync` has the
  transition-screen branch and the `PUT /rest/api/3/issue/{key}` fallback, and
  `SetResolutionAsync(key, null, ...)` is the clear. What changes is which
  target triggers them: the `live` flag currently gates the resolution work to
  `DeployedToProduction` only.
- **A cleared resolution cannot go through a transition screen that requires
  one.** Where the back transition has a required Resolution field there is no
  null to send, so that case has to fall to the issue edit — or be reported.
  Worth checking which shape the PROJECT workflow actually has before building it;
  it decides how much of this is real.
- Keep the failure honest the way the forward path does: if the status moves and
  the resolution will not clear, say both and count the row as a failure.
- Decide whether to keep `POST /api/jira/resolution` afterwards. Dropping the
  button does not have to mean dropping the endpoint — it is the only way to fix
  a ticket whose resolution is wrong without moving its status, which is exactly
  the mess this entry exists to prevent. Recommendation: keep the endpoint, drop
  the button.
- The forward path already handles the mirror case (a ticket already in
  `YOUR_DEPLOYED_STATUS` but unresolved still gets resolved); the same
  reasoning applies coming back — a ticket already in
  `YOUR_READY_STATUS` but still `Done` should be cleared rather than
  reported as unchanged.

### 8. Before sharing a published build

- **`Atlassian:DefaultSpaceKey` ships empty**, so a fresh copy opens the page
  picker on nothing until it is set. Point it at a sandbox space while a build is
  being validated, and at the real space only once an Apply has been checked.
- **`App_Data/settings.json` ships as it stands.** Whatever branch names, branch
  name format and repository list were last saved become every recipient's
  defaults — check them before publishing, or delete the folder to ship blank.
- The publish **fails** if `appsettings.json` has anything in its `Credentials`
  section. That guard exists because a token was once shipped in it.

## Deferred by decision

### PR approval on defaulted rows

When the connected user is the **fallback developer**, rows that defaulted to
them (no PR comment, no "fixed on" reference) count as their rows and receive
the PR approver plus Approved and Merged. On a typical release that is a handful
of extra rows beyond the ones genuinely theirs.

Decided: **leave as is for now.** The option, if it is ever wanted, is to write
the approver only where the developer came from a PR or reference comment, and
skip `DeveloperSource.Defaulted`.

## Not yet done from the original build plan

- **Validate against a real page** (Phase 10). The tool has never run end to end
  with a real token. First run should go against the sandbox copy
  (`PAGE_ID`), never the live page (`1000000002`), and the Confluence page
  history diff should show only the managed columns changed.
- **Confirm the real column headers.** The tool matches on "Developer Assigned",
  "Requested By", "PR Approved By", "PR Approved Status" and "Merged to
  Deployment Branch". A column worded differently on the live page is silently
  skipped — the Clear panel shows it as *not on this page*, which is the quickest
  way to spot a mismatch.
- **Confirm the two Jira status names and the resolution.** The status panel
  matches `Atlassian:DeployedToProductionStatus` ("YOUR_DEPLOYED_STATUS"),
  `Atlassian:ReadyForDeploymentStatus` ("YOUR_READY_STATUS") and
  `Atlassian:ResolutionName` ("Done") against what the PROJECT workflow offers.
  None has been checked against the live workflow — if any is worded
  differently, the run comes back naming what Jira *does* offer, which is the
  quickest way to read off the real wording. Then fix them in config; no
  rebuild. Worth doing on **one** ticket first: unlike the Confluence write,
  there is no sandbox copy of a Jira ticket.
- **Point `DefaultSpaceKey` at `PROJECT`** once validation is done. It currently
  points at the personal sandbox space on purpose.
- **Deploy to IIS** (Phase 9): ASP.NET Core 10 Hosting Bundle, app pool set to
  *No Managed Code*, hostname and certificate, Azure Pipelines with an approval
  gate. The build agent needs **node on PATH**, since publish builds the SPA.
- **HTTPS only.** A typed token travels in a request header, so the hosted site
  needs HTTPS with a redirect and HSTS before the URL is shared. (A configured
  token never leaves the server, but the override path still sends one.)
- **Sign-in in front of it** (Entra ID) before anyone else gets the URL. This is
  now the *load-bearing* control: with credentials configured server-side, an
  unauthenticated visitor can write to Confluence as the configured account.
- **Health check** is already exposed at `/health` for monitoring.
