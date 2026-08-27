# Release Tool

Fills in the `IV. Approvals` table on a release page in Confluence, lines up
the pull requests behind the release, and cuts and populates the deployment
branches.

Built for one team's release process and published in case the approach is
useful. It assumes a particular shape of Confluence release page and a
Jira-ticket-to-pull-request convention — see
[Point it at your site](#point-it-at-your-site) for what has to match. Nothing
here is specific to any one organisation: every site, project and repository
name is configuration.

## What this tool is for

Filling the Approvals table by hand means opening every ticket in the release,
finding the pull request comment, noting who wrote it, checking the reporter, and
typing mentions into the table. Cutting a release branch by hand means repeating
the same ref update in every repository. This does both, and shows you its
working before it writes anything.

There are four tabs. **Atlassian** is where you start. **Azure DevOps** lines up
the pull requests behind the release and the files they changed. **Deployment**
cuts the branches and replays the pull requests onto them. **Settings** holds the
branch names, formats and repositories the other three work from.

### What it actually does

For each row of the Approvals table:

1. Reads the ticket key. It lives inside a Confluence *inline card* (a smart
   link), not as text — which is why that column looks empty if you copy the
   page as plain text.
2. Skips anything that is not `PROJECT-####`. **OTHER_PROJECT tickets are ignored entirely.**
3. Looks at the ticket's comments and picks the developer:

   | Rule | Condition | Developer |
   |---|---|---|
   | Primary | A comment contains an Azure DevOps pull request URL | That comment's author |
   | Secondary | A comment says "fixed on" / "included in" / "prs:" and references another ticket | That comment's author |
   | Fallback | Neither of the above | The configured `FallbackDeveloperName` |

4. Writes people into the table as real **@mentions**, not plain text, so they
   link the way a hand-typed mention would.

Rows that fell through to the fallback are **highlighted in the preview**, because
those are the ones worth a human glance.

#### Columns it fills

| Column | Filled with |
|---|---|
| Developer Assigned | The developer, by the rules above |
| Requested By | The Jira **reporter** of that ticket |
| PR Approved By | A person you choose — see below |
| PR Approved Status | "Approved", whenever an approver is recorded |
| Merged to Deployment Branch | "Merged", whenever an approver is recorded |

**Pick the approver by searching Jira.** Start typing a name or email and the
field offers matching Atlassian accounts; choose one and it is tagged as a real
mention. Typing a name without picking from the list does nothing — the account
ID is what makes the mention work, and only Jira can supply it. Deactivated
accounts and bot/app accounts are not offered, since mentioning either leaves a
dead link.

**It is scoped to your own rows.** The approver is written only to rows where
**you** are the resolved developer, so you can record that a colleague approved
your PRs without touching anyone else's rows. Recording an approver also sets
Approved and Merged on those same rows — the three go together.

The two status columns match the page's own formatting. Release pages list the
allowed values as coloured lozenges in the column heading — `REJECTED` /
`APPROVED`, `MERGED` — and the tool copies the matching one, so you get a
lozenge in the same colour and the same wording (`APPROVED`, not `Approved`).
If the column uses lozenges but not that word yet, it writes a green one. Only a
column with no lozenge anywhere gets plain text.

Columns are found by their heading text, not by position, so reordering the
table does not break anything. A column that is not on the page is skipped.

#### Clearing columns

There is a **Clear columns** panel with a checkbox per column, for resetting a
test page between runs. It empties the chosen columns on every in-scope row and
leaves everything else — including the ticket column and any OTHER_PROJECT rows — alone.

Everything else on the page is left exactly as it was. The edit is made against
the page's underlying document structure rather than by rewriting its HTML, so
inline comment anchors, column widths, cell shading and images all survive. After
a run, the Confluence page history diff should show *only* the columns listed
above changed — that is the check that it behaved.

### The Azure DevOps tab

It reuses what the Atlassian tab already found: pressing *Retrieve From Jira*
collects every Azure DevOps pull request link mentioned on the release's
tickets, so the Azure DevOps tab never re-reads Jira. The tab shows a count
badge once there is something to look at.

Press *Fetch pull requests and files* — the Azure DevOps token comes from
configuration, so there is nothing to paste. You get one section per repository,
and within each repository the pull requests are ordered by **when they merged**
— the order a deployment replays them. Anything still open has no merge date and
sorts last, marked *not merged*. Each section also carries a collapsed list of
the **files changed**, labelled with the tickets that touched them; a file
touched by two tickets is highlighted, and is what holds back merging that
repository in one go.

*Only pull requests I authored* is on by default, since most of a release is
someone else's work. It filters the list instantly and narrows which pull
requests the file lookup has to read.

Each pull request shows its **source commit**. Click the short id to expand it to
the full SHA and copy it at the same time.

A pull request that cannot be read — no access to that repo, or it has been
deleted — is reported on its own line with the reason, and the rest of the
release still comes through.

#### Filtering by environment

A release only cares about what merged into the branches it deploys from. Name
those branches on the **Settings** tab — one each for DEV, SIT and UAT — and the
Azure DevOps tab gains a dropdown to filter by them:

- **All configured** — every pull request targeting any of the three.
- **DEV / SIT / UAT** — only those targeting that one branch. An environment
  with no branch name set is greyed out.

It starts on **UAT**, since that is the environment a release is checked against
most often; with no UAT branch set it falls back to showing everything rather
than filtering the list down to nothing.

Anything merged elsewhere, such as a feature branch, drops out, and a repository
left with nothing disappears from the list. A count line says how many of the
total are showing.

`deploy-scripts` is the exception: its pull requests are always shown whatever they
target, since they follow their own branching.

Leave all three blank and the filter switches off, showing every pull request.

PROD is set on the same tab but is **not** a filter — it is the branch a
deployment branch is cut from.

#### Tickets with no pull request

Some tickets are fixed under another ticket and never get a PR of their own —
they just carry a comment saying so. Those are listed in their own table at the
bottom of the Azure DevOps tab, showing the ticket, the comment, and who wrote it.

### The Deployment tab

Cuts the release's branches and replays the pull requests onto them. It works on
its own: the repositories come from **Settings**, the source branch is Settings'
PROD, and the token is the configured one. The other two tabs only pre-tick the
boxes.

Two branches, each with its own Create and Delete:

- the **deployment** branch, cut from PROD, named from the date you pick;
- the **candidate** branch, cut from the deployment branch.

*Check branches* reports what already exists without changing anything. Failures
are per repository — one repo the token cannot write to does not strand the rest.
An existing branch is reported rather than force-updated.

Pull requests are then **cherry-picked** onto whichever branch you choose, either
one at a time or all of a repository in one go — and "all" means one after
another until the last one lands, not a single bulk operation. **Cherry-pick all
is only available where no file was touched by two tickets** — those are the ones
that conflict, and the table highlights the pull requests carrying them. A run
stops at the first failure, because every pick moves the branch and everything
after a conflict would be built on a head that has moved.

Cherry-pick rather than merge on purpose: a merge would bring in the whole
history between the deployment branch and the pull request's own branch, and
conflict on all of that instead of on the one change being replayed.

A pull request completed with **squash** replays as the single squashed commit,
not the commits it collapsed. The row says *squashed* and shows that id.

## How to use it

### What you need

| | |
|---|---|
| .NET SDK | **10.x** (pinned in `global.json`) |
| Node.js | **20+** (24 is what this was built against) |
| Atlassian API token | See below |
| Azure DevOps PAT | See below |

Both tokens are read from configuration, so they are set up once rather than
typed in every session. The tool acts as whoever they belong to: it can only
reach what that account can reach, and the Confluence page history shows that
name.

Check your toolchain:

```bash
dotnet --version && node -v
```

#### Point it at your site

This repository ships with placeholders, not with anyone's real setup. Before it
does anything useful, open `src/ReleaseTool.Api/appsettings.json` and set at
least these four — the [Configuration](#configuration) table explains each:

| Setting | Example |
|---|---|
| `Atlassian:BaseUrl` | `https://your-domain.atlassian.net/` |
| `Atlassian:DefaultSpaceKey` | the space holding your release pages — a **sandbox copy** to begin with |
| `Atlassian:TicketKeyPrefix` | your Jira project key, e.g. `PROJECT` |
| `Atlassian:FallbackDeveloperName` | the display name to assign when a ticket has no PR comment |

The Settings tab covers the rest — branch names, the branch name formats and the
repository list — and stores them server-side rather than in this file.

It also assumes your release page has a heading containing **"Approvals"**
followed by a table whose first column holds Jira links, and columns headed
*Developer Assigned*, *Requested By*, *PR Approved By*, *PR Approved Status* and
*Merged to Deployment Branch*. Columns are matched by heading text, and any that
are missing are simply skipped.

#### Atlassian API token

1. Go to <https://id.atlassian.com/manage-profile/security/api-tokens>.
2. **Create API token**, give it a label such as `Release Tool`, and set an
   expiry.
3. Copy it when it is shown — you cannot see it again afterwards.

Store it with the email address of the same Atlassian account. The email is part
of the credential, not just a label: the tool authenticates as `email:token`, so
a mismatched pair fails.

```bash
dotnet user-secrets set "Credentials:AtlassianEmail" "you@example.com" --project src/ReleaseTool.Api
```

```bash
dotnet user-secrets set "Credentials:AtlassianApiToken" "<token>" --project src/ReleaseTool.Api
```

The Atlassian tab still offers *use a different account*, which reveals the email
and token fields and acts as you instead. That is the one place a per-user
credential still matters, because it decides whose name lands in the page
history.

#### Azure DevOps personal access token

1. Sign in to <https://your-organization.visualstudio.com>.
2. Open **User settings** — the icon next to your avatar, top right — and choose
   **Personal access tokens**. The direct link is
   <https://your-organization.visualstudio.com/_usersSettings/tokens>.
3. **+ New Token**.
4. Fill it in:
   - **Name** — something you will recognise later, e.g. `Release Tool`.
   - **Organization** — `your-organization`. A token scoped to one
     organization will not read another.
   - **Expiration** — pick the shortest period you can live with. When it
     expires the tab simply reports that the token was rejected.
   - **Scopes** — choose **Custom defined**, then tick **Code → Read**. Add
     **Code → Write** as well if you intend to create branches or merge from the
     Deployment tab; reading pull requests needs only Read.
5. **Create**, then copy the token immediately. It is shown once and cannot be
   retrieved afterwards.

```bash
dotnet user-secrets set "Credentials:DevOpsPersonalAccessToken" "<pat>" --project src/ReleaseTool.Api
```

The Azure DevOps and Deployment tabs have no token field at all — they show which
account the configured token belongs to.

If a repository comes back as *"The token lacks Code (read) permission for this
project"*, the token is valid but scoped too narrowly, or your account has no
access to that repository. Every other repository in the release still loads.

### Running it

#### Day to day (two terminals)

```bash
dotnet watch --project src/ReleaseTool.Api
```

```bash
npm run dev --prefix ClientApp
```

Then open **<http://localhost:5173>**.

Not `:5000` — in development the page is served by Vite, which forwards `/api`
calls to the API on port 5000. Opening `:5000` directly gives you a 404 because
the API has no page to serve until it is published.

#### As a single app (what gets shared)

```bash
dotnet publish src/ReleaseTool.Api -c Release -o ./publish
```

Then run `publish\run.cmd` and open **<http://localhost:5000>**. Here the page
and the API share one origin, no Vite involved. Publishing runs the front-end
build automatically, so **node must be on PATH** wherever you publish.

Use `run.cmd` rather than the `.exe` directly: the working directory decides
where the app looks for `wwwroot`, `App_Data` and `logs`, so starting the `.exe`
from somewhere else serves a blank page. `run.cmd` changes to its own folder
first.

> User secrets are read **only** when the environment is Development. A published
> app does not load them, however correct the secrets file is. It reads
> `appsettings.Local.json` beside the executable, or environment variables such
> as `Credentials__AtlassianApiToken`.

See [Sharing it with the team](#sharing-it-with-the-team) for the full sequence.

#### Tests

```bash
dotnet test
```

140 tests, a few seconds, no network access. They host the real application and
only swap out the outbound HTTP calls, so the middleware, credential checks and
endpoints are all genuinely exercised.

### Using it

On the **Atlassian** tab:

1. **Connect** — the tab shows which account the configured credentials belong
   to and verifies itself on load. Press *use a different account* to act as
   someone else instead.

   Verification matters. Confluence answers an unauthenticated request with *404
   Not Found* rather than *401 Unauthorized*, so a bad token further on looks
   identical to a missing page.

2. **Choose the page** — the dropdown lists the space's pages newest first and
   preselects the most recently added one, which is usually the release page you
   want. The *Space key* field is prefilled from configuration; type another key
   and the list refills. Press *Load*.

   Pages must be **published**. Unpublished drafts are invisible to the API and
   to Confluence search, so a draft never appears in the list.

3. **Review** — press *Retrieve From Jira*. The table fills in with the proposed
   developer, where it came from (PR comment, referenced ticket, or defaulted),
   and the Jira reporter that will go into Requested By. A summary line counts
   each. Check the highlighted defaulted rows.

   Under **Write**, tick what you want written. For *PR approved by*, search for
   the approver and pick them from the list; the preview's *PR approved by*
   column then fills in on exactly the rows that will change, and a line tells
   you how many. Leave it blank to skip the approval columns entirely.

4. **Apply** — press *Apply to page* and confirm. The confirmation names what is
   being written and to which page. It then tells you the new page version.

Retrieve and Apply are deliberately separate. Nothing is written until you press
Apply, and after applying the preview clears so you cannot accidentally write a
second time against a version that has since moved on.

To reset a test page, use **Clear columns**: tick the columns to erase and press
*Clear selected columns*. It confirms first.

Then, for the branches: fetch the pull requests on the **Azure DevOps** tab, move
to **Deployment**, pick the deployment date, and use *Check branches* before
*Create*. Merge one pull request at a time until you trust it.

### Sharing it with the team

Each person runs their own copy on their own machine, with their own tokens. That
keeps the Confluence page history and the Azure DevOps branch history honest, and
it means nobody needs a server.

**Before you publish, check these four.**

1. `appsettings.json` → the whole `Credentials` section is **empty**. That file
   is copied into the publish folder, so a token here is a token you have handed
   out. The publish now fails outright if you forget, but check anyway; yours
   belong in `appsettings.Local.json`, which is never published.
2. `Atlassian:DefaultSpaceKey` is the space everyone's page picker opens on. It
   currently points at a **personal sandbox space**, which nobody else can see.
   Change it to `PROJECT` when you are ready for people to work against real release
   pages.
3. `App_Data\settings.json` **ships as it stands**, so whatever branch names,
   branch name format and repository list you last saved become everyone's
   starting point. Check the deployment branch format is the real one and not a
   test one. Delete the folder before publishing if you would rather people
   started blank.
4. Run `dotnet test`. Nothing in the publish step runs the tests for you.

**Publishing.**

```bash
dotnet publish src/ReleaseTool.Api -c Release -o ./publish
```

That folder needs the **.NET 10 runtime** on the machine that runs it. If your
colleagues do not have it and you would rather not ask them to install it,
publish a self-contained copy instead — about 106 MB rather than 8, but it needs
nothing preinstalled:

```bash
dotnet publish src/ReleaseTool.Api -c Release -r win-x64 --self-contained true -o ./publish
```

Zip that folder and hand it over however you normally share a build.

**What each person does once.**

1. Unzip it somewhere of their own — not a shared network folder, since the app
   writes settings and logs next to itself and two people would collide.
2. Create their own Atlassian API token and Azure DevOps PAT (see above). The
   PAT needs **Code (read)**, plus **Code (write)** to create branches or
   cherry-pick.
3. Create `appsettings.Local.json` next to `ReleaseTool.Api.exe`:

   ```json
   {
     "Credentials": {
       "AtlassianEmail": "you@example.com",
       "AtlassianApiToken": "<your token>",
       "DevOpsPersonalAccessToken": "<your PAT>"
     }
   }
   ```

4. Run `run.cmd` and open <http://localhost:5000>. The Atlassian tab should show
   *Using the Atlassian credentials configured on the server as you@…*.

That file is theirs and stays on their machine. Anyone who skips step 3 gets an
app that loads and then reports no credentials configured — it fails visibly
rather than acting as someone else.

> **If you ever host this centrally instead**, the credential story inverts:
> whoever reaches the URL acts as whatever account the server holds. Put Entra
> sign-in and HTTPS in front of it first.

## Reference

### Configuration

Non-secret settings live in `src/ReleaseTool.Api/appsettings.json`, and ship as
placeholders — **the tool does nothing useful until you set them**:

```json
"Atlassian": {
  "BaseUrl": "https://your-domain.atlassian.net/",
  "DefaultSpaceKey": "",
  "TicketKeyPrefix": "PROJECT",
  "FallbackDeveloperName": ""
}
```

| Setting | What it is |
|---|---|
| `BaseUrl` | Your Atlassian site. On the API-token route this is the site itself, not the `api.atlassian.com` gateway, so no cloud ID is involved. |
| `DefaultSpaceKey` | Pre-fills the space field. Point it at a **sandbox space** while you are validating, so a careless *Load* cannot reach a live release page. |
| `TicketKeyPrefix` | The Jira project this tool acts on, without the dash. Rows on the Approvals table for any other project are left alone, which is how a page that mixes projects is handled. |
| `FallbackDeveloperName` | The display name written into *Developer Assigned* when a ticket has no PR comment and no "fixed on" reference. Resolved to an account ID at runtime. |

The two release status names and the resolution name live in the same section
(`DeployedToProductionStatus`, `ReadyForDeploymentStatus`, `ResolutionName`).
They are configuration rather than constants because a Jira workflow can be
renamed without a rebuild, and they are matched against the transitions the
workflow actually offers — so they must read exactly as the workflow spells them.

Bad configuration fails at startup with the offending field named, rather than
surfacing later as a confusing Atlassian error.

Everything on the **Settings** tab — branch names, the branch name formats and
the repository list — is stored server-side as a JSON file under `App_Data/`, not
in your browser. It follows the installation, survives a restart, and can be read
and edited by hand.

### About your tokens

Both the Atlassian token and the Azure DevOps PAT are treated the same way:

- **Read from configuration**, never from a database and never written by the
  app. `dotnet user-secrets` keeps them out of the repository; on a server use
  environment variables or a gitignored `appsettings.Local.json`.
- **Never sent to the browser.** The page is told only *whether* a credential
  exists and which account it belongs to.
- **Never logged.** There are tests asserting exactly that.
- A token typed into the Atlassian tab travels in a request header, so a hosted
  instance **must be HTTPS only**.

> Credentials held by the server mean anyone who can reach the URL acts as that
> account. That is fine on your own machine; put sign-in in front of it before
> the URL is shared. The app logs a warning at startup saying so.

### When something goes wrong

| What you see | What it usually means |
|---|---|
| "Atlassian rejected the token" | Wrong email or token. The email must be the account's, and the token must not have been revoked. |
| "Not found… check the page is published" | A draft, a mistyped title, the wrong space — or an invalid token, since Confluence returns 404 for that too. |
| "No table found after an 'Approvals' heading" | The page has no heading containing "Approvals", or no table after it. |
| "The page moved from version X to Y" | Someone edited the page after you loaded it. Reload and review again before applying. |
| "Some rows have no Atlassian account" | A developer name could not be matched to an account, so a mention cannot be written. Apply stays disabled. |
| "Azure DevOps rejected the token" | The PAT is wrong, expired, or scoped to a different organization. |
| "The token lacks Code (read, write) permission" | The PAT is valid but too narrow, or you have no access to that repository. Other repos still load. |
| "Azure DevOps returned a non-JSON response" | Azure DevOps served a sign-in page instead of data — almost always a bad or expired PAT, which it answers with 200 rather than 401. |
| "Source branch … does not exist here" | The repository has no PROD branch by that name. Check the Settings tab. |
| "Could not cherry-pick abc12345: …" | Azure DevOps refused the replay and the text after the colon is its reason, usually a conflict. Apply that pull request by hand, then carry on from the next one. |
| "…no reason given" | Azure DevOps failed the cherry-pick without explaining. A conflict is the usual cause; open the branch in Azure DevOps to see. |
| `npm ci` errors on publish | Node is not on PATH. |
| NuGet 401 on restore | The machine-level `private` feed needs credentials. This project only uses public packages; the repo-local `NuGet.config` already scopes restore to nuget.org. |

Logs are written next to the app under `logs/`, one file per day, 14 days kept.
On IIS the app pool identity needs write permission to that folder.

### Current state

Pending work is tracked in [TODO.md](TODO.md).

The application is complete and self-contained: it builds, publishes to a single
runnable folder, and serves the page and API from one origin.

What has and has not been exercised for real:

- **Reading has been.** Loading a release page, retrieving from Jira, and
  fetching pull requests and changed files have all been run end to end against
  the live Atlassian site and Azure DevOps.
- **Writing has not.** *Apply to page* has never been run against a real
  Confluence page. Do that first against the sandbox copy, never the live one,
  and confirm the page history shows only the managed columns changed.
- **Branching and cherry-picking have not.** Create, Delete and Cherry-pick have
  never touched a real repository, and the Azure DevOps APIs they use are
  preview-only. Try them on a throwaway repository first.
- **Not hosted.** It is meant to be run locally, one copy per person, each with
  their own tokens. Hosting it centrally inverts the credential model — whoever
  reaches the URL acts as whatever account the server holds — so that needs
  sign-in and HTTPS in front of it first.

### Layout

```
src/ReleaseTool.Api/     ASP.NET Core minimal API
  Adf/                   Confluence document reading and editing
  Atlassian/             HTTP client, credentials, error translation
  Configuration/         Options, the settings file, the OS theme probe
  Confluence/            Space, page and write-back logic
  DevOps/                Pull requests, changed files, branches, merging
  Jira/                  Comment retrieval and developer derivation
  Endpoints/             The HTTP endpoints
ClientApp/               React + TypeScript front end (Vite):
                         Atlassian, Azure DevOps, Deployment, Settings
tests/                   Test suite
```
