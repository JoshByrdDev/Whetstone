# Recon: where to point the next scout

Implementation plan. A tab for the before and after of scouting on the Discovery track
(`docs/discovery-track-plan.md`). The name is the tab's alone: **Scout** is the phase that drives
the browser, **Recon** is the page that says where it has been and where it should go next. The two
are never called by each other's name.

Read the discovery plan first - especially "Set up for what comes next: the Recon tab" and "What is
measured". This plan builds on what step 1 of that plan records, and adds only what Recon cannot
work without.

## What it is for

**Choosing the next direction.** A pilot on Opus 5.5 found that the wording of a direction
steers what a scout finds more than anything else the owner controls, so the choice of what to
scout next is a lever, and Recon's job is to pull it well. Everything on the page is there as a
reason for a suggestion: coverage says where nobody has looked, staleness says what has changed
since someone did, yield says where looking has paid. None of it is a dashboard for its own sake.
Cost and trend reporting by model, skill version and A/B arm belong to step 5's Metrics section.

It has to be useful from the first day, when there are no runs at all. It is: with areas and roles
configured and nothing scouted, every cell is a gap, and the suggestions say where to start.

## Decided with the owner

| Question | Decision |
|---|---|
| What it is for, first | Pointing the next scout. Coverage, staleness and yield are shown as the reasons for each suggestion |
| The unit of the map | **Area × role × width.** Areas from `discovery.areas`, roles from a new `discovery.roles`, width in two buckets. Route patterns are the drill-down inside an area |
| Which area a visit is in | Worked out by the board from the visit's URL against each area's `pages` patterns, at refresh time - not the scout's guess. History re-maps when areas are redefined |
| Staleness | **The area's code changed since it was scouted**: commits on the ticket's base ref after the last-scouted commit that touch the area's `code` paths, with the Delivery tickets they belong to. An area nothing has touched stays fresh however old its visit is |
| Where the numbers come from | **New marts, built first.** `refresh.py` loads the Discovery tables, the areas and roles, and each project's base history; dbt does the matching, staleness, yield and ranking under test. The board stays a reader that filters and never aggregates, as it is for Metrics |
| What "Scout this" does | Opens the existing new-ticket form on the Discovery track with the direction filled in. The owner edits the wording and files. The ticket records which suggestion it came from |
| Who writes a suggestion's words | **Reuse, else a stub.** A direction that paid off in this area is offered again word for word. A gap with no past direction gets a stub naming the gap, for the owner to rewrite as a job. No agent |
| A rerun copies | The direction only, linked to the discovery it came from. Chart writes a fresh route against the product as it is now. Replaying the same route is Recheck's job |
| Where it lives | A top-level **Recon** tab beside Steward, shown only when some project has a `discovery` block, with a project picker. Coverage does not add up across projects |
| Roles | `discovery.roles` in the project's config. Chart picks from it; visits match it case-insensitively; anything unmatched is shown as *other* so the list can be fixed |
| Widths | Two buckets, **phone** and **desk**, split at 640 - the board's own `NARROW_PX` |
| Ranking | Plain rules in a fixed order, each item showing why it is where it is (below). No weighted score |
| Setting one aside | **Not now** on a suggestion hides it until its area's code changes again, or until a date. An owner action: a REST route with no tool behind it, recorded as an event |
| In v1 | Suggestions, the coverage grid with its area drill-down, yield by area and by direction |
| Later | Before/after of an area; cold showcase cases; yield cut by model, skill version and arm |

## The page

Three parts, top to bottom, for one project at a time.

### 1. Scout next

A ranked list of suggestions. Each row has:

- its **kind**: *rerun* (a direction that paid off, in an area that has changed since), *gap* (an
  area × role × width cell nobody has scouted), or *changed* (an area scouted before, changed since,
  with no direction worth rerunning);
- **the direction**, either the past one's words or the gap stub;
- **the reasons**, as short facts rather than a score: "scouted 3 Oct on a1b2c3d", "14 commits, 9
  files in its code since, from 3 tickets", "last run: 2 approved, 1 new to me", "never as admin on
  a phone";
- **Scout this**, which opens the new-ticket form (below), and **Not now**.

**The order is these rules, in this order**, computed in dbt (`mart_recon_suggestions.rank`), with
each row's `reasons` saying which rule put it there:

1. *Rerun*: an area whose code has changed since it was scouted, which has a past direction with at
   least one approved proposal. The direction offered is the best of them by approved count, then
   new-to-me count, then the most recent.
2. *Gap* in an area whose code has changed in the last 30 days (by commit date on the base).
3. *Gap* anywhere else.
4. *Changed*: an area scouted before, changed since, whose past directions yielded nothing approved.

Inside each rule: more files changed first, then the cell scouted longest ago (never before ever).
**Cooling**: an area whose last three discoveries had no approved proposal sinks to the bottom
of whatever rule it is under and is marked *cooling*, so the page says out loud that scouting there
has stopped paying - the "Repeats" signal from the discovery plan's measures table, in the place it
is acted on.

Rows are collapsed per area: one area's six empty cells are one *gap* row listing the six, not six
rows. Expanding it lists the cells.

### 2. Coverage

A grid: a row per area, a column per role × width (signed out on a phone, signed out at a desk, a
member on a phone, ...). Each cell shows when it was last scouted, whether its area has changed
since, and its yield (approved / shown). An empty cell is a gap. Areas with no `code` paths show
coverage but no staleness, and say so once in the row. An *(unmapped)* row collects visits whose
URL matches no area's patterns, and an *other* column collects roles that match none of
`discovery.roles`. Both are there so the config can be fixed, not as coverage.

Picking an area opens its drill-down in a side column (`orbit-flank`): the route patterns visited
in it, each by role × width with the last visit and total seconds; the discoveries that went
there, each with its direction and yield; and the commits and tickets since it was last scouted.

### 3. Yield

Two small tables. By **area**: discoveries, proposals shown, approved, rejected by reason, new to
me, filed, and cost (the discoveries' runs, from `fct_runs`). By **direction**: the same, per
direction *lineage* - a direction and every rerun of it (`Ticket.ReconFrom` = `rerun:<id>`) count
together, so a direction's record carries across its reruns. A direction row has **Scout again**,
which is a rerun suggestion by hand.

## Scout this, and where a ticket came from

"Scout this" opens `CreateTicketModal` on the Discovery track with the direction in the
description, the suggestion's area, role and width in a one-line note under it ("From Recon: a gap
in Billing as admin on a phone"), and the suggestion's **key** carried along. The owner edits and
files through the existing create route. Nothing new can file a discovery.

The key is stored on the ticket as **`Ticket.ReconFrom`** (nullable string), set only by the REST
create route; no MCP tool takes it or shows it. Keys are stable strings:

| Kind | Key |
|---|---|
| Gap | `gap:<area>:<role>:<width>` (a collapsed row's key is `gap:<area>`) |
| Changed | `changed:<area>` |
| Rerun | `rerun:<discovery ticket id>` |

It does two things. The reader hides a suggestion while an open (not Done) discovery carries its
key, which is a filter on the live tickets table, so filing takes the row off the page at once
rather than at the next refresh. And yield follows it: a rerun's results count toward the direction
it reran.

## Not now

`POST /api/recon/{project}/dismiss` with `{ key, until? }` and `POST .../undismiss` with `{ key }`
- REST, no tool behind them, like the verdict. Each writes a row to **`ReconDismissalEvents`**
(`ProjectKey`, `SuggestionKey`, `Kind` Dismissed / Undone, `AreaHeadCommit`, `Until`, `At`), append
only. The `AreaHeadCommit` is the latest commit that touched the area's code when the owner pressed
it, which each suggestion row carries (`area_head_commit`).

The reader drops a suggestion whose latest event is *Dismissed* while its `area_head_commit` is still
the one recorded and `Until` (if set) is in the future. That is filtering, not aggregating, so it
belongs in the reader and takes effect without a refresh. A set-aside list at the foot of the
section shows what is hidden and offers undo.

## What the record needs that step 1 does not have

Small, and all of it in step R1 below:

- **`discovery.roles`** in `DiscoveryConfig`: an array of role names (`"signed out"`, `"member"`,
  `"admin"` - the project's own words). `get_discovery` returns the project's roles and area names,
  so Chart picks `SignedInAs` from the list and the scout logs the same word.
- **Chart and scout skills**: Chart's `signedInAs` is one of the project's roles; the scout's
  `signedInAs` on `log_visit` and `log_observation` is the route's. A **pipeline rule**: it goes into
  the templates and every project's copy word for word (CLAUDE.md, "A skill exists more than once").
- **`Ticket.ReconFrom`**, and `ReconDismissalEvents`, with their migration.
- Nothing else. Area and route pattern are derived from the URL at refresh time; the scout's own
  `Area` and `RoutePattern` stay recorded and are used only where the URL matches no area.

## The marts

### Loading

`refresh.py` adds to `BOARD_TABLES`: `DiscoveryRoutes`, `DiscoveryVisits`, `DiscoveryObservations`,
`DiscoveryProposals`, `DiscoveryVerdictEvents`, `ReconDismissalEvents` (the last for history only;
the reader applies dismissals live), and `Tickets.ReconFrom`. A table the database does not have yet
loads empty, as the loader already does.

Two inputs it cannot read for itself, because the rules for them live in C#:

- **Each project's areas and roles**, from the merged project config.
- **Each project's base ref and repos.** Which ref is "the base" is written twice already
  (`Resolve-BaseRef`, `BaseRefAsync`), and is per ticket on a project with slice branches or a
  ticket base of its own (CLAUDE.md, "The base is not always origin's"). Recon's staleness is
  measured against the **project's** base, the ref the product is shown from, and it must not
  become a third copy of that rule.

So `AnalyticsService` writes **`<data root>/analytics/recon-inputs.json`** before it starts a refresh:
per project with a `discovery` block, its key, its areas (name, pages, code), roles, and per repo
the folder and the resolved base ref (asked of `TicketGitService.BaseRefAsync` the way the board
already does). `refresh.py` reads that file. It does not open `project.json` itself. A refresh
started by hand without the file loads no Recon inputs, logs that, and the Recon marts build empty.

From that file, `refresh.py` loads per project and repo:

- **`raw.recon_commits`**: `git log --first-parent --name-only` on the base ref, from the oldest
  commit any visit recorded (or 90 days back when there are none) to the tip, one row per commit
  and file, with each commit's first-parent position so that "after" is ancestry, not clock time.
  A visit's commit that is not on the base's first-parent line is placed by
  `git merge-base --is-ancestor`, and if it is not an ancestor at all (a rewritten base), staleness
  for that visit is *unknown* and is shown as such, never as fresh.
- **`raw.recon_areas`** and **`raw.recon_roles`**, as given.

On a project of several repos an area's `code` path starts with its repo's folder
(`alpha-api/src/Billing/**`), the rule for any path that stands on its own; commits are loaded per
repo and matched to the code paths of that repo.

### Models

| Model | Grain | What it says |
|---|---|---|
| `stg_board__discovery_*` | one per table | The Discovery tables, typed. Step 5's funnel builds on these too, so they are written once, here |
| `int_recon_visit_area` | visit | The visit's area (most specific `pages` match on the URL's path; the scout's own `Area` when none matches; *(unmapped)* when neither), route pattern (the matching pattern, else the scout's), role (matched to `discovery.roles`, else *other*), width bucket |
| `fct_discovery_proposals` | proposal | Its discovery, area (from its observations' URLs, majority; else its own `Area` matched to an area name), latest verdict from the event log, new to me, filed ticket, the discovery's direction lineage. Shared with step 5 |
| `mart_recon_coverage` | project × area × role × width | Last scouted at and on which commit, discoveries, visits, seconds, commits and files changed in the area's code since, the tickets those commits belong to (`stg_board__ticket_commits`), approved / shown |
| `mart_recon_patterns` | project × area × route pattern × role × width | The drill-down: visits, seconds, last at |
| `mart_recon_yield` | project × area, and project × direction lineage, per `window_days` | Discoveries, shown, approved, rejected by reason, new to me, filed, cost |
| `mart_recon_suggestions` | project × suggestion key | Kind, area, the cells it covers, direction text, source discovery for a rerun, `rank`, `rule`, `reasons` (a JSON list of short facts), `cooling`, `area_head_commit` |

**Scoping.** Recon is per project and never shows "all projects", so the coverage, patterns and
suggestions marts carry `project_key` and are not run through `scoped()`: coverage is all of
history by definition. `mart_recon_yield` is cut by `window_days` with the same `scoped()` macro as
every other mart, keeping only its own project rows and the Discovery track.

**The width split is written twice and the two must agree**: 640 in the dbt model and `NARROW_PX`
in `ClientApp/src/hooks/useNarrow.ts`. Each names the other in a comment.

**Published**: the new marts go into `PUBLISHED` in `refresh.py` and `AnalyticsService.Marts`,
which are already a pair that must agree.

**Tests** (dbt, in `analytics/tests`): a URL matched by two areas' patterns lands in the more
specific one; a visit on a commit off the base's line is *unknown*, not fresh; an area with no code
paths is never stale and never a *changed* suggestion; a gap disappears from the suggestions when a
visit covers it; the rule order holds; cooling needs three discoveries, not three visits. Fixtures
use `alpha` and `bravo` as project keys and placeholder routes.

**Freshness**, as on Metrics: opening the tab refreshes marts older than `StaleAfter` (15 minutes),
the page shows when they were built, and there is the same Refresh. A discovery reaching
`NeedsVerdict` is what usually makes the page out of date; marking the marts stale then is a
refinement for later, not v1.

## Routes

All reads go through `AnalyticsService`, which filters and sorts and never aggregates:

| Route | Reads |
|---|---|
| `GET /api/recon/projects` | Projects with a `discovery` block, and whether each has areas and roles. Shows or hides the tab |
| `GET /api/recon/{project}/suggestions` | `mart_recon_suggestions`, minus those with an open discovery carrying their key and those set aside (live tables) |
| `GET /api/recon/{project}/coverage` | `mart_recon_coverage` |
| `GET /api/recon/{project}/areas/{area}` | `mart_recon_patterns`, the area's discoveries and its commits since |
| `GET /api/recon/{project}/yield?days=` | `mart_recon_yield` |
| `POST /api/recon/{project}/dismiss`, `.../undismiss` | Writes `ReconDismissalEvents`. No tool |

**No MCP tool reads or writes any of it** in v1. A worker has no use for it, and the steward
takes no part in Discovery.

**QA isolation**: none of these reach past the data folder - they read the copy's own marts and
database and write one local table - so none belongs in `QaIsolation`'s table. The refresh's git
reads on a QA copy are of its placeholder projects. Say so in the routes' comment, so the next
person does not wonder.

## Look and phone

Built from the system (CLAUDE.md, "The look is a system"); nothing picks its own colour or timing.

- Suggestion rows and area rows are `orbit-pick`, with the open area `aria-current="true"`; *Not
  now*, *Scout again* and the cell chips are `orbit-tap`; *Scout this* is the page's one
  `orbit-primary`. The area drill-down is an `orbit-flank` grid. Switching project or yield window
  is `orbit-swap` keyed on it. The new-ticket form is already an `orbit-veil`.
- Cell states (gap, fresh, changed, unknown, cooling) are named tokens in `src/theme/`, not
  literal colours, and each one also has a word or a mark, not colour alone.
- **On a phone** (below `NARROW_PX`): Scout next first and full width; the grid folds to one card
  per area with its role × width cells as a wrap of chips; the drill-down replaces the list rather
  than flanking it; yield's tables become one card per row. Nothing appears only on hover - a
  reason that would be a tooltip at a desk is written on the row. Check at 390px with
  `document.documentElement.scrollWidth` equal to the viewport's.

## Empty and broken states, said out loud

| State | The page says |
|---|---|
| No project has a `discovery` block | The tab is not shown |
| A `discovery` block with no `areas` | "Name this product's areas first", with the shape of an area (name, pages, code), and no grid |
| Areas but no `roles` | The grid has one *any role* column, with a line saying gaps by role need `discovery.roles` |
| Analytics cannot run here (no uv) | The analytics status's own reason, as on Metrics, linking the Setup check |
| Marts built before the inputs file existed | "Recon has not been built yet - Refresh" |
| Nothing scouted yet | Every cell a gap; the suggestions are gaps, in rule order |

No new dependency: Recon needs what Metrics needs, which `docs/setup.md` and the Setup check
already list.

## Build order

1. **R1 - the record.** `discovery.roles` in `DiscoveryConfig`; `get_discovery` returns roles and
   area names; the Chart and Scout skill rule, in the templates and every project's copy;
   `Ticket.ReconFrom` and `ReconDismissalEvents` with their migration; `ReconFrom` on the create
   route only. Tests: `ReconFrom` cannot be set through MCP; roles parse.
2. **R2 - the marts.** `recon-inputs.json` from `AnalyticsService`; the loaders; the staging,
   intermediate and mart models above with their dbt tests; published.
3. **R3 - the routes.** Readers, the live filters (open discovery, dismissals), the dismiss routes.
   C# tests under `Recon` (`dotnet test Whetstone.Tests -c Release --filter "FullyQualifiedName~Recon"`).
4. **R4 - the tab.** Picker, Scout next, coverage and drill-down, yield, the prefilled form, empty
   states, phone layout. Rebuild and commit `wwwroot`. Verified on the QA copy with a placeholder
   project given a `discovery` block, areas, roles and a few seeded visits - never against 5338.

Recon does not wait for the discovery plan's steps 2 to 5. It does take two pieces of step 5 early
(the Discovery staging models and `fct_discovery_proposals`), and step 5 builds on those rather than
beside them.

## Later

- **Before and after of an area**: its timeline of discoveries, proposals filed, tickets done, and
  shots then and now. Bounded by retention: past Complete only approved proposals keep clips and
  shots, for 90 days, and a rejected one keeps one still. Recheck (discovery step 2) is what gives
  an "after".
- **Cold showcase cases**: `discovery.casesIndex`, a path in the project to a JSON list of case ids
  with their areas, loaded by `refresh.py`, so cases no run has touched show in the drill-down and
  become a kind of suggestion.
- **Yield cut by model, effort, skill version and A/B arm**, once step 5's marts carry them.
- **Snoozed proposals that have come due**, offered as a suggestion in their area.
- ~~Agent-drafted directions~~ for gaps, written as jobs: built, as an experiment, before the
  stub-and-rewrite had shown anything. See "Drafted directions".
- **Marking the marts stale** when a discovery reaches `NeedsVerdict`.

## R1 and R2 as built

R1 and R2 are built (the record, the marts). R3 and R4 are built too: see "R3 and R4 as built"
below. What R3 and R4 relied on:

### The record (R1)

- **`DiscoveryConfig.Roles`**: trimmed, blanks dropped, de-duplicated without regard to case, in
  config order. **`DiscoveryConfig.Present`** is true when the project has a `discovery` block at
  all - the test for "show the tab" and for "is this project in `recon-inputs.json`".
- **`get_discovery`** returns `project = { areas, roles }`: area *names* only, never their page or
  code patterns, which are for the marts and not for a worker to second-guess.
- **The skill rule** is in the chart, scout and recheck templates and every project's copy:
  `signedInAs` is one of `project.roles`, word for word, and the scout's `log_visit` passes its
  width. Recheck got it too, because it logs visits the same way. Where a project has no roles
  the text says to describe the session in a few words, as before.
- **`Ticket.ReconFrom`** (max 256), set by `POST /api/tickets` only (`CreateTicketRequest.ReconFrom`;
  `UpdateTicketRequest` has none, no MCP tool or ticket DTO carries it - `ReconTests` checks by
  reflection). The route refuses it on anything but the Discovery track, refuses a key whose shape
  is wrong, and refuses a `rerun:<id>` whose id is not a discovery of the same project.
  `Services/ReconKey.cs` is the one place the shape is written: `gap:<area>`,
  `gap:<area>:<role>:<width>`, `changed:<area>`, `rerun:<discovery id>`; `ReconKey.Clean`,
  `Problem` and `RerunOf` are what R3's dismiss routes should use too.
- **`ReconDismissalEvents`** (`Models/ReconEntities.cs`): `ProjectKey`, `SuggestionKey`, `Kind`
  (`Dismissed` / `Undone`, stored as text), `AreaHeadCommit` (the area's head when set aside, so
  R3 can bring a dismissed `changed:` back when the area moves again), `Until` (nullable), `At`.
  Indexed on project and key. Nothing writes it yet: the dismiss and undismiss routes are R3's,
  and like every owner action they get no tool.

### The inputs and the loaders (R2)

- `AnalyticsService.StartRefreshAsync` writes `<data root>/analytics/recon-inputs.json`
  (`ReconInputs`) before every refresh it starts, then starts the refresh as before. The file covers
  enabled projects with a `discovery` block: key, areas (name, pages, code), roles, and per repo
  its `dir` (empty on a project of one), hub `path`, `readOnly`, and the project's base ref,
  resolved by `TicketGitService.ResolveBaseRefAsync` - the same `BaseRefAsync` the board uses, so
  the rule is not written a third time. A repo whose ref cannot be resolved gets `baseRef: null`
  and loads no history. If the write fails, the file is deleted rather than left stale, and the
  refresh goes on with the Recon marts empty.
- `refresh.py` loads `raw.recon_*` from it: projects, areas, roles, repos, each base's
  first-parent history (`fp_index` 0 is the tip; back to the oldest commit a visit recorded, or 90
  days), the files each first-parent commit touched (`--diff-merges=first-parent`, so a merge
  lists what it brought in), the commits each merge brought in (`recon_commit_members`, at most 500
  merges a repo), and where each recorded visit commit sits (`recon_visit_commits.placement`:
  `line`, `merged` - reached the line through a merge, placed at that merge - or `unknown`).
  `ReconDismissalEvents` is loaded for history only. No inputs file: the Recon raw tables load
  empty, and `status["recon"]` says so.
- On a QA copy the git reads are of the copy's placeholder repos, and nothing reaches past the data
  folder, so no route went into `QaIsolation`.

### Settled syntax

Written beside `DiscoveryArea` in `Services/DiscoveryConfig.cs`, and implemented once, in
`analytics/macros/recon.sql`:

- **`pages`**: a path pattern, matched against the URL's path only (no host, query or fragment),
  case-insensitively, ignoring a trailing slash. `*` and `[name]` are exactly one segment; `**` is
  any number of segments, none included (`/events/**` matches `/events`). Anything else is literal.
  When several areas match, the **most specific** wins: more literal segments, then fewer `**`,
  then more segments, then the area that comes first in config.
- **`code`**: git pathspec globs relative to the repo, `**` across folders, a bare folder meaning
  everything under it. On a project of several repos a path starts with its repo's folder.
- **Width**: a `WxH` width under 640 is `phone`, otherwise `desk`; anything unparseable is
  `unknown`. 640 is `NARROW_PX`, and each names the other; `ReconTests` checks they agree.

### The marts

All five carry `project_key` and are published (`PUBLISHED` in `refresh.py`, `AnalyticsService.Marts`).
Lists inside a row are JSON text.

| Mart | Key | Columns R3 and R4 will want |
|---|---|---|
| `mart_recon_projects` | `project_key` | `project_ordinal`, `area_count`, `role_count`, `inputs_at`, `repos` (JSON per repo: `repo`, `base_ref`, `tip`, `commits`, `error`), `history_ok`, `built_at`. A project absent here but present in config is "Recon has not been built yet" |
| `mart_recon_coverage` | `project_key, area, role, width` | `*_ordinal` for sorting, `in_config` (a configured role and a phone or desk width; false for a role or width only the visits used), `has_code`, `visits`, `seconds`, `discoveries`, `last_visited_at`, `last_discovery_id`, `last_commit`, `last_placement`, `state` (`gap` / `fresh` / `changed` / `unknown`), `commits_since`, `files_since`, `tickets_since` (JSON ids), `shown`, `approved`, `area_head_commit`, `cooling` |
| `mart_recon_patterns` | `project_key, area, route_pattern, role, width` | `visits`, `seconds`, `discoveries`, `last_visited_at` |
| `mart_recon_yield` | `window_days, project_key, cut, cut_key` | `cut` is `area` or `direction` (`cut_key` the lineage's first discovery id), `label`, `discoveries`, `shown`, `approved`, `rejected` and by reason, `snoozed`, `new_to_me`, `filed`, `cost_usd`, `cost_complete`, `latest_discovery_id`, `latest_direction`, `latest_at` |
| `mart_recon_suggestions` | `project_key, suggestion_key` | `kind` (`rerun` / `gap` / `changed`), `rule` (1-4), `rank`, `area`, `area_ordinal`, `cells` (JSON of `{role, width}`), `direction` (the stub, or the source's direction for a rerun), `source_discovery_id`, `source_lineage_id`, `reasons` (JSON list of short sentences), `cooling`, `area_head_commit`, `files_changed`, `commits_changed`, `tickets` (JSON ids), `last_scouted_at`, `last_commit`, `approved`, `new_to_me` |

The suggestions mart applies **no live filter**: R3 removes suggestions with an open discovery
carrying their `ReconFrom` and those whose latest `ReconDismissalEvents` row is `Dismissed` (and,
for `changed:`, whose `AreaHeadCommit` is still the mart's).

`fct_discovery_proposals.area` is replaced in place: the majority area of the proposal's
observations' URLs, else its own `Area` when that names a configured area, else *(unmapped)*;
`area_source` says which (`url`, `sift`, `none`; `own` on a project without areas, whose
proposals keep the area Sift wrote).

### Choices made where the plan left room

- **The scout's own `Area`** is used for a visit only when its URL matches no area *and* that Area
  is one of the configured names; otherwise the visit is *(unmapped)*. A free-text area would make
  a column nobody configured.
- **A role** not in `discovery.roles` is matched case-insensitively, else *other*; a project with no
  roles has one *any role* column.
- **Unknown is never fresh**: a visit commit that is not on the base's line and not reached by a
  merge, or a repo whose base did not load, is `unknown`. A `merged` placement counts the merge
  itself as a change after the visit - it is the first point on the line that contains the visit's
  commit, so what else it brought in is after.
- **Several repos**: a visit's staleness counts only its own repo's commits (the area's code
  paths that start with that repo's folder).
- **Tickets since** are matched by commit SHA, directly or as a commit a merge brought in
  (`stg_board__ticket_commits`). Not by patch id: a squash merge on origin is not on a
  `mergeInsteadOfPr` project's line, and on a pull-request project the board records the PR's
  commits.
- **Cooling** is the last three *judged* discoveries in the area (Done, or with any closed verdict) having no approval - not three visits, and not discoveries still running.
- **The gap stub is per area**, naming the missing cells and collapsing a whole role or a whole
  width: "Nobody has used Billing as an admin, or as anyone on a phone." An area visited only
  outside its configured cells says "Nobody has used X as r1 or r2."
- **A never-scouted area** is ranked among gaps by the files its code changed in the last 90 days.
- **`mart_recon_projects`** was added: the plan's empty states need to tell "not built yet" from
  "built, nothing scouted", and only the mart can say when it was built from which inputs.
- **The tests** are dbt unit tests in the `_recon.yml` files rather than `analytics/tests`: they
  are fixtures with expected rows, which is what the unit-test form is for. They cover the plan's
  list (most specific pattern, off-line commit is unknown, no code is never changed, a gap goes
  when covered, rule order, cooling needs discoveries) plus yield following a direction across
  reruns.

`wwwroot` is untouched: R2 changed one comment in `useNarrow.ts` and nothing a build would ship.

## R3 and R4 as built

All four steps are built. What the routes and the tab do, and the choices made where the plan
left room:

### The routes (R3)

- **`ReconService`** (`Services/ReconService.cs`) is the reader and the one write;
  `Api/ReconEndpoints.cs` maps it. It reads the marts through `AnalyticsService.ProjectMart`, a
  per-project reader for marts that carry `project_key` and are not cut by `scoped()`, and parses the
  JSON list columns (`cells`, `reasons`, `tickets`, `ticketsSince`, `repos`) so the page reads a list
  as a list. It filters and sorts; it never counts.
- **`GET /api/recon/projects`**: enabled projects with a `discovery` block, each with its configured
  areas (name, pages, code) and roles, whether the marts were built for it (`built`, from
  `mart_recon_projects`), what they were built from (`builtAreas`, `builtRoles`, `builtAt`,
  `historyOk`, `repos`), and **`cannotFile`**: why Scout this cannot file a discovery there -
  Discovery not in the project's `tracks`, or no `siteUrl` - the create route's own refusals, said
  before the form is filled in rather than after. It also returns the analytics status, so the page
  needs one call to decide everything above its sections. An empty list hides the tab.
- **`GET /api/recon/{project}/suggestions`** returns `{ suggestions, setAside, inFlight }`. The live
  filters, in `ReconService.Filter`:
  - **An open discovery** (Discovery track, not Done, same project) whose `ReconFrom` is a row's key
    takes the row off and lists it under `inFlight`. A discovery filed from **one cell** of a
    collapsed gap row (`gap:<area>:<role>:<width>`) takes that cell out of the row's `cells`; the
    row goes when none is left.
  - **Not now**: a row is set aside while its latest `ReconDismissalEvents` row is `Dismissed`, the
    `Until` (if any) is still ahead, and the `AreaHeadCommit` recorded is still the mart's
    `area_head_commit`. That is applied to **every kind of key**, not only `changed:` - the plan's
    "until its area's code changes again" is the rule for all of them. An area with no code paths
    has no head (null on both sides), so its set-aside lasts until its date or an Undo.
  - The mart's `rank` is kept on each row; the page numbers rows by their position instead, because
    the filters leave holes in the ranks.
- **`GET /api/recon/{project}/coverage`**, **`/areas/{area}`**, **`/yield?days=`**. The drill-down
  returns the area's config, its coverage cells, `mart_recon_patterns`, and two marts added for it
  (below). Yield takes 7, 30, 90 or 0 and **defaults to 0, all of history**: a project starts with
  a handful of discoveries, and a 30-day window would show nothing for most of its first months.
- **`POST /api/recon/{project}/dismiss`** `{ key, until? }` and **`/undismiss`** `{ key }`, REST only.
  Dismiss refuses a key that is not one of the project's suggestions as last built (the event has
  to record that row's area head), a malformed key (`ReconKey.Problem`) and a date that is not in
  the future. Undismiss refuses a key that is not set aside. A project without a `discovery` block,
  or not enabled, is a 404 on every route.
- **No MCP tool** reaches `ReconService` or `ReconDismissalEvent`; `ReconRoutesTests` checks by
  reflection. **QA isolation**: nothing here reaches past the data folder, so nothing went into
  `QaIsolation`'s table; the routes' comment says so.
- **Two marts were added**, because the drill-down's lists are not in the five R2 published and the
  board does not aggregate: `mart_recon_area_discoveries` (every discovery that went to an area, by a
  visit or a shown proposal, with its direction and its yield there) and `mart_recon_area_commits`
  (commits on the base touching the area's code since it was last scouted, one row per commit, with
  `since` = `after` - the coverage's own ancestry rule - or `recent` for a never-scouted area's last
  90 days, or `by_date` when the last visit is off the base). Both published; a dbt unit test pins
  which commits are listed.

### The tab (R4)

`ClientApp/src/components/recon/`: `ReconPanel` (picker, freshness, states, the form, the drawer),
`ScoutNext`, `Coverage` (grid, phone cards, drill-down), `Yield`; cell states are named in
`src/theme/recon.ts`.

- **The tab** sits between Steward and Classroom and shows only when `/api/recon/projects` is not
  empty, asked once when the app loads - a block added later shows the tab on the next reload, as a
  new project does. The picker remembers its project per machine, as the other tabs' do.
- **Freshness** is the Metrics page's: on arrival a refresh starts if the marts are stale or
  missing, the status is polled only while one runs, and the page reads everything again when it
  ends. The heading says when Recon was built (`mart_recon_projects.built_at`).
- **Scout next**: one row per suggestion with its kind (a mark and a word), area, cooling when it is,
  the rule that put it there in words, the direction, and the reasons as small wells. A row opens to
  list a gap's cells, each with **Scout just this** (files `gap:<area>:<role>:<width>`), the tickets
  that changed the area, the discovery a rerun reruns, and a link into its area's drill-down.
  **Scout this is `orbit-primary` on the first row only**; on the others it is a ghost button in the
  done tier's colour - a page where every row wore the accent would have no primary action. **Not
  now** swaps the row's buttons (`orbit-swap`) for *Until its code changes* (or *Until I undo it*
  for an area without code), *A week*, *A month*, *Keep it*. Below the list: what is being scouted
  now and so not suggested (each opening its ticket), and the set-aside list with **Undo**.
- **Scout this** opens `CreateTicketModal` with a new `prefill` prop: the project, the Discovery
  track, a title ("Scout Billing", "Scout Events again", "Scout what changed in Search"), the
  direction, a one-line note under it ("From Recon: a gap in Billing - as admin on a phone"), and
  the key. The key is sent as `reconFrom` only while the form is still on that project and the
  Discovery track. Filing re-reads the page, so the row leaves at once, and opens the new ticket in
  the board's drawer. A cell's stub direction ("Use Billing as admin on a phone: who are you, and
  what did you come to do?") is a question for the owner to answer as a job, not a direction.
- **Coverage** at a desk is a table inside its own horizontal scroller (many roles scroll the
  table, never the page): config columns first in config order, then the `other` / unknown-width
  columns marked *not in config*; `(unmapped)` last, saying it is there to fix the patterns. Area
  names are `orbit-pick` with `aria-current`; cells are `orbit-tap` and open their area. The
  drill-down is an `orbit-flank` grid (`minmax(0,1fr) 0rem` ↔ `minmax(0,1fr) 24rem`). On a phone each
  area is a card with its cells as a wrap of chips, and the drill-down replaces the grid with a
  Close. A legend under the grid says every state's mark and word.
- **Yield**: window chips (`orbit-swap` keyed on the window), then By area and By direction as
  tables at a desk and one card per row on a phone. Rejections are broken down by reason in words;
  cost is shown only where the mart says every discovery is priced, else *not priced*. A direction
  row has **Scout again** (`rerun:<its latest discovery>`).
- **States said out loud**: no `discovery` block anywhere (no tab); a block with no areas (the
  shape of an area, as JSON, and no grid); areas but no roles (the *any role* column and a line);
  analytics unavailable (the status's reason, pointing at the System page's Setup check); not built
  yet (Refresh); the areas or roles changed since the build (Refresh); a repo whose history did not
  load or whose base did not resolve; and a project where a discovery cannot be filed (Scout this
  and Scout again off, with the reason once above the list). A failed refresh says so and the page
  shows the build before it.
- **The look**: no literal colours, radii or transitions in the new files; corners are `chamfer()`,
  colours are tokens or the themed Tailwind classes, cell states borrow tiers through
  `RECON_STATE`, and each has a mark and a word. Nothing appears only on hover.

### Checked

- `dotnet test --filter Recon`: 34 (R1's tests plus `ReconRoutesTests`). dbt build 335/335 on a
  snapshot of this machine's board, with the two new marts' schema and unit tests.
- On the QA copy, alpha given four areas (one with no code paths), three roles, three commits around
  a visit, and two judged discoveries with six visits (one off the base, one unmapped, one as a role
  not in the list): the suggestions came out rerun, gap (rule 2) x2, gap (rule 3) x2, changed, in
  that order; Scout this filed #3 with `ReconFrom = gap:Events`, and the row left the page at once
  and showed under "being scouted"; Not now for a week and Undo round-tripped; the drill-down showed
  the patterns, discovery #1 and the one commit since. bravo was taken through no areas, not built,
  Refresh, built with no roles, and Scout this off for a missing track. Every state was checked at
  desktop width and at 390px, where `document.documentElement.scrollWidth` was 390.

### Left for later

- The plan's "Later" list stands. Two small additions found while building: Recon's tab appears
  only on a reload after the first `discovery` block is added; and the Not now choices are fixed
  (code change, a week, a month) rather than a date picker.

## What to look at first

1. **The tab on a real project**: give one project's `discovery` block its `areas` and `roles`, add
   `Discovery` to its `tracks` if it is not there, reload, open Recon. Does the first suggestion
   look like where you would have pointed a scout?
2. **The gap stubs.** ~~A gap's direction is the marts' stub, meant to be rewritten.~~ Answered on
   a real project: the stub went into the form as the direction, and for a wide area ("Nobody has
   used Portal yet.") it told Chart nothing - not who, not why. See "Drafted directions".
3. **Scout this on the first row only wears the accent.** If every row should look the same, it is
   one line in `ScoutNext.tsx`.
4. **Yield defaults to all of history**, not 30 days. Say if you would rather it matched Metrics.
5. **Not now applies the area-head rule to every key**, not just `changed:` (R2's notes read as if
   only `changed:` would come back on new code). A gap set aside comes back when its code moves too.


## Drafted directions

Built after R4, on what the first look at a real project showed: Scout this on a gap put the
marts' stub in the form as the direction, and a stub names a gap, not a person or a job. On a wide
area - one used by business owners and their staff alike - Chart was left to invent both, or to
block and ask. Four changes, in the order they were made.

### The stub is a description, not a direction

The gap row still reads "Nobody has used Billing yet, as member or admin." - a never-scouted area's
line now names the roles it is used as, where it used to say only "yet." But Scout this no longer
puts it in the form. A gap's or a changed area's direction starts **empty**, with the question as
its placeholder ("Nobody has used Billing as member or admin. Pick one person: who are they, and
what did they come to get done?"), so it cannot be filed as it came. A rerun's direction still goes
in word for word: it is a direction already.

### A wide gap leads with its cells

A gap row whose cells span more than one role lists them on the row itself, under **One at a
time**, each opening the form for that one cell (`gap:<area>:<role>:<width>`). The whole-area Scout
this stays; it is for when one person's job really does reach the whole area.

### An area says who it is for

`discovery.areas[]` takes two optional fields:

```json
{ "name": "Billing", "pages": ["/billing/**"], "code": ["src/billing"],
  "about": "Where a member pays and an admin chases what is owed.",
  "roles": ["member", "admin"] }
```

- **`roles`** are which of the project's `discovery.roles` use the area, matched without case and
  stored in the project's spelling; a name the project does not list is dropped, and none means
  every role. Coverage counts only those cells as the area's (`mart_recon_coverage`, via
  `recon_areas.roles`, which the board resolves - its own or the project's - in `ReconInputs`), so
  nobody is asked to scout an owner's portal signed out. A visit there as another role still shows,
  outside the config, so the list can be fixed.
- **`about`** is the owner's sentence on who comes to the area and why. People, not screens: a
  direction drafted from a list of features is written as a feature. `get_discovery` hands both to
  Chart as `project.areaGuide` (`{name, about, usedAs}`), the one thing about the product Chart
  reads - every copy of the chart skill says so. The drill-down shows them, and says when `about`
  is missing.

### Drafting

Under a gap's or a changed area's empty direction, **Draft directions** runs one agent
(`ReconDrafter`, on Chart's agent role) and lists two or three candidates: a person, signed in as
one of the area's roles, the job they came to do, phone or desk, and one line on what it would
find that nobody has looked for. **Use this** puts one in the field to edit; filing is the form's,
as always. On a whole-area gap, a candidate for one role at one width files under that cell's key,
so the area's other cells stay suggested.

- **What it is told** is what the board knows and nothing else: the area's name, pages, `about` and
  roles; its empty cells and why it is suggested; every past direction on the project with its
  yield (approved, new to me, rejected as generic, already decided or wrong); the directions being
  scouted now; the taste file. Never the code, specs or plans - Chart's rule, for Chart's reason -
  and it runs in an empty folder, so there is nothing to read.
- **What it answers is checked** (`ReconDrafter.Check`): a candidate signed in as a role the area
  does not list is dropped, as is one repeating a past or open direction; nothing left is refused.
  When it lacked something it says what in `question`, aimed at the area's `about`, and the form
  shows it.
- **Kept**, as `ReconDraft` (project, key, candidates, question, model, tokens, time), so reopening
  the form reads the last draft back rather than paying for another. `GET` and
  `POST /api/recon/{project}/drafts`; the POST is an agent run, so it is in `QaIsolation`'s table
  and refused again in the service. No MCP tool reads or writes a draft.

### Whether it pays

The plan held drafting back until the stub-and-rewrite had shown whether the owner's rewrites are
what makes a direction pay. It was built first instead, so it is measured instead. A discovery
records the draft it was picked from (`Ticket.ReconDraftId`) and where its direction came from
(`Ticket.DirectionSource`, worked out by the create route): `draft` (a candidate as it was, compared
without case, spacing or a closing stop), `draft-edited`, `rerun` (a past direction word for word)
or `owner` - which is also every discovery filed before this, or by hand. `mart_recon_yield` has a
third cut, `source`, and the Yield section shows it as **By who wrote the direction**. When drafted
directions are approved less than the owner's, the drafter is what to change, or to switch off;
when an edited draft pays and an unedited one does not, the edit is what to learn from.

### Checked

- `dotnet test --filter "Recon|QaIsolation"`: 134, `ReconDraftTests` among them. dbt build 337/337
  on a snapshot of this machine's board, with the new unit tests for an area's roles and the
  never-scouted stub. On that snapshot, an area given two of five roles became the first suggestion
  as "Nobody has used <area> yet, as <role> or <role>." with four cells, not ten.
