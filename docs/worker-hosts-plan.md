# Worker hosts: running workers on a second machine

Implementation plan, phase 1. The **board host** is the machine that runs the API, the database,
the UI and `heartbeat.ps1`. A **worker host** is another machine on the same LAN that only runs
workers and hands their branches back. There is still exactly one orchestrator, and it lives on the
board host.

Phase 1 sends a worker host only the phases that take no stack lease: `speculate`, `implement` and
`refine`. `qa-test` and `review-pr` keep running on the board host. QA on a worker host (per-host
stack leases, port offsets) is phase 2 and is not designed here.

## What it is for

The board host's fleet is limited by its RAM and by one Claude account: three workers and one QA
lease. A second machine with its own account adds slots that spend *its* account, so the extra
capacity is real rather than a share of the first account's limit.

## What stays exactly as it is

Claims, ticket leases, the steward, review, merging, the board's git operations, the UI, events and
the MCP tools. They all run on the board host, against its repos. The board host's copy of each
project repo stays the only copy that counts: everything a worker host does is fetched from it at
the start of a run and handed back to it at the end.

## Shape of the solution

```
board host                                         worker host
---------------------------------------------      ------------------------------------------
heartbeat.ps1 --orders--> API (/api/hosts/*) <---- runner.ps1   (check-in every few seconds:
     ^                     |                        |             "here is what I am running,
     |                     | claim-next,            |              any orders for me?")
     +---- host state -----+ events, MCP,           +--> worker.ps1 --> agent CLI (account 2)
                             branch accept   <-------------- events, MCP, branch hand-back
project repos  <---------- git fetch / push (file share) ------------- clones of the repos
```

**The runner pulls; nothing listens on the worker host.** The runner checks in with the API and
gets its orders back in the response, so the only connection is worker host to board host, which
workers need anyway for events and MCP. That means no listener, firewall rule or URL ACL on the
worker host, and the API stays the one place that knows the fleet's state. Orders take a few
seconds to arrive, which is nothing next to a run that takes minutes.

## The pieces

### 1. Branch round trip (`data/worker-board.ps1`, dot-sourced by `worker.ps1`) - built

Built and tested first, against temporary repos and with no fleet involved:
`data/worker-board.tests.ps1`.

**The remote is called `board`, not `hub`.** In `worker.ps1` "the hub" already means a project's
main checkout, the one its worktrees hang off, and on a worker host that is the clone itself.

**The worker host's clone mirrors the board host's refs.** Each project on the worker host is a
clone (`git clone -o board`) whose remote `board` is the board host's repo (over a file share, see
7). `Sync-BoardRefs` fetches, at the start of a run and again before the hand-off rebase:

```
+refs/heads/<base>:refs/heads/<base>          # the board's LOCAL base (mergeInsteadOfPr)
+refs/remotes/origin/*:refs/remotes/origin/*  # the board's view of origin
```

and `Get-BoardBranch` fetches `+refs/heads/<branch>:refs/remotes/board/<branch>`. It asks with
`ls-remote` first, so a branch the board host doesn't have is told apart from a board host it
can't reach.

The clone's main checkout is detached off `<base>` (it does that itself, which rewrites no file),
so `<base>` can be force-updated. It is a mirror and nothing commits to it.

Because the worker host sees exactly the refs the board host sees, `Resolve-BaseRef` and
`worker-base.tests.ps1` stay as they are. That rule is written in two places
(see "The base is not always origin's" in `CLAUDE.md`), and this keeps a third from appearing.
The tests check that `Resolve-BaseRef` gives the same answer on both sides, both ways round.

**Bringing the ticket branch up to date** (`Update-BranchFromBoard`, before `Resolve-Worktree`):

| Worker host's branch vs `board/<branch>` | Action |
|---|---|
| no board branch | nothing (`new`, or `local-only` for a run here that never handed back) |
| no local branch | create it at the board host's commit |
| equal | nothing |
| behind (the board host ran it since) | fast-forward |
| ahead (a refused hand-back left commits here) | leave it; the next hand-back carries them |
| diverged, local tip is the **agreed** commit | hard reset to the board host's commit (the board rebased what it already had) |
| diverged, anything else | refuse: those commits exist nowhere else |

The **agreed** commit is `refs/whetstone/agreed/<branch>` in the clone. It is set whenever the
branch is brought up to the board host's commit, and when the board host accepts a hand-back
(`Set-BranchHandedBack`). It replaced this plan's first idea, which was "every run hands back, so
divergence is always safe to reset." That isn't true after a refused hand-back, and a reset there
would delete work nobody else has. A worktree holding the branch moves with it, and refuses if it
has anything uncommitted.

After that, `Resolve-Worktree` sees a local branch and runs unchanged. Worktrees on the worker host
are kept between runs, which keeps `node_modules` and friends warm. The board host's
`WorktreeReaper` never sees them. The runner reaps a worker host worktree once its ticket is Done
or its branch is gone from the board host.

**Handing back.** At the end of the run, after `Save-WorkerLeftovers`, the hand-off rebase and the
squash net (all of which run against the mirrored base and are unchanged):

1. `Send-BranchToBoard` pushes HEAD to `refs/whetstone/incoming/<runId>` with `--no-verify`.
   A project's pre-push hook is written for origin, and this is transport between two copies of one
   repo. It's a ref nothing ever checks out, so the push can't hit git's refusal to update a branch
   that is checked out in one of the board host's worktrees. A ticket the board host has run before
   is normally in exactly that state, and the tests prove the refusal.
2. `POST /api/tickets/{id}/branch/accept` with `{ runId, branch, sha, expectedOld }`.
   `expectedOld` is the board sha fetched at the start of the run. The API does the update on the
   board host (piece 2), where `TicketGitService` already knows which worktree holds the branch.
3. On a yes, `Set-BranchHandedBack` records the commit as agreed.

The worker does the push, not the agent. It happens outside the agent session, so the guard never
sees it, and nothing here pushes to `origin`. That is the rule's own target, and it is unchanged.

The accept route's own cases (lease lost, branch moved meanwhile, dirty board host worktree) are
the API's, and are tested with it in piece 2.

### 2. API (`Services/WorkerHostService.cs`, `Api/WorkerHostEndpoints.cs`)

**Host registry.** In memory, like `EventStore`. Each host has a name, its configured slots, its
last check-in, the Whetstone commit it runs, its live runs, its pending orders and its usage-limit
pause. Nothing is persisted. After the API restarts, the runner's next check-in reports every live
run again, the same way agents announce themselves again today. An order lost in a restart shows
up as a start that never ran, and the orchestrator handles that as below.

| Route | Called by | Does |
|---|---|---|
| `POST /api/hosts/{name}/checkin` | runner | Body: its commit, its live runs (slot, ticket, runId, pid, exited/exit code), its usage limit if any. Response: pending orders. Long-polls about 20s when there are none. |
| `POST /api/hosts/{name}/orders` | orchestrator | `start` (slot, runId, the claim) or `kill` (runId). |
| `GET /api/hosts` | orchestrator, UI | Each host: online/offline, commit match, slots, runs, pause. |
| `POST /api/tickets/{id}/branch/accept` | worker on a worker host | Below. |
| `GET /api/hosts/project-kit/{key}` | runner | Below. |

A host is **offline** once its last check-in is more than 2 minutes old. A host whose commit
differs from the board host's is **mismatched** (it brings itself back in step once idle - see
"Keeping in step" at the end): it is shown, and given no orders, because the
worker host runs its own copies of `worker.ps1`, the prompt and the guard.

**Claims.** `ClaimNextRequest` gains `Skills` (the allowed skills; a ticket whose next skill is not
on the list is skipped like a busy one, and never blocked for a missing skill it was not going to
run) and `Host` (narrows the claim to that host's configured skills and projects). The orchestrator
sends `Host`. A board host claim sends neither and behaves exactly as it did before.

*As built:* the host is **not** written into the lease's holder label. A non-null `HolderLabel` is
what marks a lease as a *person's*, and `ReleaseAllAsync` spares those, so a host name there would
make a worker host run's lease survive every orchestrator restart. Where a ticket is running is read
from the host registry's runs instead. Every claim now carries `runId`: the id of the ticket's own
lease (`ClaimResult.RunId`). That gives "this run" a name without a new table or a migration.

**Branch accept.** It refuses unless the ticket's lease is still held for that `runId`. So a run
the orchestrator gave up on (host offline, lease released, ticket picked up elsewhere) can never
overwrite what the next run did. If `refs/heads/<branch>` is not at `expectedOld`, it is refused
too. If the lease is good, it updates the branch:

- no board host worktree holds the branch: `git update-ref refs/heads/<branch> <sha> <expectedOld>`
- a clean board host worktree holds it: `git -C <wt> reset --hard <sha>`
- a dirty board host worktree holds it: refused (a person has edits there)

After that it deletes the incoming ref. The branch name is recorded on the ticket by the worker at
the start of the run, as on the board host, but **without its worktree path**: the board reads a
ticket's worktree path as one of its own (analysis, the steward's checks, the reaper). A refusal
comes back to the worker, which blocks the ticket with the reason. It does not retry. A branch
checked out in the board host's *main* checkout, which is a person's, is refused as well.

**Project kit.** Served from the board host on every run, so a skill edited on the board host
takes effect on the worker host immediately. *As built* it is an allowlist: `skills/`, `guard.ps1`,
and the notes store's `*.md` under `_notes/`. That is all `worker.ps1` reads from the project
folder. Everything else it needs arrives in the claim, which the runner rewrites to the worker
host's paths. The conventions file is read from the worktree, not the project folder. The kit
never includes `project.json` or `project.defaults.json`, which hold credentials, or
`reflect-prompt.md`.

**QaIsolation.** Check-in, orders, accept and the project kit are in `QaIsolation`'s table, and the
QA copy refuses them. `GET /api/hosts` stays open: a copy has no hosts, and the fleet view asks for
it.

Tests (`Whetstone.Tests`, filter `WorkerHost`): offline and mismatch rules, orders queued and
delivered once, claim honouring `Skills`, accept refusing on a lost lease, a moved branch and a
dirty worktree, and the isolation refusals.

### 3. The runner (`data/runner.ps1`, ASCII)

A loop on the worker host, started at logon by a Task Scheduler entry. It is never started by a
Claude session: like the live instance, whatever owns the runner's process tree can kill every
worker under it.

Each pass:

1. Check in with its state and receive its orders.
2. **`start`:**
   1. Sync the project kit into its own `data/projects/<key>/`.
   2. Rewrite the claim's paths from `host.json`.
   3. Write `run/claim-<slot>.json`.
   4. Launch `worker.ps1 -ClaimFile ... -Agent <slot> -TicketId <id> -RunId <runId>`, the same way
      `Start-Agent` does today, and keep the process handle.
3. **`kill`:** a tree kill of that run.
4. A run it is holding that the API no longer recognises (its lease was released while the host was
   offline) is killed, and it does not hand back.

At startup it adopts surviving `worker.ps1` processes by command line, the same way
`Find-OrphanAgents` does on the board host. It reports them on its first check-in.

`data/host.json` on the worker host is machine-local and gitignored:

```json
{
  "name": "bravo-host",
  "projects": { "alpha": { "path": "D:\\src\\alpha" } }
}
```

*As built:* no `dashboardUrl` here (it is `config.json`'s, read by every script alike), and no
remote path: the clone's own `board` remote names the board host's repo. `worktreeRoot` per project
is optional.

The worker host's own `data/config.json` sets `dashboardUrl` (which `heartbeat-common.ps1` already
reads, so workers and their MCP config point at the board host with no further change) and
`claudeAccount` for the worker host's login.

### 4. Worker (`data/worker.ps1`)

When the claim carries a host (`$Claim.host`), and only then:

- before `Resolve-Worktree`: the fetch and branch update from piece 1
- after the hand-off steps: the hand-back from piece 1, and on a refusal, block with the reason
- skip `Invoke-Reflection`. It writes to the project folder and the notes store, which are the
  board host's. Running reflection on the board host from a run log shipped back is a follow-up
  (open question 4).
- the usage-limit file is written to the worker host's own `run/`, the runner reports it, and the
  board host's file is left alone

A claim with no host takes none of these paths, so a board host run is byte-for-byte today's.

### 5. Orchestrator (`data/heartbeat.ps1`)

`workerHosts` in the board host's `config.json`:

```json
"workerHosts": [
  { "name": "bravo-host", "enabled": true, "slots": 4,
    "skills": ["speculate", "implement", "refine"], "projects": ["alpha"] }
]
```

**Slots.** Board host slots are 1..`MaxHeartbeatWorkers` (3 when this was written, 10 since) and
the steward is the next one. Worker host slots are numbered past it (from 12 now), in blocks per
host, and each block is clamped on its own (1..8). `$Agents[slot]` gains `Host` and `RunId`. Its `Process` is `$null` for a
remote slot.

| Function | Change |
|---|---|
| fill loop | Per host, fill while it has a free slot, is online, matches the board host's commit, and is not paused. A worker host claims with its `Skills`, `Projects` and `Host`. Which place fills first, and whether it is filled before the next or work is spread across them, is the owner's setting (open question 2); unwritten, worker hosts fill **before** the board host. A place with nothing it can take is done for the pass, and only it. |
| `Start-Agent` | A remote slot posts a `start` order instead of calling `Start-Process`. If the order is not seen as running within 2 minutes, the lease is released, as for a local start that failed. |
| `Update-Fleet` | A remote slot reads its exit from `GET /api/hosts`, not from `HasExited`. |
| startup | Remote live runs from `GET /api/hosts` are adopted and added to `KeepTicketIds` **before** `Reset-Leases`. Without this, restarting the orchestrator releases every worker host ticket that is mid-run. That is the one bug in this design that would double-work tickets. |
| `Resolve-OrphanedClaims` | Remote runs count as "an agent here is working it". |
| host offline more than 10 min | Release its runs' ticket leases. The tickets resume anywhere, and the existing attempt counting covers runs that made no progress. The runner kills those runs when it comes back (piece 3). |
| `Wait-ForFleet` | Counts remote runs, so a graceful stop drains both machines. |
| usage limit | Per host. A worker host's limit pauses that host's slots. The board host's file still pauses the board host. |

The dashboard's hard Stop (`HeartbeatManager.Stop`) also posts `kill` orders for every remote run.
"Now" means both machines (open question 3).

Tests: an extension to the existing heartbeat script tests, or a new `heartbeat-hosts.tests.ps1`,
that stubs the API and drives the fill loop, adoption-before-reset, offline release and stop.

### 6. The local-only rule

Workers on the worker host reach one thing off their own machine: the board host's API. The rule
and the wall have to say that exactly and no more.

- `data/heartbeat-prompt.md`, HARD RULE 1: "this machine" becomes "this machine, and the board's own
  API on its host. Nothing else on that host: not its docker, not its databases, not its files."
- `data/worker-guard.ps1`: the worker passes the dashboard's host in the environment
  (`WHETSTONE_BOARD_HOST`, set only on a worker host). The guard allows that host and port
  **exactly**, and everything else stays as it is. Tests: the dashboard URL is allowed, the same
  host on another port is denied, and another LAN address is denied. On the board host an agent can
  already reach the board at `localhost`, so this keeps a worker host level with that rather than
  giving it more.
- `CLAUDE.md` "The fleet never leaves this machine": the same sentence, plus one row for the runner.
  These three have to stay in step, as that section says.

### 7. Network and setup

- **API binding.** *As built:* the app itself reads `lanUrl` from the global config at startup
  (`LanBinding`) and adds that one address to the ones it was given. A batch file cannot read JSON
  without a script of its own. It must be an explicit `http://<ip>:<port>`; a wildcard is refused,
  and a QA copy never adds it. Off by default, so a machine with no worker host is unchanged.
- **Firewall.** One inbound rule on the board host allowing TCP 5338 from the worker host's IP only.
  The API has no authentication, so this rule is the boundary. Nothing else is, and the host name in
  the routes identifies a host. It is not authentication, and should not be described as such.
- **Fixed addresses.** A DHCP reservation for both machines, because the rule and `dashboardUrl`
  name them.
- **The repos.** A file share on the board host covering the project repos, with read and write
  access for one local account that the worker host's runner user stores with `cmdkey`.
  The alternative is git over SSH (the Windows OpenSSH server). It is more setup, and it does not
  give a whole share to a whole account.
- **Worker host checkout.** A Whetstone clone (from the board host's share, so the commits match), a
  clone per project made with `git clone -o board` and its main checkout detached, the gitignored files the
  project's `worktreeCopyFiles` names copied in once, the agent CLI logged in to account 2,
  `host.json`, the Task Scheduler entry for the runner.

`docs/worker-host-setup.md` is written as part of this piece, as a checklist with a check after
each step.

### 8. UI

Minimal in phase 1. *As built:*

- the dock in the board's header draws, after the board host's own berths, each switched-on worker
  host's name and its berths. A host that is not taking work (off, offline, on another commit, at
  its usage limit) rests its berths stopped and says why. Polled from `/api/hosts` every 15 seconds.
- a ticket a worker host is running shows its agent like any other, by slot number. Naming the host
  on the ticket row is left for phase 2.
- `workerHosts` is edited in `config.json`. A Settings page for it is phase 2.

## Order of work

Each step is its own commit, and steps 1 to 4 change nothing for a board host on its own:

1. Branch round trip + its tests (piece 1) - done
2. API: registry, check-in, orders, claim filter, accept, project kit, QaIsolation + tests (piece 2) - done
3. Worker host mode in `worker.ps1` (piece 4) - done
4. Runner + `host.json` (piece 3) - done
5. Orchestrator host awareness + tests (piece 5) - done
6. Prompt, guard and `CLAUDE.md` wording + guard tests (piece 6) - done
7. LAN binding, `worker-host-setup.md` (piece 7) - done
8. Fleet view (piece 8) - done
9. First live run: one worker host slot, one project, `implement` only, on a low-stakes ticket.
   Then one ticket deliberately moved board host to worker host to board host, to watch the round
   trip in both directions.

Step 9 is the only step that cannot be proven on the QA copy, which refuses agent runs by design.
Everything before it is proven by its tests and by the QA copy refusing the new routes.

## Open questions (the owner's)

Phase 1 was built on the planned answer to each of these, as the owner asked. Each can still be
changed.

1. **The project folder on the worker host.** The decision of 2026-09-03 was that no two of the
   owner's computers see each other's projects. A worker host has to read a project's skills, guard
   and notes to work on it. This plan serves them from the board host on every run (the project
   kit) rather than cloning the project folder's own repo. That way nothing drifts, and nothing is
   committed on the worker host, but the files are still on that disk. Is a worker host an
   exception to that decision, and for which projects?
2. **Which host fills first.** Answered: the owner's, on the Heartbeat page. `dispatchOrder` ranks
   every worker host and this machine (`@board`), and `dispatchMode` is **Fill in order** (`fill`,
   the default: each place filled before the next is given anything) or **Spread** (each ticket to
   the place running the fewest, the order breaking ties). Unwritten, it is fill with worker hosts
   first - the planned answer, unchanged. The orchestrator reads both from `GET /api/hosts/dispatch`
   every pass; see `Start-Fill` in `data/heartbeat-hosts.ps1`.
3. **Hard Stop.** Planned: it kills worker host runs too.
4. **Reflection.** Skipped on worker host runs in phase 1, so their lessons are lost. The follow-up
   is shipping the run log back and reflecting on the board host.
5. **File share or SSH** for the repos.

---

# Phase 2: QA on a worker host - built

Phase 1 kept `qa-test` and `review-pr` on the board host, because QA stands up a docker stack and
the stack lease was one per project: a worker host's QA would have waited on the board host's
stack, or started a second copy on the same ports. Phase 2 makes each machine's stack its own, and
moves the files a QA run reads and writes by path between the two machines.

## A worker host's own stack

A claim for a worker host takes that host's copy of every resource its phase needs:
`alpha:app-stack@bravo-host` (`WorkerHostConfig.HostResource`), beside the board host's
`alpha:app-stack`. The two never wait on each other. The ticket's own lease is not qualified, so
one worker per ticket still holds across machines.

Capacity for a host-qualified resource is the host's `qaLeases` (default 1, at most 8), **but only
for a project that declares `parallelStacks: true`** in its config. Any other project gets one
stack per machine, whatever `qaLeases` says. `qaLeases` is a statement about the machine's RAM.
`parallelStacks` is a statement about the project: that its stack can run twice on one machine
without the copies fighting over ports and container names. Both have to be true.

`resourceCapacity` in the global config still describes the board host's machine only.

## The stack slot

Every claim carries `stackSlot`: which of the resource's capacity slots the run got (0-based),
from the lease's own `Slot`. `worker.ps1` sets `WHETSTONE_STACK_SLOT` and gives the prompt a
"Stack slot" line. On slot 0 it says "use the stack exactly as your skill says". On slot N it
tells the agent to bring the stack up as slot N, as the project's conventions say, and to block
if they say nothing about slots.

How slot N chooses its compose project name and ports is the project's own business. It belongs in
that project's conventions file or skill, never here. A project turns `parallelStacks` on only once
it has written that down.

## A ticket's files on both machines

The rule is one sentence: **each machine keeps a ticket's files at the same path under its own data
root** (`HostDataMirror`).

| Files | Direction | When |
|---|---|---|
| `ticket-files/<id>/`, `issue-attachments/<id>/` | board host to worker host | at the start of every worker host run (`GET /api/hosts/ticket-data/<id>`) |
| `qa-captures/<id>/` | worker host to board host | as each capture lands, every ~2s, plus one last pass at the end (`PUT /api/hosts/<host>/qa-captures/<id>/<file>?runId=`) |

- The prompt's "Machine" line tells a worker host's agent that a path the ticket tools give under
  the board's data root is at the same place under its own.
- `set_qa_shots`, handed a worker host's capture path, looks for the same path under the board host's
  root (it learns each worker host's root from its check-ins). It waits up to 20 seconds for a
  capture still on its way. It maps capture paths and nothing else.
- An upload is refused unless the run still holds the ticket.
- Files that reach the board after a run starts (an attachment fetched mid-run by
  `get_issue_context`) are not on the worker host. The prompt tells the agent to say so rather than
  guess. Fetching them on demand is left until it matters.

This also fixes a phase 1 gap: an implement run on a worker host could not open the attachments
`get_ticket` pointed it at.

## Review runs

A `review-pr` run on a worker host syncs the board host's refs like any other run. That is how its
clone has `origin/<their-branch>` at all. It does not fetch or hand back a branch of its own,
because it owns none. If the clone also has a real `origin` remote, the review worktree's own
`fetch origin` brings it fresher than the board host's last fetch.

## What a worker host needs for QA

Docker, Node and the Playwright browser, plus whatever the project's stack needs, all on that
machine: see `docs/worker-host-setup.md`, step 14. And on the board host, the host's entry must
list the testing skills, since phase 1's default leaves them out:

```json
{ "name": "bravo-host", "slots": 4, "qaLeases": 2, "projects": ["alpha"],
  "skills": ["speculate", "implement", "refine", "qa-test", "review-pr"] }
```

## Still not done

- **A live run.** As with phase 1, the QA copy refuses agent runs, so the first QA on a real second
  machine is the proof. See `docs/worker-host-setup.md`, step 15.
- **The board's stack panel** shows the board host's stack only. A worker host's stacks are seen
  through its berths.
- **Port offsets are the project's.** This repository provides the slot; a project that wants two
  QA runs at once on one machine writes its own rule for what slot N means.

---

# Keeping in step - built

A worker host is given work only while its Whetstone checkout is on the board host's exact commit,
because it runs its own copies of `worker.ps1`, the prompt and the guard. That rule stays. What
changed is who restores it: the runner, by itself.

- **The board says its commit.** Every check-in's answer to a configured host carries `boardCommit`
  (`WorkerHostEndpoints.CheckinResult`). An unknown name still gets a bare 404.
- **The runner decides** after each check-in's orders, with `Get-UpdateAction`: nothing when the
  commits match or `autoUpdate` is off in `host.json`; **wait** while runs are out (a worker must
  never see its scripts change under it, and the board sends a mismatched host nothing new, so they
  drain); **update** when idle and not stopping.
- **An update is a fast-forward to exactly the board host's commit** (`Update-OwnCheckout`): fetch
  if it is not here yet, refuse over modified tracked files, refuse anything that is not a
  fast-forward (a board host that went backwards or sideways is a person's call). Never past it:
  the board host may be behind its own origin. A commit not fetchable yet is retried on a 5-minute
  back-off. Each waiting or refusal is logged once per board commit.
- **The restart.** `runner.ps1` started normally is a supervisor (`Invoke-RunnerSupervisor`) around
  the runner itself (`-Inner`), and starts it again whenever it exits 75 - which it does only after
  a successful update. Task Scheduler keeps one long-lived process, and the task's action is the
  same as before. The supervisor is the one part an update does not reload.
