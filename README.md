# Whetstone

A local ticketing board that runs a small fleet of coding agents through your tickets — plan,
build, test, tidy — and stops at the points where a person should decide.

![The board: every ticket on one track, from backlog to done](docs/screenshots/board.jpg)

Whetstone is a single app on your own machine. You write a ticket (or link one from Jira or Azure
Boards), and a background loop called the **heartbeat** hands it to an agent, one phase at a time.
Each phase leaves a record a human can check: a plan, a diff, a QA checklist with screenshots, a
short brief on what changed. Nothing is pushed, merged or deployed without you.

> The screenshots show a fictional project, "Harbor", seeded for this README.

## What it does

**A pipeline with gates.** A ticket moves through speculation, implementation, QA and refinement.
Three of the stops belong to you: approve the plan, review the result, and answer when an agent is
blocked on a decision only you can make.

| Phase | Who | What it leaves behind |
|---|---|---|
| Speculate | agent | Work items, each marked *required*, *consequence* or *expansion* |
| Approve | **you** | A go-ahead, or the plan sent back |
| Implement | agent | A commit on the ticket's own branch and worktree, and a summary |
| QA | agent | A checklist run against the local stack, with captioned screenshots |
| Refine | agent | One squashed commit, rebased, and a brief on the part of the domain it touched |
| Review | **you** | Merged, or sent back with notes the next run must answer |

![A ticket: the plan, what was built, and the QA checklist](docs/screenshots/ticket-plan.jpg)

**A record of the trip.** Every phase change, comment and run is kept, so a ticket shows how it got
where it is — including the times it went backwards.

![A ticket's progress notes and the path it took through the pipeline](docs/screenshots/ticket-journey.jpg)

**Review where the risk is.** The pull-request dialog drafts the title and description from the
ticket and sorts the diff by what is worth reading, so a three-line change to a query is not buried
under twenty files of generated boilerplate.

![The pull-request dialog, with the diff ranked by risk](docs/screenshots/pull-request.jpg)

## The Classroom

Every phase in the pipeline checks the work against the *plan*: speculation writes it,
implementation builds it, QA tests against it. So a blind spot in the plan passes through all of
them untouched. The Classroom is the one reader that does not start from the plan. After QA, a
separate run reads the ticket, the diff and the code around it, and explains the change from the
system's side - what was asked, how it worked before, what changed, and whether the two agree.

![The Classroom: a ticket's analysis, with its narrated walkthrough](docs/screenshots/classroom-overview.jpg)

In the demo, QA had passed the ticket five for five. The analysis found that the server now
refuses the double booking but the form that submits it throws the refusal away, so the skipper is
never told - and that the two new tests call a function that does not exist. Nothing in that
analysis was seeded; it is the feature's own output on the demo repository.

**Requirement coverage.** The ticket's text is split into lines and each one gets a verdict -
met, partial, gap, deferred - with the code that decides it quoted beside it. A line the run said
nothing about is marked unaddressed by the app, not by the model, so silence cannot pass as a yes.

![Requirement coverage: each line of the ask, a verdict, and the code behind it](docs/screenshots/classroom-coverage.jpg)

**The user's path.** The change is drawn as the route a person takes through the product, with
what is new marked against what was already there. Every stop links to the lines of code behind it.

![The user's path through the change, new steps marked](docs/screenshots/classroom-flow.jpg)

**Why it was built this way, and what would catch you out.** The alternatives that were weighed,
what each costs and leaves open, how the claim that it works was established, and the risks worth
knowing before you touch the area again.

![Alternatives weighed, how we know it works, and a risk called out](docs/screenshots/classroom-why.jpg)

**It is part of the pipeline, not a report on the side.** The risks and partial verdicts an analysis
raises become findings on the ticket, and refinement cannot hand off until it has ruled on each
one: a defect to fix, or not a defect, with a reason. A small ticket can opt out.

**A narrated walkthrough.** Each analysis also comes as a few minutes of guided telling: scenes
that point at the diagram, the quoted lines and the diff as they are described, with captions and
optional speech synthesised locally. Every pointer in the script is checked against the analysis
before it is shown, so the narration cannot gesture at something that is not there.

![The walkthrough player, lighting up the steps it is describing](docs/screenshots/classroom-walkthrough.jpg)

**Built so it cannot ramble.** The run is not asked for prose. It returns a fixed set of typed
blocks - a comparison, a reading path, a glossary, a flow, a coverage map - under a JSON schema,
and the app decides how each looks. A section with nothing real to say comes back absent rather
than padded. Four lenses re-read the same ticket for different readers: full, decisions, code and
testing.

## More of what it does

**Agents that stay on this machine.** Every worker runs in its own git worktree against a local
stack. A `PreToolUse` guard denies the commands that reach beyond it — cloud CLIs, `git push`,
database clients and HTTP requests aimed off-machine — and a ticket that asks for production work
is turned into a runbook for a person instead. A project can add rules of its own on top; it can
never loosen the shared ones.

**What it costs.** Rework rate, first-pass QA, time and tokens per phase, and how much rate-limit
headroom is left.

![Metrics: rework, first-pass QA, cost per phase](docs/screenshots/metrics.jpg)

**Also in the box**

- Up to three workers at once, with leases so two agents never rebuild the same stack.
- Claude Code, Codex or Gemini CLI as the agent, chosen per installation, with a model per phase.
- Read-only links to Jira and Azure Boards; git or TFVC-backed repos.
- Slack or ntfy notifications when the fleet needs you.
- Five themes. The look is a token system, not per-page styling.

| | |
|---|---|
| ![Snow theme](docs/screenshots/board-snow.jpg) | ![Mountain theme](docs/screenshots/board-mountain.jpg) |

## How it fits together

```
 Browser (React + Vite)  <-- SignalR -->  ASP.NET Core API (.NET 9)  -->  SQLite (data/dashboard.db)
                                                 |        ^
                                    starts/stops |        | MCP tools: set_phase, add_qa_items, ...     
                                                 v        |
                                  heartbeat.ps1  -->  worker.ps1  -->  agent CLI in a git worktree
                                  (one loop)          (one per ticket)   + worker-guard.ps1 on every tool call
```

- **`Api/`, `Services/`, `Models/`, `Persistence/`** — the board, the phase machine, git and tracker
  integration, metrics.
- **`Mcp/`** — the tools an agent uses to read its ticket and report back. Hand-offs are tool
  calls with checked requirements, not prose the next phase has to trust.
- **`ClientApp/`** — the React client. The built bundle is committed in `wwwroot/` so a clone runs
  without a Node step.
- **`data/`** — the orchestrator (`heartbeat.ps1`, `worker.ps1`), the dispatcher prompt, the guard,
  and `skill-templates/`, which a new project's phase instructions are scaffolded from.
- **`data/projects/<key>/`** — everything specific to one project: its skills, its config, its own
  guard rules. Gitignored here; each is its own repository. The platform names no project.
- **`Whetstone.Tests/`** — about 950 xUnit tests. The PowerShell guard and base-ref logic have
  their own test scripts beside them.

[`CLAUDE.md`](CLAUDE.md) holds the conventions that are not discoverable from the code, and
[`data/README.md`](data/README.md) describes the data folder in full.

## Running it

**New machine? Start with [`docs/setup.md`](docs/setup.md)** — everything a fresh install needs,
which parts are optional, and how to check each one, since several fail silently when missing.

Requirements: Windows, the .NET 9 SDK, Windows PowerShell 5.1, git with a `user.name`/`user.email`,
Node (QA drives a browser through `npx`), and at least one agent CLI signed in (`claude`, `codex` or
`gemini`). Voicing Classroom walkthroughs also needs `npm install` once in `data/narrator/`.

```bash
dotnet run --project Whetstone.csproj
```

Then open http://localhost:5338, add a project in **Settings** (a name and the path to a git
repo), write a ticket, and start the heartbeat from the header. `Start-Whetstone.bat` does the
first two steps in one double-click.

Machine-local settings and secrets go in `data/config.json` and each project's `project.json`,
both gitignored; `data/config.example.json` shows the shape.

## Tests

```bash
dotnet test Whetstone.Tests -c Release
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-guard.tests.ps1
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-base.tests.ps1
```

## Status

A personal tool in daily use, built for one developer's machine. It is published as a showcase
rather than a supported product: there is no installer, and it has only been run on Windows.
