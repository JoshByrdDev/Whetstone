# Analytics: the marts behind the Metrics page

The Metrics page answers "where does a phase's time go, and did the last change to it help?" It
reads a set of marts built by dbt over DuckDB, from three sources the board already has. This is
the shape of that, and why.

```
board database (SQLite) ─┐
skill + prompt git log ──┼─► refresh.py ─► warehouse.duckdb ─► dbt build ─► marts/*.parquet ─► API ─► page
worker transcripts ──────┘   (raw schema)   (staging → intermediate → marts, tested)   (DuckDB.NET, read-only)
```

## Where things are

| What | Where |
|---|---|
| The dbt project, its loader and its Python pins | `analytics/` (`dbt_project.yml`, `refresh.py`, `pyproject.toml` + `uv.lock`) |
| The build database, the published marts, the last refresh's report | `<data root>/analytics/` - one machine's, gitignored |
| What reads them | `Services/AnalyticsService.cs`, routes under `/api/analytics/` |

One command refreshes everything:

```bash
uv run --project analytics python analytics/refresh.py
```

It snapshots the board database through SQLite's backup API (a read; the live file is never held),
reads each project's skill history and the worker prompt's history from git, loads the transcripts
that changed since last time, runs `dbt build`, and only if every model and test passed, swaps the
new parquet files in. A failed refresh leaves the published marts exactly as they were.
`--rebuild` forgets the incremental transcript load; `--no-transcripts` skips them.

## Decision: how the board reads the marts

**Chosen: dbt writes the marts as parquet; the API reads the parquet with DuckDB.NET; the API
starts the refresh as a child process.** All three of the options considered, in a particular
arrangement:

- **Not DuckDB.NET over the warehouse file.** DuckDB allows one writer and, on Windows, a reader
  holding the file blocks it. The board would either hold the lock dbt needs, or have to close and
  reopen around every refresh. Parquet files are opened for the length of one query, so the build
  and the board never contend. The refresh writes `marts.next/` and renames it over `marts/`.
- **Not JSON the API serves as-is.** The drill-down asks for one run's tool calls out of ~100k,
  and a page filter asks for one slice of each mart. A query engine over columnar files does both
  in milliseconds; a JSON file would be read whole every time.
- **The marts are pre-cut, so the API stays a reader.** Every aggregate mart carries three scope
  columns - `window_days` (7, 30, 90, 0 = all), `project_scope` (a key or `*`), `track_scope` (a
  track or `*`) - built by the `scoped()` macro. Every number the page shows is computed in dbt,
  under test; the API does `WHERE` and `ORDER BY`, never `GROUP BY`. Medians are why: they cannot
  be re-aggregated, so a mart that left the windowing to the API would have pushed the arithmetic
  out of the tested layer.

## Decision: when a refresh runs

**On demand, and when someone looks at stale numbers.** The Metrics page asks for the status when
it opens; if the marts are older than 15 minutes it starts a refresh and keeps showing the old ones
until the new ones land. There is also a Refresh button. Nothing runs on a timer: a refresh costs a
minute of CPU on the machine the fleet runs on, and numbers nobody is reading do not need to be
fresh. One refresh at a time (a lock file); a second request while one runs is told so.

A first refresh reads every transcript (a few minutes for ~5k sessions). After that only changed
files are read, and a refresh takes well under a minute, most of it dbt.

On an isolated QA copy (`WHETSTONE_QA_ISOLATION`) the refresh runs with `--no-transcripts`: the
transcripts are the live fleet's, under the user profile, which is outside the copy's folder.

## What the models do

**Staging** renames and types each source. Board timestamps carry seven fractional digits, which
`board_ts()` trims to DuckDB's six.

**Intermediate**

- `int_transcript_sessions` - one Claude session: its span, working directory, and the ticket and
  phase its worker prompt names (`**Ticket:** #123`, `**Current phase:** X`).
- `int_tool_calls` - one tool call, timed from the line that asked for it to the line its result came
  back on; with a family (`shell`, `mcp:playwright`, `files`...) and, for a shell call, the program
  and verb it ran (`dotnet build`, `until-loop`).
- `int_run_entry` - why the run happened and which time round it was. Recorded by the board since
  run tracking; for older runs, derived from the transition log the same way `RunEntryReasons.Of`
  and the `AddRunTracking` backfill do. The three must stay in step.
- `int_run_sessions` - which session a run was. By recorded `SessionId` where there is one;
  otherwise a session whose prompt names the run's ticket **and phase** and overlaps its time, or,
  failing that, one in the ticket's worktree that started when the run did. Both guards are there
  because of real mismatches: a run on a worker host leaves its transcript on that machine, so the
  nearest local session is the ticket's previous phase; and short promptless sessions run in the
  same worktree right after a run.
- `int_run_agent_time` - the session's span inside the run's window, and the union of its tool
  intervals (parallel calls overlap; summing them would claim more tool time than wall clock).
- `int_runs` - everything about a run, including the **time split**, whose parts add up to the
  run's duration (tested):

  | Part | Recorded runs (run tracking) | Older runs with a transcript | Neither |
  |---|---|---|---|
  | prep, startup, hand-off | measured by the worker | in `overhead`, unsplit | in `unmeasured` |
  | model / tools | agent time split by the transcript | session span split by the transcript | in `unmeasured` |

  A recorded run with no transcript (a worker host's) has its agent time as `agent_unsplit`.

**Versions.** A run recorded since run tracking carries `SkillHash` and `PromptHash`. For older
runs the loader reads the git history of each project's `skills/<phase>/skill.md` and of
`data/heartbeat-prompt.md`, hashes each version the way the worker does (`Get-TextHash` over the
text as `Get-SkillText` reads it), and an as-of join gives each run the version in force when it
started. If the two hashings agree, a version reads as one line across the switch; if they ever
drift, a version shows twice and `skill_version_source` says which is which. An uncommitted edit to
a skill is invisible to the git side - one more reason skills are committed.

**Marts**

| Mart | Answers |
|---|---|
| `mart_phase_breakdown` | What a phase costs (runs, median/p90 minutes, tokens, turns, failures, rework share) cut by provider, model, effort, skill version, prompt version, entry reason, pass, CLI version, A/B arm |
| `mart_time_split` | Where the wall clock goes: prep, startup, model, tools, hand-off |
| `mart_rework` | Each phase's runs and minutes by why they happened |
| `mart_tool_time` | What agents wait on, by tool and by shell command |
| `mart_start_rebase` | What the start rebase costs, by what it found |
| `mart_diff_size` | Duration and tokens by size of change |
| `mart_first_pass_qa` | Share of tickets QA never sent back, by week and by what built them |
| `mart_qa_rulings` | QA's carry-overs and passes by inspection, how often refinement overturned one, and how often the re-drive then failed - by the QA run's model, effort, provider, skill and prompt version and arm. Read from the append-only `QaRulingEvents` log, each ruling joined to its run by the ticket lease id (`CycleRuns.LeaseRunId`) |
| `mart_version_trend` | Each skill and prompt version in order, with what its runs cost and how often their work came back |
| `mart_daily` | The trend under all of it |
| `fct_runs`, `fct_tool_calls`, `fct_tickets` | The drill-down: one run, its calls, one ticket |
| `fct_bug_links`, `mart_bugs` | Bugs traced to their cause: confirmed and escaped bugs by the stage that missed them, who found them, and what wrote them |
| `fct_experiment_tickets`, `mart_experiment_arms`, `mart_experiments` | A/B experiments: per arm, cost per ticket to Done and the three guardrails; per experiment, its stage, the Fisher tests and the suggested verdict |

"Cost" is tokens and wall clock, as on the rest of the page - the fleet runs on a subscription, so
headroom is the scarce thing. `fct_runs.list_cost_usd` carries Claude Code's own list-price figure
for a run whose session served only that run; it is a relative weight, not a bill.

## Bugs and experiments

The board traces a bug ticket to the tickets whose commits wrote the lines it fixed, and runs A/B
experiments on a phase's skill, model, effort or provider (docs/bugs-and-experiments-plan.md). Its
tables are the source of truth; the board computes only what it must act on (the auto-stop, and the
stage and verdict on its own page), and the comparisons over time live here.

- **A table the database does not have yet is loaded empty**, like a missing column, so these models
  build on a board that predates the migrations and fill in once they have run.
- **A bug is a confirmed link.** `Escaped` is the board's own flag (`BugTraceService.IsEscaped`),
  read, never recomputed. `fct_tickets.escaped_bugs` counts them against the ticket that caused
  them, and `mart_first_pass_qa.escaped_rate` sets that beside QA's pass rate: what QA caught, and
  what it did not.
- **The run that wrote a bug is the board's "likely" tier**: among the source ticket's runs whose own
  commits changed a blamed file (`RunFilesJson`), the latest before the ticket was merged or Done.
  The "exact" tier asks git whether a commit lies inside a run's recorded range, which is a question
  for the repository at read time, so `run_tier` says `likely` or `none`, never `exact`.
- **An experiment is measured as the board measures it.** `fct_experiment_tickets` follows
  `ExperimentService.MeasureAsync` line for line - every run of every skill from assignment on, the
  entry reasons as recorded, conflicts not counted as rework - and `mart_experiments` repeats its
  stages, its one-sided Fisher test (`lchoose` in `macros/board.sql`) and its thresholds. They were
  checked against the board's `/api/experiments` on the same database, value for value. A change to
  either side's rules changes both.
- **A run that ran as configured is in no arm.** A run that named an arm with
  `ExperimentApplied = false` (the experiment was stopped, or an override was not honoured) reads as
  `not applied` in `experiment_arm`, so `mart_phase_breakdown` never credits an arm with the other's
  settings. The board's per-ticket numbers still count those runs; `mart_experiment_arms` gives both,
  the board's and `applied_*` - the cost over Done tickets that never left their arm.

## Changing it

- A new board column: add it to `BOARD_TABLES` in `refresh.py` (missing columns load as NULL, so
  the models can be written before the migration ships) and to `stg_board__cycle_runs`.
- A new question: a mart in `models/marts/`, cut with `scoped()`, with a `unique_combination` test
  on its grain, and its name in `PUBLISHED` in `refresh.py` and `AnalyticsService.Marts`.
- Run `dbt build` against real data before committing - the tests encode what the page assumes:

```bash
uv run --project analytics python analytics/refresh.py --out-dir <scratch folder>
```
