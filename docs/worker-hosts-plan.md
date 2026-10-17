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

### 1. Branch round trip (`data/worker-hub.ps1`, dot-sourced by `worker.ps1`)

This is the part that has to be exactly right, so it is built and tested first, against temporary
repos and with no fleet involved.

**The worker host's clone mirrors the board host's refs.** Each project on the worker host is a
clone whose remote `hub` is the board host's repo (over a file share, see 5). Before every run the
worker fetches:

```
+refs/heads/<base>:refs/heads/<base>          # the board's LOCAL base (mergeInsteadOfPr)
+refs/remotes/origin/*:refs/remotes/origin/*  # the board's view of origin
+refs/heads/<branch>:refs/remotes/hub/<branch>
```

The main checkout of the worker host's clone is kept on a detached HEAD, so its `<base>` is never
checked out and can be force-updated. It is a mirror and nothing commits to it.

Because the worker host sees exactly the refs the board host sees, `Resolve-BaseRef` and
`worker-base.tests.ps1` stay as they are. That rule is written in two places
(see "The base is not always origin's" in `CLAUDE.md`), and this keeps a third from appearing.

**Bringing the ticket branch up to date.** It goes in the ticket branch's worktree on the worker
host, or creates the branch from `hub/<branch>` when there is none. Then:

| Worker host's branch vs `hub/<branch>` | Action |
|---|---|
| equal, or no hub branch yet (new ticket) | nothing |
| behind (the board host ran it since) | fast-forward |
| diverged (the board rebased it, or a person did) | hard reset to `hub/<branch>`. Safe, because every worker host run ends by handing its commits back (below), so the worker host never holds commits that exist only there. A worktree with uncommitted changes refuses instead, and the run blocks the ticket. |

After that, `Resolve-Worktree` sees a local branch and runs unchanged. Worktrees on the worker host
are kept between runs, which keeps `node_modules` and friends warm. The board host's
`WorktreeReaper` never sees them. The runner reaps a worker host worktree once its ticket is Done
or its branch is gone from the hub.

**Handing back.** At the end of the run, after `Save-WorkerLeftovers`, the hand-off rebase and the
squash net (all of which run against the mirrored base and are unchanged):

1. `git push hub <branch>:refs/whetstone/incoming/<runId>`. A ref nothing ever checks out, so the
   push cannot hit git's refusal to update a branch that is checked out in one of the board host's
   worktrees, which is the normal state for a ticket the board host has run before.
2. `POST /api/tickets/{id}/branch/accept` with `{ runId, branch, sha, expectedOld }`.
   `expectedOld` is the hub sha fetched at the start of the run. The API does the update on the
   board host (piece 2), where `TicketGitService` already knows which worktree holds the branch.

The worker does the push, not the agent. It happens outside the agent session, so the guard never
sees it, and nothing here pushes to `origin`. That is the rule's own target, and it is unchanged.

Tests (`data/worker-hub.tests.ps1`): a hub repo and a worker host clone in a temp folder.
Fetching covers: new ticket, behind, diverged and clean, diverged and dirty, and base moved on the
hub with `mergeInsteadOfPr` both ways. Handing back covers: accepted, hub branch moved meanwhile
(refused), and a hub branch checked out in a hub worktree.

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
differs from the board host's is **mismatched**: it is shown, and given no orders, because the
worker host runs its own copies of `worker.ps1`, the prompt and the guard.

**Claims.** `ClaimNextRequest` gains `Skills` (the allowed skills; a ticket whose next skill is not
on the list is skipped like a busy one) and `Host` (recorded in the lease's holder label and on the
ticket, so the board can say where a ticket is running). The orchestrator always sends both for a
worker host slot. A board host claim sends neither and behaves exactly as today.

**Branch accept.** It refuses unless the ticket's lease is still held for that `runId`. So a run
the orchestrator gave up on (host offline, lease released, ticket picked up elsewhere) can never
overwrite what the next run did. If `refs/heads/<branch>` is not at `expectedOld`, it is refused
too. If the lease is good, it updates the branch:

- no board host worktree holds the branch: `git update-ref refs/heads/<branch> <sha> <expectedOld>`
- a clean board host worktree holds it: `git -C <wt> reset --hard <sha>`
- a dirty board host worktree holds it: refused (a person has edits there)

After that it deletes the incoming ref and records the branch on the ticket, as the existing branch
endpoint does. A refusal comes back to the worker, which blocks the ticket with the reason. It does
not retry.

**Project kit.** Everything a worker reads from `data/projects/<key>/`, served from the board host
on every run, so a skill edited on the board host takes effect on the worker host immediately:
`skills/`, `guard.ps1`, the conventions file, a read-only copy of the notes store, and the
`project.json` fields `worker.ps1` reads. It does **not** include credentials or `reflect-prompt.md`.
Step one of this piece is to go through every field `worker.ps1` reads from the claim and from
`project.json`, and decide for each one whether it is a path to remap, a value to pass, or a secret
to leave out.

**QaIsolation.** Every route above dispatches or feeds agent runs, so each one goes in
`QaIsolation`'s table. The QA copy refuses them all.

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
  "dashboardUrl": "http://192.168.1.20:5338",
  "projects": { "alpha": { "path": "D:\\src\\alpha", "hub": "\\\\board-pc\\repos\\alpha" } }
}
```

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

**Slots.** Board host slots stay 1..3 (`MaxHeartbeatWorkers`, still clamped at 3 on both sides) and
the steward stays 4. Worker host slots are numbered from 5, in blocks per host, and each block is
clamped on its own (1..8). `$Agents[slot]` gains `Host` and `RunId`. Its `Process` is `$null` for a
remote slot.

| Function | Change |
|---|---|
| fill loop | Per host, fill while it has a free slot, is online, matches the board host's commit, and is not paused. A worker host claims with its `Skills`, `Projects` and `Host`. Worker hosts fill **before** the board host, so a single ticket runs on the bigger machine. A config flag can reverse that (open question 2). |
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
- `data/worker-guard.ps1`: the worker passes the dashboard's host in the environment. The guard
  allows that host and port **exactly**, and everything else stays as it is. Tests: the dashboard
  URL is allowed, the same host on another port is denied, and another LAN address is denied.
- `CLAUDE.md` "The fleet never leaves this machine": the same sentence, plus one row for the runner.
  These three have to stay in step, as that section says.

### 7. Network and setup

- **API binding.** `Start-Whetstone.bat` sets `ASPNETCORE_URLS=http://localhost:5338;http://<board-lan-ip>:5338`
  when `lanUrl` is set in `config.json`, and only then. Off by default, so a machine with no worker
  host is unchanged.
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
  clone per project with `hub` set and its main checkout detached, the gitignored files the
  project's `worktreeCopyFiles` names copied in once, the agent CLI logged in to account 2,
  `host.json`, the Task Scheduler entry for the runner.

`docs/worker-host-setup.md` is written as part of this piece, as a checklist with a check after
each step.

### 8. UI

Minimal in phase 1:

- the fleet view labels each slot with its host, and shows a host as offline, mismatched or paused
- a ticket that is running says where
- `workerHosts` is edited in `config.json`. A Settings page for it is phase 2.

The agent slot numbers are already open-ended in `EventStore`. The check is that the fleet view
renders slots past 4 rather than assuming there are three.

## Order of work

Each step is its own commit, and steps 1 to 4 change nothing for a board host on its own:

1. Branch round trip + its tests (piece 1)
2. API: registry, check-in, orders, claim filter, accept, project kit, QaIsolation + tests (piece 2)
3. Worker host mode in `worker.ps1` (piece 4)
4. Runner + `host.json` (piece 3)
5. Orchestrator host awareness + tests (piece 5)
6. Prompt, guard and `CLAUDE.md` wording + guard tests (piece 6)
7. LAN binding, `worker-host-setup.md` (piece 7)
8. Fleet view (piece 8)
9. First live run: one worker host slot, one project, `implement` only, on a low-stakes ticket.
   Then one ticket deliberately moved board host to worker host to board host, to watch the round
   trip in both directions.

Step 9 is the only step that cannot be proven on the QA copy, which refuses agent runs by design.
Everything before it is proven by its tests and by the QA copy refusing the new routes.

## Open questions (the owner's)

1. **The project folder on the worker host.** The decision of 2026-09-03 was that no two of the
   owner's computers see each other's projects. A worker host has to read a project's skills, guard
   and notes to work on it. This plan serves them from the board host on every run (the project
   kit) rather than cloning the project folder's own repo. That way nothing drifts, and nothing is
   committed on the worker host, but the files are still on that disk. Is a worker host an
   exception to that decision, and for which projects?
2. **Which host fills first.** Planned: worker hosts first, to keep load off the board host. The
   other order keeps more of the work on the machine you are sitting at.
3. **Hard Stop.** Planned: it kills worker host runs too.
4. **Reflection.** Skipped on worker host runs in phase 1, so their lessons are lost. The follow-up
   is shipping the run log back and reflecting on the board host.
5. **File share or SSH** for the repos.
