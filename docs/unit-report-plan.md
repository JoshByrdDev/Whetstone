# Unit report: a Classroom for a whole plan unit

Implementation plan, v1. A *unit* is one numbered piece of a steward plan (`StewardUnit`); the
owner's plan document may call it a slice. Code says unit throughout, as the steward code already does.

## What it is for

When a unit is built, its owner has one decision left that is theirs: release it. Today the
evidence for that decision is spread across every ticket in the unit — each one's Classroom
analysis, QA shots, steward calls and release notes — and is read one ticket at a time.

A unit report is one document that answers, in order:

1. **What did I agree to, and did I get it?** One coverage ruling per outcome in the unit's spec,
   with the tickets that delivered it and the screenshot that proves it.
2. **How does it work now?** The unit's user flows, merged across tickets, marked new / changed
   against the state before the unit, each stop showing the newest screenshot of that screen.
3. **What was decided without me?** The steward's `Call` entries on the unit's tickets.
4. **What does release need from me?** The unit's release notes, open ones first.
5. **How did it go?** Bounces and cost per ticket.

Plus a narrated walkthrough, one chapter per outcome group, played by the existing player.

## Shape of the solution

A **synthesis over what already exists**, not a fresh read of the repo. Every ticket in the unit
already has (or can be given) a Classroom analysis: a validated block document with flows, coverage
and code citations. The unit run reads those documents, the outcomes, and the shots, and writes one
document in the *same* block schema. That buys three things:

- the input is small and already digested, so the run is cheap and fast;
- `AnalysisDoc`, `UserFlow`, the coverage map and `WalkthroughPlayer` render it unchanged;
- the coverage machinery works as-is, because an outcome is just a requirement line with a
  different id (see "Outcomes as segments").

Sections 4 and 5 are **not written by the model**. They are facts the server already has
(`StewardEntry` release notes, `TicketJourney`, `CycleRuns`) and are rendered from the DTO, so they
can never be paraphrased wrong.

## Decisions

### A table of its own, a walkthrough row shared

`UnitReport` is a new table rather than `TicketAnalysis` with a nullable `TicketId`. Ticket analyses
are keyed by ticket through the gate, the queue, staleness, the picker and ~30 call sites; none of
that applies to a unit, and widening the key would make every one of them handle a case it has no
meaning for.

The walkthrough is different: its row is subject-agnostic except for its foreign key. So
`AnalysisWalkthrough` gains a nullable `UnitReportId` and `AnalysisId` becomes nullable, with a
check that exactly one is set. One clip cache, one deck format, one player.

```csharp
public class UnitReport
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public string Plan { get; set; } = "";       // StewardUnit.Plan - a unit number means nothing without it
    public int Unit { get; set; }

    public string FromSha { get; set; } = "";    // base before the unit (see "The range")
    public string ToSha { get; set; } = "";      // base when the report was written
    public string TicketIdsJson { get; set; } = "";   // [ticketId, analysisId] pairs it read - the staleness key
    public string ShotsJson { get; set; } = "";       // the shot pool (see below)

    public string Headline { get; set; } = "";
    public string BlocksJson { get; set; } = "";      // AnalysisDocument, same schema

    // accounting, as on TicketAnalysis
    public long InputTokens, OutputTokens, CacheReadTokens, CacheCreationTokens;
    public string? Model; public int DurationMs; public int StepCount;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
```

Rows accumulate, like ticket analyses: the report written when the unit was built, next to the one
written after a late fix, is worth having.

### Outcomes as segments

`AnalysisDocument.Parse(structured, segments)` joins coverage rulings against a list of
`DescriptionSegmenter.Segment`. A unit's outcomes become that list directly:
`Segment(Id: "A2", Kind: "outcome", Text: <outcome text>, Depth: 0, Origin: <group heading>, OriginRole: null)`.
The server's "unaddressed" fill-in then catches any outcome the run forgot to rule on, for free.
`deferredTo` points at the next unit's outcomes, not at sibling tickets.

### The shot pool, and "newest"

Shot references in a document are integers resolved by `order` against a shot list the page
provides (`AnalysisContext` → `QaShots`). A report gets its own list: a **pool** numbered 1..N
across every ticket in the unit, stored in `ShotsJson` as
`{n, ticketId, ticketOrder, caption, focus, mergedAt, file}`.

- **Snapshotted.** `QaShotStore` deletes a ticket's shots on every re-QA, so the pool is copied to
  `data/unit-reports/<reportId>/shots/<n>.<ext>` when the run starts. A report never loses its
  pictures to a later ticket.
- **Ordered newest-merged first.** "Merged" is the ticket's move to Done (from `PhaseTransitions`)
  until `Ticket.MergedAt` exists (phase 1 adds it).
- **Same-screen judgment is the model's, with eyes.** The prompt gives each shot's path, and the run
  may open them (ticket analyses read captions only; here seeing them is the point). Rule: when two
  shots show the same screen, cite the one from the later-merged ticket. A ticket's own shot may still
  be cited for *its* outcome's evidence when a later ticket changed that screen in an unrelated way;
  the prompt says to note that.
- The validator rejects pool numbers that do not exist, as the walkthrough validator already does.

v2 (separate plan): a "unit tour" run that re-shoots every flow stop on the built base in a local
stack, replacing the pool. Nothing here should make that harder: it only swaps where the pool
comes from.

### The range

Code citations in the report point at `ToSha`. The report's `/diff` route diffs `FromSha..ToSha`.

- `ToSha`: `BaseRefAsync` resolved at dequeue — the same base ref rule the rest of the board uses,
  so a `mergeInsteadOfPr` project reads its local base.
- `FromSha`: the previous unit's `ReleasedSha` when there is one; otherwise the oldest
  `git merge-base <base> <analysis.HeadSha>` across the unit's tickets (the earliest fork point).
  That works for squash and merge alike, because it never needs the ticket's commit to be in base.
- The range can contain commits that are not this unit's work. The report says how many
  (`git rev-list --count` minus the unit's merges, once `MergeSha` is recorded) rather than hiding it.

### Inputs must be current

The unit run needs a Full-lens analysis for each ticket that is not stale (`TicketAnalysisService.IsStale`).
The gate for a unit item:

- a unit ticket still open (not Done) → **Wait** ("ticket #N is still in <phase>"), unless the
  request was manual, in which case it runs and the report says which tickets were unfinished;
- a ticket lacking a current analysis → enqueue that ticket's Full analysis and **Wait**;
- the unit's `steward:<key>` lease held → **Wait** (a merge may be landing).

## Phases

Each phase is a commit. Tests in `Whetstone.Tests`, run with `-c Release`.

### 1. Record what a merge did

- `Ticket.MergeSha` and `Ticket.MergedAt`, written wherever a ticket's branch lands in base:
  `StewardService.MergeAsync` (it already has `merged.MergeSha`) and the board's own merge path in
  `TicketGitService`. PR projects: left null; the report falls back to the Done transition.
- Migration. Test: a steward merge stamps both.

Small and independent; worth doing first because every report after it gets a better range.

### 2. The material, as a pure function

`Services/UnitReport/UnitMaterial.cs`: given a project's tickets, a plan view, outcome states,
steward entries and analyses, build the run's input. No I/O, so it is tested against rows like
`StewardServiceTests` does.

- tickets of the unit: filter on **both** `PlanKey` and `PlanUnit` (the CLAUDE.md rule);
- outcomes → segments;
- shot pool ordering;
- per ticket: analysis headline, its `userFlow` and `coverage` blocks, `ImplementationSummary`,
  bounces, steward `Call` entries;
- what is missing (no analysis, stale, open ticket) as a list the gate and the report both read.

Tests: two plans with a unit 1 each, only one plan's tickets returned; outcome segments carry their
group; pool ordered by merge; a stale analysis reported missing.

### 3. The run

- `UnitReportRunner` beside `AnalysisRunner`: build prompt → `AgentJsonRunner.RunAsync(provider,
  readDir: <repo at ToSha>, ..., AnalysisDocument.JsonSchema)` → `AnalysisDocument.Parse(doc,
  outcomeSegments)` → persist. Read dir: a detached worktree at `ToSha` under the run dir, removed
  after; the project checkout is never moved.
- `UnitReportPrompt.Build` (public static, like `BuildPrompt`, tested in `AnalysisPromptTests`
  style). Fixed spine the prompt asks for: *What you agreed* (coverage, one ruling per outcome),
  *How it works now* (one `userFlow` per feature area, at most 14 stops each, `change` relative to
  before the unit), *Decisions made for you* (from the `Call` entries: keyValue or callouts, with
  a `risk` callout for any the run would have asked about), *Where to look* (a `readingPath`
  across tickets). Instructions to prefer the ticket analyses' own findings over re-deriving them,
  and to read code only to settle a disagreement between two of them.
- Queue: `AnalysisQueue.Pending`/`Running` gain a subject (`TicketId` *or* `(Project, Plan, Unit)`).
  One slot still: a unit run waits its turn behind ticket runs. Its gate is the one above.
  `IAnalysisExecutor` gets `RunUnitAsync`. Existing queue tests keep passing untouched.

### 4. Its walkthrough

- Split `WalkthroughService.WriteAsync` into *gather material* (ticket-specific today) and *script,
  validate, voice* (subject-agnostic). Add a unit gatherer: shots from the pool, attachments none,
  changed files from `FromSha..ToSha`, code cut at `ToSha`.
- `WalkthroughPrompt` gains a unit variant: one chapter per outcome group, a `title` scene, then for
  each group its shots, its flow and at most one code scene; a closing `cards` scene for the
  decisions made for you.
- `AnalysisWalkthrough` takes the nullable `UnitReportId` described under "Decisions". Tests in `WalkthroughTests`: a deck citing
  pool shot 7 of 6 is repaired or dropped.

### 5. API

`Api/UnitReportEndpoints.cs`:

- `GET  /api/steward/plan/units/{unit}/report?project=&plan=`: the latest report + staleness +
  the server-rendered sections (release notes, journey, cost) + the pool as `LightboxShot`s.
- `GET  /api/steward/plan/units/{unit}/reports?project=&plan=`: history.
- `POST /api/steward/plan/units/{unit}/report?project=&plan=`: enqueue (manual).
- `GET  /api/unit-reports/{id}/shots/{n}`, `GET /api/unit-reports/{id}/diff?path=`
- walkthrough: the existing routes, keyed by walkthrough id, or a `.../report/walkthrough` twin.

None of these is a steward tool. The report is the owner's reading, not the steward's input; keep
it off `/mcp/steward` so nothing it says can become a reason for a merge.

### 6. The page

In `StewardPlan.tsx`, `UnitView` gets a third view beside *Outcomes* and *The spec*: **Report**,
shown once the unit has a done outcome. Same `orbit-swap` slot, same `Chip`.

- `UnitReport.tsx`: a staleness line ("written at abc1234; base has moved 3 commits, 1 of them this
  unit's" / "ticket #N has changed since"), the generate button with queue progress (reuse
  `GenerateBar`'s pieces), `WalkthroughCard`, then `AnalysisDoc` with `shots` = the pool, then the
  two server-rendered sections (release notes reuse the Steward record's note rows; journey and
  bounces reuse `Journey` / `Bounces` from `Classroom.tsx`, exported).
- The coverage map leads, as it already does in `AnalysisDoc`; its rows link each outcome's tickets
  to the board drawer.
- `Yours` (the Release step) shows "Read the report first" with a link to the view when the unit is
  built and a current report exists. A nudge, not a lock: release stays a REST route the owner calls.
- Motion and theme by name only: `orbit-swap` on the view, `orbit-pick` on ticket links,
  `orbit-tap` on chips, `tierToken` for verdict colours. Check at 390px.

Verify against the Vite dev server on 5173, not by restarting 5338.

### 7. Written when the unit is built

When `steward_set_outcome` (or the owner) moves the last outcome of an agreed unit to Done, enqueue
the unit report, so it is waiting by the time the owner opens the Steward page. Debounced like the
steward's own trigger: at most one per unit per five minutes, and not when a current report exists.
Also when a ticket of a built unit merges again (a late fix): the old report goes stale and a new one
is queued.

## Not in v1

- Re-shooting screens on the built base (the unit tour). The pool records where each shot came from,
  so the tour only has to replace it.
- A plan-level report across units.
- Exporting or sharing the report outside the board.

## Before each commit

```bash
dotnet build -c Release
```

```bash
dotnet test Whetstone.Tests -c Release --filter "FullyQualifiedName~Steward|FullyQualifiedName~Analysis|FullyQualifiedName~Walkthrough|FullyQualifiedName~UnitReport"
```

```bash
git grep -niE '<each project key and display name on this machine>' -- . ':!wwwroot'
```
