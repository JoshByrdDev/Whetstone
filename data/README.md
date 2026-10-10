# `data/` — dashboard-owned state (self-contained)

Everything the dashboard needs lives here, resolved from the repo root — the
dashboard no longer reads or writes anything inside the projects it monitors.
The **only** outward reference is each project's `projectPath`, used as the
working directory when the heartbeat runs Claude against that repo.

## Layout

There is **one** orchestrator for every project, not one per project. Anything to
do with running the fleet — the loop, the worker, the dispatcher prompt, the
lock/stop signals, the event log, the logs — lives at the top of `data/` and is
written to be true of a repo it has never seen. A project folder holds only what
is genuinely that project's own.

```
data/
  heartbeat.ps1             # THE orchestrator loop - one, for all projects
  worker.ps1                # one agent, one ticket, one process
  heartbeat-prompt.md       # dispatcher prompt, shared by every project (tokenised, no project names)
  worker-guard.ps1          # PreToolUse hook on every claude worker run - the local-only wall.
                            # Knows no project: one that needs rules of its own puts them in
                            # projects/<key>/guard.ps1, which the wall dot-sources last
  augment-hook.ps1          # PostToolUse hook on Augment retrieval - logs each call, asks for a rating
  heartbeat.lock / .stop    # GITIGNORED - runtime signals for the one loop
  events.jsonl              # GITIGNORED - runtime, events across every project
  logs/                     # GITIGNORED - runtime (heartbeat-*.log, augment-calls.jsonl)
  run/                      # GITIGNORED - per-run scratch (staged claims, per-ticket MCP configs)
  config.defaults.json      # committed - non-secret global settings
  config.json               # GITIGNORED - local global overrides (notification webhook/topic, secrets)
  skill-templates/          # committed, project-agnostic - what a NEW project is scaffolded from
  projects/                 # GITIGNORED by Whetstone - one nested git repo per project, see below
    <project-key>/
      .git/                   # this project's OWN repo, pushed to its own private remote
      .gitignore              # restates the exclusions below; Whetstone's rules do not reach in here
      project.defaults.json   # versioned in the project's repo — non-secret defaults (branch, worktree setup, QA ports, stack)
      project.json            # ignored by both — projectPath + secrets (tracker token, QA creds); overrides defaults
      project.example.json    # versioned in the project's repo — template for project.json
      reflect-prompt.md       # post-cycle reflection prompt for this project
      guard.ps1               # optional - commands only THIS repo makes dangerous, added to the local-only wall
      guard.tests.ps1         # optional - drives the real guard as this project's workers do
      pronounce.json          # optional - how the walkthrough narrator says this project's names and key prefix
      (knowledge lives in the target repo, not here - see projectPath/.claude/memory)
      skills/                 # this project's speculate/implement/qa-test/refine
      .disabled               # marker - unregisters this project without deleting it
      improvement-suggestions.md  # GITIGNORED — runtime (reflection output)
      reflection-keys.txt     # GITIGNORED — runtime (every fact key ever accepted, for dedup)
      notes-maintenance.stamp # GITIGNORED — runtime (rate-limits the consolidation pass)
```

## Why project folders are not in this repo

A project folder is one machine's business. Its skills name that repo's stack, its
`project.json` points at a path that exists on one computer, and its defaults
describe infrastructure nobody else runs. When they were committed here, every
clone of Whetstone received every machine's projects - enabled by default, since
`.disabled` is local - with no repo behind them.

So Whetstone ignores `data/projects/*/`, and each project folder carries its own
nested git repo pushed to its own private remote: the arrangement the notes store
already uses (`Initialize-NoteStoreGit` in `notes-store.ps1`). A nested repo inside
an ignored directory is invisible to the parent - no submodule, nothing in its
status, and `git -C` never walks up. The folder is versioned and backed up, and no
other machine ever sees it.

**Moving an existing project out** is `data/migrate-projects-to-nested-repos.ps1`,
and it must run on the machine that **owns** the project. Un-tracking is
`git rm --cached`: it leaves the files on that disk, but every other clone that
pulls the commit has the folder deleted from its working tree. The script backs
the folders up first, carries each one's history across with `git subtree split`,
checks every tracked file arrived in the nested repo, and only then un-tracks.
Run it with `-DryRun` first.

`data/skill-templates/` stays here and stays shared: it is what a new project is
scaffolded from, and knows nothing about any one repo. Note that a template
improvement does **not** reach existing projects - scaffolding never overwrites a
skill - so porting one into a project's `skills/` is a deliberate edit, made in
that project's repo.

## Config resolution

`DataRoot` is found by walking up from the app's content root / base dir to the
folder containing `Whetstone.csproj`, then `+ /data`. Override with the
`DASHBOARD_DATA_ROOT` environment variable.

Per project, config = `project.defaults.json` (versioned in the project's own
repo) merged with `project.json` (local, ignored everywhere). Both merges are **shallow and top-level** —
an override file that names `stack` replaces the whole block, it does not merge
into it.

There is **no active, current or default project.** Every enabled project is a
peer: the board shows them together, and the one heartbeat works them together.
Anything project-scoped therefore names its project — `?project=<key>` on the
API, `projectKey` when creating a ticket — and is refused when it does not,
rather than answered for whichever project sorts first. (`config.json` used to
carry an `activeProject`; it was read by four things, none of which needed it,
and it has been removed.) The one global setting that does select projects is
`heartbeatProjects`, and that is a filter, not a default: empty means all of
them, including any added later.

## Adding a project

Settings → Projects → Add does the mechanical part: it creates the folder, writes
both config files, and copies every required skill out of `skill-templates/` with
`{{PROJECT_NAME}}` / `{{PROJECT_KEY}}` filled in. Scaffolding is additive, so
re-running it later picks up a template added since without touching a skill you
have customised.

Then give the folder its own history - the form does not do this yet:

```
git -C data/projects/<key> init
git -C data/projects/<key> add -A
git -C data/projects/<key> commit -m "Baseline"
git -C data/projects/<key> remote add origin <private-repo-url>
git -C data/projects/<key> push -u origin HEAD
```

Copy the `.gitignore` from any existing project folder before the `add -A`, or the
first commit sweeps up `project.json` and its tracker token.

What the form does **not** cover, and what you therefore hand-edit into
`project.defaults.json` afterwards:

- `stack` — how the board's Start/Stop control brings this project's local stack
  up, takes it down, and decides it is running. Without it the project simply has
  no stack control.
- `phaseResources` — what a phase needs exclusive use of. Omitted, QA and
  review-pr both take `<project>:app-stack` - per skill, so a project that lists only
  `qa-test` still leases the stack to review-pr. Bare names are scoped to the project; a name containing a
  colon is taken literally, which is the **only** way two projects express that
  they share something machine-wide. Two projects whose stacks bind the same
  ports share a resource whether they say so or not — say so.
- `memoryDir` (defaults to `.claude/memory` in the target repo), `learningLabel`
  (defaults to "Learn {displayName}"), `ideUrl` (defaults to VS Code's scheme),
  `diffNoisePaths`.

Then customise `skills/` — the scaffolded copies are deliberately generic, and
everything specific to this repo belongs there or in its notes store, never in
the shared `heartbeat-prompt.md`.

## Enabled tracks

A project runs one or both **tracks**: `Delivery` (our own work, start to finish)
and `Review` (somebody else's branch, which we read, drive-test and stage PR
feedback on but never change). `tracks` in `project.defaults.json`, or the
checkboxes in Settings → Projects. Omitted means delivery only, which is what a
new project gets.

The setting is **presentation**. Nothing in the pipeline reads it — a ticket
carries its own track, fixed when it is created, and the claim, the phase machine
and the skills all go by that. What the setting decides is which lanes a
project's board draws and which tracks its New Ticket form offers. Filtered to
one project the board shows that project's tracks; across all projects it shows
the union, so one review project among five still draws the review lane.

Which is exactly why a track cannot be switched **off** while unfinished tickets
are still on it. They would keep their phases, keep being claimed and keep being
walked forward, with no lane on the board to show where they are — hidden would
quietly come to mean lost. Finish them or delete them first; Settings greys the
checkbox and says how many, and the server refuses the save either way. For the
same reason a project cannot have no track at all: it could never be given work
again.

## Issue trackers

Each project links its tickets to **one** external tracker, or to none. Jira and
Azure Boards are peers: one project can be on Jira while the project beside it is on
Azure Boards, and neither inherits anything from the other. It is read-only in
both cases — local tickets own the phase, the work items, the QA items and the
dependencies, and nothing here ever writes back.

Settings → Projects covers the whole block; `project.json` holds it directly:

```jsonc
"tracker": {
  "kind": "jira",                                // or "azureDevOps", or "none"
  "baseUrl": "https://your-org.atlassian.net",   // Azure: https://dev.azure.com/your-org
  "email": "you@example.com",                    // Jira only
  "token": "…",                                  // Jira API token, or an Azure PAT
  "project": "YourTeamProject"                   // Azure DevOps only
}
```

The old top-level `"jira": { siteUrl, email, apiToken }` block is still read and
still means `kind: "jira"`, so an existing project keeps working untouched. The
first save from Settings rewrites it as a `tracker` block and removes the old one
— two sets of credentials with only a precedence rule deciding which is live is
worse than either.

Two settings exist only because the trackers genuinely disagree, and both are
Azure DevOps' (Jira answers each itself):

- `doneStates` — which `System.State` values mean finished. Jira statuses carry a
  category; Azure DevOps' state is per-process free text. Defaults to
  `["Closed", "Done", "Removed"]`, which covers Agile, Scrum, Basic and CMMI.
- `blockingLinkTypes` — which link types hold a ticket back. Jira has a "Blocks"
  link that means impossibility. Azure DevOps has no such thing; its nearest
  relation is Predecessor/Successor, which is a *scheduling* hint many teams set
  for ordering they never meant to gate work on. Absent means the provider
  default; **present but empty means nothing blocks**, which is the honest answer
  for a team that uses Predecessor as a sequence rather than a gate.

## Where a learned fact goes

After each run, reflection mines the log for durable facts (`Invoke-Reflection`
in `worker.ps1`, backed by `notes-store.ps1`). A finding has three possible
destinations, and only the third needs a person:

1. **Folded into an existing note.** The finding is scored against note *bodies*
   by TF-IDF (`Find-RelatedNotes`); the top few candidates go to the agent with
   their prose, and it either rewrites one to include the fact or answers `NEW`
   (`Resolve-FactPlacement`). This is the common case for anything the store
   already half-knows, and it exists because key-matching alone could not see a
   fact recorded under a different name.
2. **Written as a new note** in the target repo's `.claude/memory`, when the
   agent says the fact is genuinely separate.
3. **Filed in `improvement-suggestions.md`** for review — only for `process`
   findings (changes to how the pipeline itself behaves) and for anything the
   model is inferring rather than reporting. `confidence` is about evidence, not
   about how long a fact will stay true.

The store keeps its own git history (the parent repo gitignores `.claude`), and
every mutation commits first, so an automated rewrite is always revertible.

`Invoke-NoteStoreMaintenance` runs off the back of reflection once the index
crosses ~120 entries, at most once a day. It merges only genuine near-duplicates
and otherwise files one item asking for a human read — the index is loaded on
every worker run, every reflection and every interactive session, so its size is
a real cost rather than untidiness.
