# Bugs traced to their cause, and experiments on skills

Two features that turn the run-tracking columns on `CycleRuns` into decisions about the pipeline:

1. **Bug tracing.** A bug ticket's fix is blamed back to the commits, tickets and runs that wrote the
   lines it changed. The board suggests, and the owner confirms.
2. **Experiments.** A project runs one phase's skill (or its model, effort or provider) as two arms
   on alternating tickets, measures each arm per ticket to Done, and stops early if the variant
   clearly breaks quality.

The second depends on the first, because escaped bugs are one of its guardrails. The order of work
is: the plan doc, then bug tracing end to end, then experiments end to end.

The decisions marked *settled* came from the owner. The rest are decided here, each with a reason,
favouring numbers that stay trustworthy over what is quickest to build.

---

## 1. Bug tracing

### 1.1 Marking a ticket as a bug

**Decided: a field, `Ticket.IsBug`, not a label.** Labels (`Models/Labels.cs`) are deliberately
invisible to everything but the owner's filing: "nothing about how a ticket is worked may ever turn
on these". A bug flag is what makes the board run a blame when the ticket lands, and what an
experiment's guardrail counts. That is board behaviour, which is exactly what labels promise never
to drive. A label called "bug" would also be one rename away from silently switching tracing off.

- It is set at creation (`CreateTicketRequest.IsBug`) or later from the drawer (`PUT /api/tickets/{id}/bug`).
- No MCP tool sets it. A worker cannot reclassify the work it was given.
- It is shown in the drawer and as a chip on the card.

### 1.2 When the board traces

A trace runs when a bug ticket:

- **is merged by the board** (the `/git/merge` route, or the steward's merge), with the merge sha in hand; or
- **reaches Done** by any route without one (pull request and TFVC projects, a hand move).

It also runs from the drawer's "Trace again" button (`POST /api/tickets/{id}/bug/trace`). A trace
replaces the suggestions it made before. It never touches a link the owner has confirmed or
rejected.

The trace runs in the background (`BugTraceQueue`, a hosted single-reader channel like
`AnalysisQueue`), so neither a merge nor a phase move ever waits on `git blame`.

### 1.3 What is blamed

For each repo of the project (`TicketGitService.TargetsOf`, so a project of one repo is the same
code run once), the trace finds the fix's range, from `parent` to `tip`:

| The ticket has | `tip` | `parent` |
|---|---|---|
| `MergeSha` (merged here, `--no-ff`) | `MergeSha^2` | `merge-base(MergeSha^1, MergeSha^2)` |
| A branch that still exists | the branch | `merge-base(<base-ref>, branch)` (`BaseRefAsync`, the same rule as everywhere else) |
| Neither, but recorded delivered commits (1.5) | the newest recorded commit | its first parent's merge base with the base ref |

A repo the fix did not touch (an empty `parent..tip` diff) is skipped. That gives the same answer
as `TouchedAsync` without needing a worktree that may already be reaped.

In each touched repo:

1. `git diff -U0 --no-renames -w <parent> <tip>`. The **old side** of each hunk is the lines the fix
   changed or removed. `-w` drops hunks that only re-indent.
2. Those line ranges are blamed at `parent`:
   `git blame --porcelain -w -M -C -L a,b [-L ...] <parent> -- <file>`.
   - `-w` ignores whitespace-only changes.
   - `-M` follows lines moved within a file.
   - `-C` follows lines moved or copied from other files changed in the same commit.
   - If the repo has a `.git-blame-ignore-revs`, it is passed with `--ignore-revs-file`, so a
     project's own list of reformatting commits is respected.

   `-C -C` (copies from any file in history) is **not** used. Its cost grows with the repo's history
   on every blamed line, and the case it adds (code copied from an untouched file in an older
   commit) is rare. When it does happen, it points at the copy's own commit, which is still a
   reasonable answer.
3. **A fix that only adds lines** has nothing on its old side. In that case the trace blames the
   one line either side of each insertion, at half weight. A missing guard is the common bug of
   this kind, and the code around the hole is the code that should have had it. The suggestion says
   it is "from neighbouring lines", so its confidence is visible.
4. Files that are generated or vendored are left out: lockfiles and anything under `wwwroot/`,
   `dist/` or `node_modules/`. A rebuilt bundle is not where a bug was written.

### 1.4 From commits to source tickets

Each blamed commit is mapped to a ticket through `TicketCommits`, a new table recording which
commits on a base each ticket delivered:

| Column | Meaning |
|---|---|
| `TicketId`, `ProjectKey` | The ticket that delivered the commit |
| `Repo` | The repo's folder on a project of several; empty on a project of one |
| `Sha` | The commit |
| `PatchId` | `git patch-id --stable` of it, so a cherry-pick or rebase-merge still matches |
| `Source` | `merge` (read from a board merge), `branch` (the branch's commits at Done) |
| `RecordedAt` | When it was recorded |

It is written at two points:

- **A local merge.** `rev-list MergeSha^1..MergeSha^2` is recorded with `Source = merge`. The merge
  is `--no-ff`, so these are exactly the shas blame will report.
- **Done.** The branch's commits against its base ref are recorded per touched repo, with
  `Source = branch`. On a pull-request project those shas may be squashed or rebased on GitHub, so
  the patch id is what matches. Because this pipeline delivers one commit per ticket, a GitHub
  squash of that one commit has the same patch id unless the merge resolved a conflict.

**History.** The first trace in a project backfills `TicketCommits` for every Done ticket that has
a `MergeSha` and no rows yet. That covers every ticket the board merged before this table existed.

A blamed commit is matched in this order:

1. by sha;
2. by patch id;
3. by the subject starting with a Done ticket's issue commit ref (`PROJ-123: ...`), which is the
   squash rule's subject (`Compress-TicketCommits`);
4. otherwise it is **unmapped**: written outside the board, or before it existed.

A blamed commit that belongs to the bug ticket itself is dropped. It should not happen when
blaming at the parent, but it can on a re-trace of a ticket that was reopened.

### 1.5 What a link stores

`BugLinks`, one row per bug ticket and source:

| Column | Meaning |
|---|---|
| `BugTicketId` | The bug |
| `SourceTicketId` | The ticket whose commits wrote the blamed lines. Null for an *unmapped* group (commits from outside the board), which is still shown so a 100%-unmapped trace does not look like an empty one |
| `ProjectKey` | Denormalised for per-project counts |
| `Status` | `Suggested`, `Confirmed`, `Rejected` (settled) |
| `Origin` | `Blame` (suggested by a trace) or `Manual` (the owner named a source the trace did not) |
| `Lines` | Blamed lines attributed to this source (neighbouring lines count half) |
| `Share` | `Lines` over the trace's total, 0..1, so the strongest suggestion is obvious |
| `CommitsJson` | `[{repo, sha, subject, lines}]`: the introducing commits (settled) |
| `Neighbours` | True when every line came from around an insertion (1.3, step 3) |
| `Escaped` | Whether this counts as an escaped bug (1.7). Recorded when the link is written |
| `FoundBy` | `Owner`, `LaterTicketQa`, `Production`, `Steward` (settled). Set on confirm |
| `MissedBy` | `speculate`, `implement`, `qa-test`, `refine`, `review` (settled). Set on confirm |
| `Note` | The owner's note, if any |
| `TracedAt`, `DecidedAt` | When suggested; when confirmed or rejected |

There is a unique index on `(BugTicketId, SourceTicketId)`.

Each trace is also recorded in `BugTraces` (one row per bug ticket, overwritten on re-trace):
`TracedAt`, `LinesBlamed`, `RangesJson` (the `parent..tip` per repo) and `Note`. The note says why a
trace found nothing ("the fix's branch is gone and no delivered commits were recorded"), so an empty
suggestion list explains itself.

`MissedBy` is a skill name rather than a `TicketPhase`. It names a stage of the pipeline, not a
board column, and skill names are what `CycleRun.Skill` and the experiments use.

### 1.6 Confirming: a route with no tool behind it (settled)

| Route | What it does |
|---|---|
| `POST /api/bug-links/{id}/confirm` `{ foundBy, missedBy, note? }` | Suggested or Rejected to Confirmed. Both `foundBy` and `missedBy` are required |
| `POST /api/bug-links/{id}/reject` `{ note? }` | To Rejected |
| `POST /api/tickets/{bugId}/bug/links` `{ sourceTicketId, foundBy, missedBy, note? }` | "The cause is ticket N", confirmed in one step (`Origin = Manual`). Also what "change" means in the UI: reject the wrong suggestion and name the right ticket |
| `POST /api/bug-links/{id}/reopen` | Back to Suggested, for a slip of the finger |

There is no MCP tool that writes `BugLinks`, `BugTraces`, `TicketCommits` or `Ticket.IsBug`. This
is the steward's pattern: separation by absence. A test enumerates every `[McpServerTool]` in the
assembly and fails if any of them can write these tables (2.9).

*May see.* Neither door gets a read tool in this change, because nothing in a skill asks for one
yet. When the steward's skill wants "bugs traced to this unit", a read-only `steward_bug_links` goes
on `/mcp/steward`, and the test's allowlist names it.

### 1.7 What counts as escaped (settled)

**An escaped bug is a confirmed link whose source ticket was already merged or Done when the bug
ticket was created.** Whoever found it (`FoundBy`) does not matter.

- *Merged* is `MergedAt` when the board merged it.
- Otherwise *Done* is the first `PhaseTransition` to Done.

`Escaped` is computed when the link is written and recomputed on confirm. It is stored because the
experiments' guardrail and the analytics marts both count it, and the definition should live in one
place (`BugTraceService.IsEscaped`).

### 1.8 The run that wrote the bug

**Decided: record each run's commit range, and derive the run from it. Nothing is entered by hand.**

Two new columns on `CycleRuns`, beside the other run-tracking columns:

- `CommitRangeJson`: `[{repo, from, to}]`, each writable repo's HEAD when the agent started
  (`$runPlaces[].startHead`, already taken) and when it ended. Null when the run made no commits.
- `RunFilesJson`: the paths the run's own commits changed (`from..to --name-only`). Uses the same
  repo-folder prefix as `ChangedFilesJson`, capped at 200 paths.

**How a squashed commit maps back to runs.** Squashing rewrites the shas, so after refine squashes a
branch the run ranges no longer contain the final commit. The board derives the run in two tiers,
and says which tier it used:

1. **Exact.** The introducing commit is inside a run's recorded range
   (`git merge-base --is-ancestor from C` and `C` is an ancestor of `to`, while both still exist).
   This holds for a ticket merged without a squash and while the pre-squash objects are still
   around.
2. **Likely.** Otherwise: among the source ticket's runs that made commits, those whose
   `RunFilesJson` contains a blamed file of that commit. The latest such run before the ticket's
   merge or Done is "most likely", and the others are listed.

`RunFilesJson` survives a squash because it is a list of paths, not shas. It is the reason to record
it rather than lean on `ChangedFilesJson`, which is the whole branch's diff and would name every
run that ran after the line was written.

This derivation is computed on read (`GET /api/tickets/{id}/bug`). It is never stored, because the
runs and links it joins are already stored.

### 1.9 UI

**On the bug ticket**, a "Where this bug came from" section in the drawer, shown when `IsBug` is set:

- The trace's state: not traced yet, traced at, and its note.
- Each suggestion: source ticket (a link), share bar, introducing commits, and derived run(s) with
  their exact/likely tier.
- *Confirm* opens two small pickers (Found by / Missed by), and *Reject* sits beside it.
- "Name another source" takes a ticket number, and "Trace again" re-runs the blame.

**On the source ticket**, "Bugs traced here" lists confirmed links (and suggested ones, dimmed),
with the bug, its `FoundBy` / `MissedBy`, and whether it escaped. It draws nothing when there are
none, like `StewardOnTicket`.

**On the card**, a small "bug" chip. The drawer gets a toggle beside the other ticket options.

All of it uses the theme tokens and the motion classes (`orbit-tap` on controls, `orbit-swap` when a
row changes state), and folds to one column below `NARROW_PX`.

---

## 2. Experiments

### 2.1 What an experiment is (settled)

One project, one phase's skill (`speculate`, `implement`, `qa-test`, `refine`, `review-pr`), two
arms. An arm is a name plus optional overrides:

| Override | Meaning |
|---|---|
| `SkillVariant` | A file name. The run reads `data/projects/<key>/skills/<skill>/variants/<name>.md` instead of `skill.md` |
| `Model`, `Effort`, `Provider` | Replace what the skill's agent role resolves to |

A control with no overrides is the usual shape, but control may override too ("effort high vs
xhigh").

**Variant files live in the project's folder** (settled), which is gitignored here and its own repo.
They already travel to worker hosts: `ProjectKit.Build` zips `skills/` recursively, so
`skills/<skill>/variants/*.md` is in the kit with no change. A test pins that (2.9).

`SkillDriftService` compares `skills/<phase>/skill.md` only, so a variants folder is invisible to
drift. That is intended: a variant is an experiment's text, not a copy of the template.

### 2.2 Schema

`Experiments`:

| Column | |
|---|---|
| `Id`, `ProjectKey`, `Skill`, `Name`, `Hypothesis` | What is being tested and why |
| `Status` | `Running`, `Stopped`, `Concluded` |
| `MinTicketsPerArm` | Set at start (default 8); enrolment closes when both arms have it |
| `WaitDays` | Set at start (default 21): how long after the last enrolled ticket lands before a verdict |
| `StartedAt`, `StoppedAt`, `ConcludedAt` | |
| `StopKind` | `Human` or `Guardrail` |
| `StopReason` | The rule that fired, in words, with its numbers |
| `Winner` | The arm key the owner declared (`control` / `variant`), or `none` |
| `ConclusionNote` | The owner's words |

`ExperimentArms`: `Id`, `ExperimentId`, `Key` (`control` | `variant`), `Name`, `SkillVariant`,
`Model`, `Effort`, `Provider`. Two rows per experiment.

`ExperimentAssignments`: `Id`, `ExperimentId`, `TicketId`, `ArmKey`, `Ordinal` (1, 2, 3... in order
of enrolment), `AssignedAt`. Unique on `(ExperimentId, TicketId)`.

`CycleRuns` gains:

- `ExperimentId`: the experiment the ticket is enrolled in for this run's skill, or null.
- `ExperimentArm`: the arm key it is assigned.
- `ExperimentApplied`: whether that arm's overrides were actually applied. False after a stop, or
  when the variant file was missing at claim time.

`Applied` is the honest half. An analysis of the arms filters on it, and a run that silently fell
back to control can never be counted as the variant's.

At most one experiment is `Running` per project and skill. The service refuses a second.

### 2.3 Assignment (settled: sticky, first claim into the phase)

Assignment happens at the claim (`/api/heartbeat/claim`, after `ClaimNextAsync` returns), in the
board, never in the worker:

1. Is there a `Running` experiment for this ticket's project and the claimed skill? If not, no
   experiment.
2. Does the ticket already have an assignment in it? Then use that arm. This is the stickiness:
   every later pass of the phase (a QA failure back to implement, a resume, a review's changes) runs
   the same arm, so rework counts against the arm that caused it.
3. Otherwise, if enrolment is open (some arm is below `MinTicketsPerArm`), enrol it: the next
   ordinal, and the arm the sequence says (below).
4. Otherwise, run without an experiment.

A ticket is only ever enrolled from a claim into the tested skill. Speculation tickets are never
enrolled in an implement experiment early.

**The split: permuted blocks of two.** Each consecutive pair of enrolments contains one control and
one variant, in a random order per pair. Fully random assignment can leave 9 against 3 at this
fleet's volume. Strict alternation is balanced but predictable, and claim order is not random: the
steward files a plan's tickets in runs, and priority bands interleave, so A-B-A-B can line up with a
pattern in the work. Pairs keep the arms balanced to within one at every count, and keep the order
inside each pair unpredictable.

The random draw is `RandomNumberGenerator`, and the drawn arm is stored with the assignment, so
replaying the history needs no seed.

### 2.4 Running it in the worker

The claim response gains an `experiment` block when the ticket is enrolled:

```json
"experiment": { "id": 3, "arm": "variant", "applied": true,
                "skillVariant": "terse", "model": null, "effort": "high", "provider": null }
```

When `applied` is false, the block only names the experiment and the arm, with no overrides. That
is every run after a stop, so the worker still reports which arm the ticket was in.

`data/worker.ps1` applies it right after `Get-AgentRole -Role $skill`:

- **Provider** replaces `$AgentProvider` (`Get-AgentProvider -Name`). Model and effort then default
  from that provider's defaults, unless they are overridden too.
- **Model** replaces `$phaseModel`, through the same `Test-AgentModelBelongsTo` check
  `Get-AgentRole` uses. A model that is not this provider's is refused, logged, and the arm is
  reported as not applied.
- **Effort** replaces `$WorkerReasoningEffort`.
- **SkillVariant** replaces `$skillFile` with
  `Join-Path $Claim.project.skillsDir "<skill>/variants/<name>.md"`. It is built from `skillsDir`,
  not handed over as a path, because `runner.ps1` rewrites `skillsDir` and `skillFile` for its own
  data root on a worker host. A file that is missing reports `applied = false` and runs `skill.md`.

The existing `$runDetail.skillHash` then hashes whichever text was actually read, so the variant
shows up as its own hash with no further change. `$runDetail` gains `experimentId`,
`experimentArm` and `experimentApplied`, sent in the cycle report's `detail`.

The provider override is limited to providers some role already uses, checked when the experiment
is created (`AgentRoles.Resolve` over the configured roles). The orchestrator checks the installed
CLIs and reaps processes for `$AgentProvidersInUse`. An arm on a provider nothing else uses would
start processes the orchestrator never reaps.

### 2.5 What is measured, per ticket

The unit is **the ticket**, not the run (settled). Runs inside one ticket are not independent, and
the question is what an arm costs to get a ticket to Done.

For each enrolled ticket, counting only runs from its assignment onward, across **every** skill (a
terser speculate that costs implement an extra pass has cost that pass):

| Measure | Definition |
|---|---|
| `worker minutes` | Sum of `DurationSeconds` over those runs |
| `fresh tokens` | Sum of `InputTokens + OutputTokens + CacheCreationTokens`. Cache reads are shown beside it, not added, because they cost the rate limit far less |
| `had QA failure` | Any transition out of `QaTesting` that is `Backward`, after assignment |
| `had rework` | Any run with an `EntryReason` of `qa-failed`, `refine-sent-back` or `review-changes`. Conflicts are excluded: they are caused by other tickets landing, not by the arm |
| `rework share` | Rework runs over all runs, for display |
| `escaped bug` | Any confirmed, escaped `BugLink` whose source is this ticket |
| `Done` | Reached Done, and when |

The page shows medians of time and tokens over Done tickets, and rates of the three yes/no measures
over tickets with an outcome.

A ticket has an outcome for QA failure and rework once it is Done. It has an outcome for escaped
bugs once it has been Done for `WaitDays`.

### 2.6 Guardrails and auto-stop

**Rule.** The board auto-stops when, for any one guardrail (QA failure, rework or escaped bug), all
of these hold:

- the variant has at least 6 tickets with an outcome for it;
- the variant has at least 4 tickets with the bad outcome;
- a one-sided Fisher's exact test, variant worse than control, gives p < 0.01.

**Why this shape, at this volume:**

- **Per ticket and yes/no, with Fisher's exact test.** With 5 to 15 tickets an arm, normal
  approximations are meaningless. Fisher's test is exact at any count and needs no assumptions
  beyond independent tickets.
- **p < 0.01, not 0.05.** The check runs after every recorded run and every confirmed link: dozens
  of looks over an experiment's life. At 0.05 per look, a pair of arms that are identical would be
  stopped by chance far too often. 0.01, with the floor on counts, keeps a false stop rare. A stop
  that fires is still conservative in the direction that matters: new tickets go back to control.
- **The floors.** At least 6 tickets and 4 bad ones means "one unlucky ticket" can never stop an
  experiment. For example, 4 of 6 against 0 of 6 is p ≈ 0.03 and does *not* stop; 5 of 6 against 0
  of 6 does.
- **Not worse, not better.** The test is one-sided. A variant that is better on quality is never
  stopped for it.

The check is `ExperimentService.CheckGuardrailsAsync(projectKey)`, in the app (settled). It is
called after each `/api/heartbeat/cycles` record and after a bug link is confirmed. The arithmetic
is a pure static function (`ExperimentStats.FisherOneSided`), tested against known values.

**On a stop, by the guardrail or the owner:**

- `Status = Stopped`, with the kind and reason recorded.
- A System comment goes on every enrolled ticket still in flight.
- Every later claim reports the arm with `applied = false`, so the run uses control's settings.

The settled decision says "new tickets go back to control". This plan goes one step further:
**tickets already on the variant go back to control too.** A guardrail stop means the variant is
believed to be doing harm, and letting the tickets already on it keep running it would carry on
that harm. The experiment's numbers stay intact: runs after the stop record `applied = false`, so
the analysis can stop counting each ticket at the point it left the variant.

### 2.7 The verdict, and waiting for escaped bugs

Escaped bugs arrive weeks after a ticket lands. So an experiment moves through stages that are
derived from its data, not stored:

| Stage | When |
|---|---|
| **Enrolling** | `Running`, and some arm is below `MinTicketsPerArm` |
| **Landing** | Enrolment is full, and some enrolled ticket is not Done yet |
| **Waiting** | Every enrolled ticket is Done, but the last reached Done less than `WaitDays` ago. The page counts down |
| **Ready** | The wait is over. A verdict can be declared |
| **Stopped** | Stopped by the guardrail or the owner. A verdict can be declared at any time (usually `control` or `none`) |

`POST /api/experiments/{id}/declare` `{ winner, note }` is refused before **Ready** or **Stopped**.
A ticket that will never reach Done (abandoned) holds an experiment in Landing. The owner's way out
is to stop it and declare.

**The suggested verdict** is shown on the page, never applied:

- A guardrail **passes** for the variant unless it is worse by one-sided Fisher p < 0.10. This is
  looser than auto-stop, because it is one look at the end, not many.
- If every guardrail passes, the arm with the lower median worker minutes **and** fresh tokens per
  Done ticket is suggested.
- If time and tokens disagree, the page says so and suggests nothing.
- If the variant fails a guardrail, control is suggested.

Declaring a winner changes no configuration. Adopting a variant is the owner's edit to the skill
(through the Skills page and "A skill exists more than once"), or to the agent role in Settings.
The experiment records the decision and the evidence.

### 2.8 Who controls it (settled: the human only)

| Route | |
|---|---|
| `GET /api/experiments?project=` | The list, with stage and per-arm results |
| `GET /api/experiments/{id}` | One, with every enrolled ticket's measures |
| `GET /api/experiments/variants?project=&skill=` | The variant files on disk, for the create form |
| `POST /api/experiments` | Create and start it |
| `POST /api/experiments/{id}/stop` `{ reason }` | Stop it |
| `POST /api/experiments/{id}/declare` `{ winner, note }` | Declare a winner |

No MCP tool reads or writes them. Workers are told nothing about which arm they are in. The arm
reaches the worker script through the claim, and never reaches the agent's prompt beyond the skill
text itself.

### 2.9 Tests

C# (`Whetstone.Tests`):

- `BugTraceTests`, against real repos in a temp folder, like `MultiRepoGitTests`:
  - a fix changing two lines from two earlier tickets suggests both, with their shares;
  - a whitespace-only change is ignored;
  - a moved block blames its original ticket;
  - an add-only fix blames its neighbours at half weight;
  - a multi-repo project blames in each touched repo, with repo-prefixed commits;
  - a patch-id match maps a rebased commit;
  - unmapped commits become a null-source group;
  - a re-trace keeps confirmed and rejected links;
  - escaped follows Done or merged before the bug was filed.
- `ExperimentTests`:
  - assignment is sticky across passes and resumes;
  - permuted pairs stay balanced at every count;
  - enrolment closes at the minimum;
  - a second running experiment for the same project and skill is refused;
  - the claim carries the overrides, and `applied = false` after a stop;
  - `FisherOneSided` matches known values;
  - auto-stop fires at 5/6 vs 0/6 and not at 4/6 vs 0/6;
  - the stages, and declare refused before Ready.
- `HumanOnlyLimitTests`: every `[McpServerTool]` method in the assembly is enumerated, and none
  may take a dependency on `BugTraceService` or `ExperimentService`, or have a name mentioning
  bugs, links or experiments outside a read-only allowlist (empty today).
- A `ProjectKit` test: a variant file under `skills/<skill>/variants/` is in the kit.

PowerShell:

- `data/worker-experiment.tests.ps1`: the override function (`Set-ExperimentArm`) applied to a role
  and a claim:
  - each override alone;
  - a model of the wrong provider refused;
  - a missing variant file falls back with `applied = false`;
  - `applied = false` from the board changes nothing.
- The run-range and run-files helper (`Get-RunCommitRange`) goes in `data/worker-tracking.tests.ps1`.

### 2.10 For the analytics layer

The DuckDB/dbt session models these tables. They are the source of truth, and the marts compute
from them:

- `BugLinks`, `BugTraces`, `TicketCommits`, and `Tickets.IsBug`;
- `Experiments`, `ExperimentArms`, `ExperimentAssignments`;
- `CycleRuns.ExperimentId`, `ExperimentArm`, `ExperimentApplied`, `CommitRangeJson`, `RunFilesJson`.

The board computes only what it must act on: the auto-stop check and the stage and verdict shown on
its own page. Per-arm comparisons over time, escaped-bug rates by phase (`MissedBy`), and cost per
ticket belong in the marts.

---

## 3. Phasing

1. **This document.** Committed on its own.
2. **Bug tracing, server.**
   - Migration: `Tickets.IsBug`, `TicketCommits`, `BugLinks`, `BugTraces`, and `CycleRuns.CommitRangeJson` / `RunFilesJson`.
   - `TicketGitService.Blame.cs` (ranges, diff, blame, patch ids), `BugTraceService`, `BugTraceQueue`.
   - Hooks at merge and at Done; routes; tests.
3. **Bug tracing, worker.** `CommitRangeJson` / `RunFilesJson` in `$runDetail`; tests.
4. **Bug tracing, client.** The drawer section, "bugs traced here", the bug toggle and chip;
   `wwwroot` rebuilt and committed.
5. **Experiments, server.** Migration: the three tables and three `CycleRuns` columns.
   `ExperimentService` (assignment, stats, guardrails, stages), the claim hook, routes, tests.
6. **Experiments, worker.** `Set-ExperimentArm` in `worker.ps1`, the detail fields, and its test
   file.
7. **Experiments, client.** An Experiments section on the Metrics tab: create, watch, stop and
   declare. `wwwroot` rebuilt and committed.
8. **Proof on the QA copy:**
   - a bug ticket in a placeholder project traced, confirmed, and shown on both tickets;
   - an experiment created, a claim enrolled with its arm, a run recorded with it, a stop, and a
     declare.

No skill text changes in any phase. A worker does not need to know about either feature to be
measured by it. That is why no template or project copy is touched, and why "A skill exists more
than once" does not apply.

The experiments UI goes on the Metrics tab rather than a tab of its own. It reads the same runs, and
the tab bar is already long on a phone.
