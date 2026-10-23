# Whetstone — house conventions

A local ticketing board and heartbeat runner. .NET 9 API + SignalR, React/Vite client in `ClientApp`.

This file is what a new session — human or worker — reads before it writes anything. Keep it to the
things that are *not* discoverable from the code in front of you.

## Build and run

- A new machine, or a new dependency: `docs/setup.md` is the install list, and the System page's
  Setup check (`Services/SetupCheck.cs`) is the same list checked live. A change that adds something
  a fresh clone would not have (a CLI, an `npm install`, a config key) adds a row to both, rather
  than failing quietly where nobody looks.
- Build with `-c Release`. `bin\Debug` is held open by the running instance.
- Restarting the app kills in-flight heartbeat workers. Verify against an isolated QA copy, started
  from a worktree with `powershell -File data/qa-copy.ps1` (`-Stop` to stop it): this checkout's
  Release build on 5340, its Vite dev server on 5341, over a throwaway data folder with placeholder
  projects. It runs with `WHETSTONE_QA_ISOLATION=1` and refuses everything that would reach past
  that folder - heartbeat, steward, agent runs, tracker, notifications, the Claude login, docker,
  pushes - so a new route that does any of those belongs in `QaIsolation`'s table. The Vite dev
  server on 5173 proxies to the live API on 5338: reading through it is fine, changing anything is not.
- Never host the live instance (5338) as a Claude preview server. `dotnet run` holds the app, the
  heartbeat and every worker in one kill-on-close job, so when the app reaps that session's preview,
  the whole fleet dies silently mid-run. Relaunch it with `explorer.exe <repo>\Start-Whetstone.bat`,
  which no session owns.
- Editing `ClientApp/tailwind.config.js` needs a Vite restart; a reload silently serves the old CSS.
- `.ps1` files under `data/` must stay pure ASCII (PS 5.1 parses them as cp1252).
- Classroom walkthroughs are voiced by Kokoro under Node from `data/narrator/`. Run `npm install` there
  once per machine (its first run also downloads the ~90MB voice model); without it a walkthrough is
  still written but plays as captions only, and says so.

## Whetstone knows no project

This repository is the platform. It runs any project it is pointed at and names none of them: not
in code, not in a comment, not in a test fixture, not in this file. Everything that is true of only
one project lives in that project's folder, `data/projects/<key>/`, which is gitignored here and is
its own nested git repo with its own remote:

| What is specific | Where it goes |
|---|---|
| What a worker is told to do in each phase | `data/projects/<key>/skills/<phase>/skill.md` |
| Paths, ports, tracker, credentials, stack commands | `project.json` / `project.defaults.json` there |
| A command only that repo makes dangerous | `data/projects/<key>/guard.ps1`, tested by a `guard.tests.ps1` beside it |
| How the narrator says its product names and key prefix | `data/projects/<key>/pronounce.json` |

If a rule cannot be written without naming a repo, a script in it, or one of its hosts, it is not a
rule for this repository. When the shared code needs an example, it uses a placeholder: `PROJ-123`
for a tracker key, `alpha` and `bravo` for project keys in tests, `app-` for a stack's containers.
An incident that explains why something exists is told without the ticket's key - the lesson is
the platform's, the ticket is the project's.

The check before a commit:

```bash
git grep -niE '<each project key and display name on this machine>' -- . ':!wwwroot'
```

The one exception is a project that *is* this repository, pointed at its own fleet. Its display name
is this repository's name and matches every file, so the check greps it by its key alone. Everything
about it still lives in its folder - including the rules a worker on it needs most: never stop,
start, signal or write to the live board and the checkout it runs from, and prove a change on the QA
copy instead.

## The fleet never leaves this machine

Whetstone is local end to end, and every agent it runs is confined to that: a git worktree, the
docker stack, a database on `localhost`. Nothing in the pipeline may touch production, UAT, staging,
shared dev or CI — in either direction, reads included.

This is not a preference a ticket can override. It is the one rule that outranks a ticket's master
notes, and it is written in three places that have to stay in step:

| Layer | Where | What it does |
|---|---|---|
| The rule | `data/heartbeat-prompt.md`, first of the HARD RULES | Binds every provider, every phase |
| The routing | every `skill.md` — the shared "Local only" section, plus that phase's own part of it | Says what to build *instead* |
| The wall | `data/worker-guard.ps1`, a `PreToolUse` hook wired in `providers.ps1` | Denies the obvious commands outright |
| A project's own bricks | `data/projects/<key>/guard.ps1`, dot-sourced last by the wall | Denies what only that repo makes dangerous |

**The guard is a backstop, not the rule.** It is claude-only — codex has no hook system and gemini's
is not equivalent — and all three providers run with approvals switched off (`bypassPermissions`,
`--dangerously-bypass-approvals-and-sandbox`, `--approval-mode yolo`), so on two of the three the
prompt is the entire enforcement. Widening the guard does not close that gap; only the prompt reaches
there.

**A worker host is the one exception, and it is one address wide.** A second machine running this
board's workers (see "Worker hosts" below) reaches the board host's API and nothing else on it. The
prompt says so beside the rule. The guard allows that host and port exactly, named by
`WHETSTONE_BOARD_HOST`, which `worker.ps1` sets only on a worker host, and denies the same host on
any other port. The branch round trip is not a second exception: it is `worker.ps1`'s own git,
outside the agent's session, into the board host's repo, and never to origin.

Changing the guard? Run its tests. They encode the commands a fleet legitimately runs every day —
`docker compose down`, `sqlcmd -S localhost,1433`, a single-label docker host in a connection
string — and a false positive here stops every worker at once:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-guard.tests.ps1
```

**A project's rules ride on the shared ones and can only narrow them.** `providers.ps1` starts the
guard with `-Project <key>`, and `guard.ps1` in that project's folder is dot-sourced after every
shared rule has cleared the command - so it can refuse what would otherwise pass and can never
allow what the wall denies. Started without `-Project`, the guard applies every project's file: told
nothing about who it is working for, it takes the strictest reading. A file that will not load
denies the call. It is handed the browser's target (`$url`) as well as the command (`$c`), and - for
a project that sets `guardEdits: true` - the file an edit tool is about to write (`$path`); the edit
tools reach the guard only for those projects, because every call through it starts a PowerShell.
Changing one? Run the `guard.tests.ps1` beside it, which drives the real guard the way that
project's workers do.

**TFVC has one writer, and it is the human.** Where a project's source control is TFVC
(`repoType: tfvc`), the fleet works in the git mirror that shares its working tree, and the human checks in from Visual Studio. The rule
lives in `data/tfvc-guard.ps1`: `worker-guard.ps1` dot-sources it for workers, and a TFVC repo's own
`.claude/settings.json` can run it as a hook for interactive sessions — the one hook that reaches a
human's session, and it names the script by absolute path, so moving or renaming it disarms that
hook without a sound. It denies local verbs too (`tf add`, `undo`, `get`, `checkout`): the
server is already out of reach, and what they would rewrite is the human's pending changes. The
tests above cover both callers.

**A ticket asking for production work is planned, not blocked.** The action belongs to a human; the
pipeline's job is the investigation plus a locally-proven artifact — the exact statements, a rollback,
a verification query, committed to the branch, with a runbook comment telling the human what to run
and what they should see. That routing is why the rule does not simply deadlock such a ticket, and it
is the half most likely to be dropped by whoever next rewrites the rule shorter.

## "The base" is not always origin's

A ticket's branch is forked from, rebased onto and squashed against one ref, and which ref depends
on how the project delivers. By pull request: `origin/<base>`, because the local ref is only as
fresh as the human's last pull. With `mergeInsteadOfPr`: the **local** `<base>`, because that is
where the board merges every finished ticket and nothing pushes it - on a project where a push is
a production deploy, local `main` can sit dozens of commits ahead of origin.

The rule is written twice and the two must agree: `Resolve-BaseRef` in `data/worker.ps1` (the
fork, the hand-off rebase, the squash net) and `BaseRefAsync` in `Services/TicketGitService.cs`
(the diff a person reviews, the board's rebase). Workers are not left to work it out - the prompt
names it as `{{BASE_REF}}` and the skills say `<base-ref>`. A skill that spells out `origin/<base>`
or bare `<base>` in a git command has reintroduced the bug for one kind of project or the other.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-base.tests.ps1
```

## The look is a system, not a per-page decision

Nothing in the client picks its own colours, corners, faces or timings. Three files hold all of it,
and a new page's job is to *name* what it is rather than describe how it should look:

| What | Where | What a page does |
|---|---|---|
| Colour, tier, corner shape | `src/theme/` + `src/components/orbit.ts` | `tierToken(phase, kind)`, `chamfer(r)` — never a literal hex or radius |
| Tailwind class names | `ClientApp/tailwind.config.js` | `bg-gray-900`, `border-hair` — already pointed at the theme |
| Motion | the motion layer at the top of `src/index.css` | add `orbit-pick` / `orbit-tap` / `orbit-flank` / `orbit-swap` / `orbit-veil` / `orbit-pop` |

### Motion: name the behaviour, do not hand-roll the transition

Every interactive surface answers the pointer, and it answers the same way everywhere. That is a
class, not a habit:

- **`orbit-pick`** — one item in a list of things you can choose (a ticket row, a queue entry, a
  file in a diff). Nudges toward whatever it is about to open; does not grow. Mark the current one
  with `aria-current="true"` and it stays put — the same attribute a screen reader needs, so the
  two can never disagree. `pick-lift` for a list that runs across the page rather than down it.
- **`orbit-tap`** — a control rather than a choice: a toggle, a chip, a small button. Lifts.
- **`orbit-primary`** — the one filled amber action on a page.
- **`orbit-flank`** — on the *grid container* whose side column opens and shuts. The width lives in
  `grid-template-columns`, so that is the only property that can carry the animation; both
  templates need the same number of tracks or the browser cannot interpolate them.
- **`orbit-swap`** — content replacing other content in the same slot. Give it a React `key` that
  changes with the state, or React reconciles the two into one element and the animation never
  re-runs.
- **`orbit-veil`** — anything that opens over the page: a dialog, a form, a lightbox. Goes on the
  fixed container, with `data-fx` from `useVeil(onClose)`; every direct child that is not the
  dimming sheet rises, and that sheet must be marked `orbit-dim` when it is a child rather than the
  container's own background. An exit only exists if the overlay outlives its own dismissal, which
  is what the hook's timer buys — so an overlay that stays mounted while hidden has to become a
  component rendered only while open, or it re-opens already faded out.
- **`orbit-pop`** — an anchored panel: a menu, a suggestion list, a filter's bands. `data-fx` from
  `usePop(open)`, which keeps the panel mounted a little past its own close so none of the existing
  ways of closing it need intercepting. `data-side` names where it sits relative to its control
  (`above` / `left` / `right`; below is the default) so it arrives from the edge it hangs off — a
  panel that flips when there is no room below must set it from the same state that flipped it. A
  list whose open-ness *is* its contents also wants `useHeld`, or it spends its exit empty.
- **`orbit-spot`** — a part of a picture that a narration is pointing at: a stop on a diagram, a
  quoted line, a card, a range of code. `data-spot` says where it stands in the telling: `lit` (now),
  `seen` (already), `dim` (not yet), `off` (not appeared). A lit shape on a dark canvas glows; add
  `spot-flat` to a picture, paragraph or code line, where a glow would blur the thing pointed at.
  First used by the Classroom walkthrough; `UserFlow` takes a `spot` prop that applies it per stop.
- **`orbit-pan`** — a camera move on a picture: a screenshot zooming to the row that proves the point.
  Its own, slower speed (`--w-dur-pan`), because the eye follows it rather than the hand causing it.
- **`orbit-morph`** — a part of a narrated picture moving to where it now belongs: a box sliding to
  make room, re-colouring from unchanged to new. The page only sets where each part should be
  (`transform`, `width`, `height`, colours); `data-morph="enter"` / `"exit"` cover a part arriving and
  one kept mounted while it leaves. Its own speed, `--w-dur-morph`. The Classroom walkthrough's
  `picture` scene (`WalkthroughPicture.tsx`) is its first user: the deck says what is on screen at
  each beat, by id, and the move is worked out from the difference - so a new kind of explanation
  needs a new picture, never a new scene kind.
- **`orbit-row`** — the board's own row shell, which additionally carries its tier's skin.

Durations and travel are `--w-dur-1/2/3` (plus `--w-dur-pan` for `orbit-pan` and `--w-dur-morph` for `orbit-morph`) and `--w-nudge`; the curve for anything that *moves* is
`--w-ease`. Reduced motion is handled once, in the layer, for every class above.

**The check before you commit a new surface:** does every hovering, opening, or swapping thing on it
carry one of these classes? If you typed `transition-` or `duration-` into a `className`, you have
almost certainly re-implemented one of them slightly differently — that is how the app drifted into
five spellings of the same 150ms fade. `transition-colors` on a bare text or icon colour change is
the one fair exception.

### The phone view is the same components, arranged for a thumb

The client is also read on a phone, over a private network address that proxies to 5338. There is
no second UI: a page folds to one column below one width, `NARROW_PX` in `src/hooks/useNarrow.ts`,
which is Tailwind's `sm`. Say it in a class where a class can reach (`max-sm:`, `sm:`); ask
`useNarrow()` where the layout is an inline style, which is most of the board.

Two things a new surface gets wrong on a phone without ever looking wrong at a desk: anything that
only appears on hover (there is no hover - show it, or leave it to the drawer), and a `flex-1`
input or text run with no `min-w-0`, which refuses to shrink and scrolls the whole page sideways.
The check is the page at 390px wide with `document.documentElement.scrollWidth` equal to the
viewport's.

## A skill exists more than once. Change all of it.

`data/skill-templates/<phase>/skill.md` is the template; `data/projects/<project>/skills/<phase>/skill.md`
is what a worker on that project actually reads. The scaffolder only ever does `CopyIfMissing`, so a
project's copy is written once and never updated again — **editing the template changes nothing for
any project that already exists.**

How many projects have copies is a fact about this machine, not about this repository - take it
from the `ls` below. The one that gets missed is a project absent from `heartbeatProjects`: nothing
the fleet runs on a schedule ever reads its copies, and a rule left stale there stays invisible
until somebody points a run at it.

Each project folder is its own git repo, so a change that goes everywhere is one commit here and
one more in every project folder it touched.

So a change to a skill is a change to a *set* of files. Before you edit one, list them:

```bash
ls data/skill-templates/*/skill.md data/projects/*/skills/*/skill.md
```

Then decide, per project copy, which of three things this change is:

- **A pipeline rule** — anything about phases, hand-offs, MCP tools, what an outcome claims. It goes
  everywhere, verbatim. This is most changes.
- **A rule with a local example** — the same rule, worth grounding in what this project's notes store
  or history already knows. Port it, then add the local detail.
- **Genuinely project-specific** — a compose command, a credential, a CI quirk. Template gets the
  general shape; only that project's copy gets the specifics.

**Check for drift in the other direction too.** A fix written into one project's copy is a fix the
template never got, and the next project inherits the bug. It has happened the other way round as
well: a commit that expired a stale QA pass - written because a regression shipped under a green
checklist - landed in the template and in nothing else, so the project the incident came from kept
running the superseded heuristic for two days. To find that class of gap, read the difference:

```bash
diff data/skill-templates/qa-test/skill.md data/projects/<key>/skills/qa-test/skill.md
```

What is left should be only what is genuinely that project's: its compose commands, its ports, its
local examples. A pipeline rule on one side and not the other is drift, whichever side it is on.

**Other people's projects get the same change through the Skills page.** Whoever pulls a template
change has project copies this repository never sees, so each copy records the template text it was
last in step with - its baseline - in `data/projects/<key>/template-base/` (`SkillDriftService`).
When a template no longer matches a copy's baseline, the Skills tab shows a count, and the page shows
the template's own diff since then, the commits that made it, and a three-way merge into the copy
when it is clean. That is by text, not by date, on purpose: a pull stamps every template "now", and
a local edit would make a stale copy look current. It also makes a template commit's message
something the people porting it read - say what the change is for, and which of the three kinds
above it is.

## A project can have a steward, and its limits are code

A steward stands in for a ticket's owner between worker runs: it files the next tickets from a plan
the owner agreed, answers blocked tickets from decisions already made, reviews and merges finished
work, and asks the owner only what is the owner's. It is per project, off unless its owner switches
it on, and it is the one thing in the pipeline that moves a ticket to Done without a person.

That last sentence used to be false by design, and the comments that said "only a human signs a
ticket off" were the design. What replaced it is narrower than it sounds, and every part of the
narrowing is enforced in `StewardService`, not asked for in a prompt:

| The limit | Where |
|---|---|
| The mode - `off`, `propose` (says what it would do), `manage` (never merges), `full` | `stewardMode` in the project's machine-local `project.json`; set only from the Steward page. No tool changes it |
| Full means merging only where a merge is local | `ProjectConfig.StewardMode`: by pull request or TFVC it stops at `manage` however it is set |
| A change needs a run, and one run at a time | the `steward:<key>` lease, taken by `steward_begin` |
| Tickets only against a unit of the plan its owner agreed | the spec's status line, read from git **as committed** - nothing a steward can run commits one. The owner can, from the Steward page: a REST route with no tool behind it, that makes the same commit |
| A unit is released when its owner says so | `StewardUnit.ReleasedAt`, set by a REST route with no tool behind it, and refused while an outcome is not done or a note that is something to do is not ticked off |
| What a merge must prove, and may never carry | shared flags + the project's `steward-flags.ps1` + its `mergeChecks`, all inside `MergeAsync` |
| What it asks, it cannot answer | answering a question is a REST route with no tool behind it |
| A flag that is the owner's stops every merge until the owner clears it | `StewardClearance`, set by a REST route with no tool behind it, for one flag on one ticket's change as it stands - a rework lets it lapse. An answer to a question is prose and lifts nothing |
| What it notes for a release, it cannot clear | ticking a note off, or re-sorting it, is a REST route with no tool behind it |

**The words are taken.** "Orchestrator" is `heartbeat.ps1` and "owner" is the human whose ticket it
is - both everywhere in this code. The stand-in is the *steward*, and a new name for it in one file
is how the next reader ends up looking for two things.

**Its tools are a second door on the one MCP server.** A worker's session connects to `/mcp` and is
never shown a steward tool; a steward's connects to `/mcp/steward` and is shown nothing else. That
is separation by absence, the same kind "there is no tool that signs a ticket off" always was. It
is not authentication - nothing on this machine is - so do not describe it as a security boundary.

**It runs in a berth of its own.** The orchestrator asks `GET /api/steward/due` each pass and
starts `data/steward.ps1` in slot 99, beside the worker slots and never counted against them.
A run is started by something happening (a ticket reached review or blocked, the owner answered,
the base moved), not by a clock, and at most once per project in five minutes.

**A steward changes no file.** On claude that is a fact: the run is launched with no shell and no
editor (`--tools Read,Grep,Glob`) and does everything through its MCP tools. On codex and gemini it
is the prompt's rule alone - the same gap, for the same reason, as the local-only guard.

**What is one project's stays in that project's folder**, as with everything else here:

| What is specific | Where it goes |
|---|---|
| Limits, the branch synced into the base, never-merged paths, merge checks | `steward` in `project.defaults.json` |
| The plan being worked: its `key` and `name`, where its units live (`units`), the document they are cut from (`master`) | `steward.plan` there; the plans before it, readable and worked by nothing, in `steward.pastPlans` |
| What only that repo makes worth holding a merge for | `data/projects/<key>/steward-flags.ps1`, which like `guard.ps1` can only add a reason to wait |
| The order of work and the judgment behind it | `data/projects/<key>/skills/steward/skill.md` (template: `data/skill-templates/steward/skill.md`) |

**A project works one plan at a time, and a unit's number means nothing without its plan.** Every
plan numbers its units from one, so a ticket and an outcome carry the plan's key beside the unit
(`Ticket.PlanKey`, `StewardOutcome.Plan`), and anything that counts a unit's tickets filters on
both. A count by `PlanUnit` alone is right until the day a second plan starts and wrong from then
on. Starting one is the owner's (the Steward page, `data/plan-templates/` for the two documents'
shape) and is refused while the plan being worked has a ticket open.

The rules that outrank that skill are `data/steward-prompt.md`. Changing what a steward may do?
The tests are the specification of the limits:

```bash
dotnet test Whetstone.Tests -c Release --filter "FullyQualifiedName~Steward"
```

## Worker hosts

A board can borrow berths on a second machine on the LAN: a **worker host**, which runs workers and
hands their branches back. There is still one orchestrator, on the board host. The design is
`docs/worker-hosts-plan.md`, and setting a machine up is `docs/worker-host-setup.md`. What is not
discoverable from the code:

- **The runner pulls.** `data/runner.ps1` checks in with `/api/hosts/<name>/checkin` and gets its
  orders back in the response; nothing listens on a worker host. Like the live instance, it is never
  started from a Claude session, because whatever owns its tree can kill every worker under it.
- **A worker host follows the board host's commit by itself.** It gets work only on the exact same
  commit, and any commit here - docs included - takes it out of rotation until its runner, once
  idle, fast-forwards to it and restarts on the new code. Started normally, `runner.ps1` is a small
  supervisor around the runner (`-Inner`); a change to the supervisor's own lines needs the task
  started again on that machine.
- **A slot's tens digit names the machine.** The board host's workers are 1..`MaxWorkers` (10),
  and each worker host gets a block of ten after them in config order: the first from 11, the next
  from 21. The steward is 99, outside every block, so it never interrupts that numbering. Changing
  any of it renumbers berths under runs that are out, so it is done with the fleet stopped. This is
  written twice and the two must agree: `WorkerHostConfig.FirstRemoteSlot` / `HostBlock` /
  `StewardSlot` in C# and `$StewardSlot` in `heartbeat-common.ps1`.
- **Where work goes first is read every pass, never at start.** `dispatchOrder` (worker host names
  and `@board` for this machine) and `dispatchMode` (`fill` by default, or `spread`) reach the
  orchestrator through `GET /api/hosts/dispatch`, because applying a setting by restarting it would
  kill every worker it has out. The order is its own list, not the order of `workerHosts`: that
  order numbers the berths, and reordering it renumbers them under runs that are out.
- **A run is named by its ticket lease.** `ClaimResult.RunId` is the ticket lease's own id. The
  branch accept route refuses a run whose lease has gone, and a runner kills such a run, so a run
  the orchestrator gave up on can never overwrite the next one's work.
- **The worker host's clone calls the board host's repo `board`.** Not `hub`, which in `worker.ps1`
  means a project's main checkout. Its local base is a mirror of the board host's, so
  `Resolve-BaseRef` stays the same on both machines.
- **A worker host's worktree path never reaches the board.** The board reads a ticket's worktree
  path as one of its own (analysis, the steward's checks, the reaper), so a worker host reports the
  branch and nothing else.
- **The project kit is an allowlist**: skills, `guard.ps1`, a read-only copy of the notes. Never
  `project.json`. Its missing `reflect-prompt.md` is part of what keeps reflection off a worker host.
- **A worker host's resources are its own.** Its claim takes `alpha:app-stack@bravo-host`, never the
  board host's `alpha:app-stack`. More than one at once needs both the host's `qaLeases` and the
  project's `parallelStacks`, and what stack slot N means in ports is written in the project's
  folder, never here.
- **A ticket's files sit at the same path under each machine's data root** (`HostDataMirror`).
  Attachments go to the worker host when a run starts, and QA captures come back as they land. A
  route or tool that hands an agent a path under the data root has to keep to that, or a worker
  host's agent is handed a file it cannot open.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-board.tests.ps1
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/heartbeat-hosts.tests.ps1
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/runner.tests.ps1
```

```bash
dotnet test Whetstone.Tests -c Release --filter "FullyQualifiedName~WorkerHost|FullyQualifiedName~WorkerBranch|FullyQualifiedName~HostDataMirror"
```

## Knowledge

Whetstone's *managed* projects keep their notes in the repo at `.claude/memory`, junctioned from
the Claude Code auto-memory path so unattended workers and interactive sessions read the same
files. Whetstone itself is the exception: it has no `.claude/memory`, and its notes live only at
`~/.claude/projects/C--Projects-Whetstone/memory/`. Anything a *human* would also be wrong
without belongs there — or, if it is a convention rather than a fact, here, where it is read
without being asked for.

When the fleet works on this repository, that project's notes store is a third place: a
`memory/` folder in its own project folder, named by an absolute `memoryDir`. Not
`.claude/memory`, which would put a nested repo in the main checkout, and not the human's folder
above - reflection writes, folds and commits into whatever store it is given, and the human's
notes are theirs. A fact both need is copied into both by hand.

## Git

Commit at the end of every change. Never push.

**No commit ever carries an AI attribution trailer** - not this repo's, not the fleet's. Claude Code
adds a `Co-Authored-By:` line by default and instructs the model to write one in a message of its
own, which a ticket prompt cannot outrank; One ticket landed one past both a prompt rule and a
project note that forbade it. The switch that actually reaches that instruction is
`includeCoAuthoredBy: false`, and it is set in two places: the per-run settings file
`providers.ps1` writes for every claude worker, and the human's own `~/.claude/settings.json`.
Codex and gemini have no equivalent switch, so for them the wording in `data/heartbeat-prompt.md`
and in the implement and refine skills is the whole of the rule - keep all three in step.
