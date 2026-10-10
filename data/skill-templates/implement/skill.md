---
name: implement
description: Implement a local ticket's work items end-to-end - code in the ticket's worktree, commit locally (never push), mark items done, write an implementation summary, then move to NeedsImplementationReview
argument-hint: "[TICKET_ID]"
---

# Implement a Local Ticket ({{PROJECT_NAME}})

**Before starting:** Read what this repo already knows - the notes store named in your prompt - and follow the working directory's conventions file (named in your prompt). This is a generic template - defer to that file for branch naming, build/test commands, and any project-specific rules.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A ticket that asks for work on a deployed environment is not blocked and not refused - it is prepared.** Running it is a human's job; yours is the investigation and the artifact they run it with: the exact statements, proven locally against the local database, with a rollback and a verification step, committed to this branch, and a runbook `add_comment` saying what the human runs, against what, in what order, what they should see, and how to undo it. That is a normal successful outcome of this ticket, not a lesser one. Your prompt's hard rules state this in full; what this phase owes it is below.

You are handed a local ticket in the **Implementing** phase. Do the actual code work for its approved work items, then hand it to QA. Communicate via `add_comment` on the ticket (the `tickets` MCP server), never the terminal.

## Ticket state - use the `tickets` MCP tools

- `get_ticket` to read the ticket and its `workItems`. Items with `done: false` are what you implement; `done: true` are already complete - skip them.
- **Read the attachments.** `get_ticket` returns `attachments` - the linked issue's attachments already downloaded to this machine, each with a `localPath` you open with the Read tool - and an `attachmentsNote` saying what that list does and does not cover. A mockup or design PDF settles the small decisions the work items leave open. Guessing at one you could have opened is how a plausible wrong answer gets committed.
- `get_issue_context` (READ-ONLY) when you need acceptance criteria from a linked `issueKey`, or whenever `attachmentsNote` says something is not downloaded yet.

## Code search - `codebase-retrieval` first for "where" and "how"

When the `auggie` MCP server is in your tool list, its `codebase-retrieval` tool (`mcp__auggie__codebase-retrieval`) is a semantic index of this worktree. If your tools are deferred, **load it in the same call as the ticket tools** - no step below names it, so a worker that does not load it up front never uses it.

- **Ask it first when you do not yet know which files hold the answer** - where a screen is rendered and which endpoint feeds it, how a record gets from the form to the table. Ask in full sentences naming the screen, the concept and the layers you need (UI, API, SQL): one specific question beats three thin ones.
- **What it returns is a map, not a verdict.** Open the files it names before you rely on them. It can answer a backend question entirely from frontend service wrappers; when the layer you asked about is missing from the answer, go and find it with Grep rather than asking again.
- **Grep and Glob, not retrieval, for exact names and for "every"** - a known symbol, an error string, every writer to a column. Retrieval returns the dozen most relevant files, never an exhaustive list, and an enumeration built on it looks complete while missing call sites.
- A call takes around half a minute. Worth it for an unfamiliar area, not for a question one grep answers.
- **You will need it less here** - the approved work items usually name the files. Reach for it when an item sends you somewhere the plan did not map, or to find an existing pattern to copy rather than invent one.

## Workflow

### 1. Confirm your workspace
Your working directory is a git **worktree** that belongs to this ticket alone, already checked out on its feature branch by the heartbeat. Confirm it before you write anything:
- `git rev-parse --abbrev-ref HEAD` should report this ticket's branch (recorded on the ticket as `branch`). If it does not, `add_comment` with what you actually got and `set_phase` to `Blocked`, then stop.
- `git status --porcelain` should be empty. If there are uncommitted changes that aren't expected per this repo's conventions file, `set_phase` to `Blocked` with the reason and stop.

Do **not** run `git checkout`, `git switch`, or create a branch. Speculation already created this ticket's branch, and other checkouts of this same repo exist - the human owner's, and the worktrees of other tickets. Never implement on the base branch or a freshly created one.

### 2. Implement each open work item

Work the items **one at a time**, and take each one all the way round this loop before you pick up the next:

1. **`start_work_item`** for the item you are about to do - before you write any code for it. This is what puts the "working on this now" marker on it, so a human watching the board can see where you are in a list of 17 while the run is still going. Coming back to redo an item you had already ticked off? Call it again on that item; the marker moves back up with you.
2. **Write the code.** Follow every convention that file sets out. Add or update tests as the branch-coverage check below requires, and follow any project-specific steps it describes (database migrations/scripts, code generation, config, etc.).
3. **Commit.** One atomic commit per work item. Write each message for whoever reads this repo's history: what changed and why, prefixed with the ticket's `issueKey` when it has one (e.g. `PROJ-123: Add export button`), otherwise just the description. Never put the dashboard's own ticket number in a commit message - it means nothing outside this system. And never an attribution trailer: no `Co-Authored-By: Claude ...` line and no mention of Claude, Anthropic, Codex, Gemini or a model anywhere in the message, however firmly your own CLI asks for one.
4. **`set_work_item_done`** - immediately, and only now.

**Tick items off as you go, never in a batch at the end.** A ticked item means "that work is committed on this branch", and that is exactly what a resumed run trusts when it skips it. So the order in the loop above is load-bearing in both directions: ticking before the commit lands tells the next run something false, and saving all the ticks for the end throws away the record of what you finished if the run is cut short - the next run then re-reads seventeen open items and cannot tell which twelve you already built.

#### Write-integrity check - before you commit (step 3)

If your change adds a query containing more than one writing statement (INSERT / UPDATE / DELETE / MERGE), or writes a second table as part of the same logical operation, settle atomicity before you commit. Trace the call chain out to the **outermost caller** - the controller, RPC method, or job entry point - and establish whether a transaction is actually opened anywhere on that path. Then state the verdict in your `add_comment` for the item, in one of these two forms:

- **Atomic** - naming what opens the transaction and where.
- **Not atomic** - naming why, and what state the data is left in on a partial failure. If that is the right call for this ticket, fine; write it down for the reviewer rather than leaving it to be discovered.

Two ways this goes wrong. Both produce a change that looks correct and that a passing QA run cannot tell apart from one that is:

- **A parameter is not a transaction.** A method that accepts a transaction (`IDbTransaction` or equivalent) and dutifully forwards it is doing nothing whatsoever if every caller on the live path passes `null`. "It joins the caller's transaction when one is supplied" is not a verdict - go and find out whether one is ever supplied.
- **The method next to yours is not evidence.** Local precedent tells you the house style, not whether the house style is safe here. If the neighbouring function has the same hole, that is a fact to report, not a pattern to match.

Do not skip this because the answer looks obvious, and do not settle it in a commit-message qualifier. This is the one class of defect the pipeline is structurally blind to: the feature works when you drive it, so QA passes it, and refinement is forbidden from changing behaviour. If you do not resolve it here, nobody downstream will.

#### Branch-coverage check - before you commit (step 3)

A branch you add is a branch you own. Before the commit that carries it, settle how each new path gets exercised - a new condition, a new fallthrough, a new default arm, a new caller of a method whose signature you widened - and name in that item's `add_comment` which of these you reached:

1. **Driven through the app**, on data that is already there.
2. **Driven through the app**, on data you created for it.
3. **Driven at a lower seam** - a test in this project's suite, calling the method or the service directly. This is where a branch with no screen in front of it belongs.
4. **Read only** - you traced it and argued it is right.

Work down that list and stop at the first rung that holds. Rung 4 is an honest outcome for a path nothing can reach - a vendor callback you cannot provoke, a race you cannot stage - and a poor one for anything else, because it is the only rung that proves nothing about the code as built.

**"There is no data for that case" is not a reason to fall to rung 4.** It is rung 2 or rung 3 telling you what to build. Absent data is a fixture nobody has written yet, and writing it belongs to this item rather than to whoever hits the branch in production. A ticket that adds a fallthrough for accounts with no allocations, and then reports that no local account has none, has described its own missing test.

**A fixture you commit ships with the branch.** Test data in a migration, a seed script or anything else that runs on deploy runs in every environment this code reaches - production included - unless something stops it. Before you commit one, find what this repo uses to keep test data out of deployed environments (a tag, a profile, an environment check; the conventions file, the notes store or the neighbouring fixtures will show it), put your fixture behind it, and name that gate in the item's `add_comment`. If the repo has no such gate, the fixture does not go in a deploy-time path at all - ask with `Blocked` rather than guess. Running it proves nothing here: every database this pipeline can reach is a development one, so a gated fixture and an ungated one pass every check identically. A guard inside the script that skips when its data is missing is not a gate either - production has data. The reverse mistake is as real: a schema change, or reference data production genuinely needs, must *not* go behind the gate.

The rungs matter most on the paths that are hardest to see and cheapest to get wrong: the default arm of a lookup that in practice always hits, and the *other* writers of a field you changed the rules for. Both read as fine in a diff, and both are invisible to a QA run that walks only the screen the ticket was about.

#### Verification check - before you commit (step 3)

Build what you changed, run the tests that cover it, and run this project's format and lint checks over the files you touched, using the commands in the conventions file. Then say in that item's `add_comment` what you ran and what it reported.

- **Build first, on every item.** A compile error is the cheapest defect in this pipeline to find and one of the most expensive to discover late: nothing downstream reads for it, so it surfaces when QA rebuilds the whole stack, and that costs the cycle. Build before each commit rather than once at the end, so a break belongs to the item that caused it.
- **Run the tests that cover what you touched**, including any you just wrote for rung 3. Scope them the way the conventions file describes - the suite filtered to the area you changed, rather than the whole tree on every item, unless the whole tree is quick. **Nothing else in this pipeline runs them.** QA drives the app and the reviewer reads a diff, so a test your branch broke stays green in every report this system produces until it reaches a human's own checkout, long after the ticket was called done.
- **Never hand-edit a generated file.** A file a tool writes - a `*.generated.*` file, a client built from an API spec, a lockfile's hashes - is that tool's output and nobody else's. When your change means it should change, run the generator the way this project's conventions say and commit exactly what it wrote, in the same item as the change that caused it. A hunk you did not cause means the base branch's copy was already stale: keep it, and say so in that item's `add_comment`, because trimming it is a hand edit too. If the generator cannot run here, `add_comment` naming the file and the generator and `set_phase` to **Blocked** - never reproduce its output by hand, and never describe a file as hand-applied in a summary as though that were a finished state. The one exception is a file whose own header says which additions may be made by hand; make only those. A hand-written copy matches until the next real run of the generator disagrees with it, and that happens at deploy, where nothing in this pipeline can see it.
- **Formatter and linter, scoped to the files this item changed** - never the whole tree. Run whichever of this repo's checks cover the files you touched, each in its verify mode. Where this repo's notes store records that the base branch already violates one of these checks, fix only what your own diff introduced: reformatting neighbouring code buries the real change in churn and edits lines no ticket asked you to touch.
- **Run them where they actually run.** A check that will not run on the host - dependencies not installed, a runtime older than the repo requires - runs wherever this repo does run it (a container, a task runner); the notes store says which works here. "It would not run" is not an outcome, it is a step you have not finished.
- **Record every check, including the ones you skip.** One line each in the comment: what you ran and whether it passed, or which check you skipped and why. An unrecorded skip is indistinguishable from a pass, and the person it misleads is the reviewer at the end of the pipeline.

#### Building an artifact a human will run somewhere else

Where a work item's real target is a deployed environment - a row to delete on production, a record to correct, a file to remove - you build the thing the human runs, and you run it nowhere but here. The prohibition is in your prompt's hard rules. What it costs *you* is the difference between a script and a proven script:

- **The artifact is committed, like any other deliverable.** A script, a migration, a `.sql` file in whatever directory this repo keeps such things - not a fenced code block inside an `add_comment`. It goes round the same commit-per-item loop as everything else, so it is reviewable in the diff and it still exists next month.
- **Prove it against the local database, on data you put there.** Seed the local rows into the state the deployed environment is in, run the artifact, show it did what it claims. This is rung 2 of the ladder above and it applies here without softening. An unexecuted script handed over as though it were ready is the exact failure this route exists to prevent - and it surfaces on somebody's production rather than in your run.
- **Prove the rollback the same way.** Run it; show the rows came back. A rollback nobody has executed is a comforting sentence, not a safety net.
- **Prove the verification query** returns what you say it returns, both before the change and after.
- **Report the counts, not the verdict.** In that item's `add_comment`: how many rows the selector matched locally, how many the change touched, what the verification returned. Comparing those against production's own counts is the last check a human gets before something irreversible, and they cannot make it if all you wrote was "worked".

Then, as its own work item, the **runbook `add_comment`**: what is wrong, the ordered steps, what each is run against, what they should see after each one, how to verify, how to roll back, and what should make them stop. Write it for someone who has not read this ticket. That comment and the committed artifact together are the deliverable - the ticket is complete with them and hands off through the normal phases, exactly like one that shipped a feature.

### 3. Post progress
`add_comment` with a summary of what you changed (files/areas touched, notable decisions).

### 4. Write the implementation summary
Call `set_implementation_summary` with a short, plain-language description of the delivered solution - **4 sentences max**. Mix the business "what/why" with light technical detail; keep it easily digestible, not deeply technical. This is the always-current, human-readable snapshot of the solution shown on the ticket, so rewrite it fully each time (it replaces any prior summary), reflecting the complete state of the work now - not just this run's diff.

### 5. Commit locally (never push to origin)
Ensure **all** your changes are committed to the ticket's branch - QA and review must run against a clean, committed tree. Prefer atomic commits per work item, messaged as above; at minimum, `git status --porcelain` must be empty before you hand off. **Never run `git push`** (or `git push origin ...`): the heartbeat workflow is entirely local - pushing to origin is out of scope and not permitted.

### 6. Hand off
- `set_phase` to `NeedsImplementationReview`. That is the only legal move from `Implementing`: QA is mandatory in this pipeline, so you can never jump to `NeedsQa`, `NeedsReview` or `Done` yourself.
- Most tickets don't ask for a human look at the code, so the dashboard forwards them straight to `NeedsQa` for you. Always hand off to `NeedsImplementationReview` regardless - the dashboard decides per ticket - and don't be surprised if a later `get_ticket` shows `NeedsQa`. If there is genuinely nothing to QA, the QA phase itself records that and moves the ticket on.

## If the ticket is bigger than one run

A large ticket is not expected to fit in one run, and ending a run part-way through the work items is a legitimate move - not a failure, and not a reason to `Blocked`. What matters is *where* you stop: the next run starts from an empty context and knows only what it can read on the ticket.

Before you end a run early:

- **Finish, commit and tick off the work item you are on.** Never leave a half-written change behind; the tree must be clean. Every item before this one should already be ticked - if you have been following the loop above, the checklist is the handoff and there is nothing to reconcile here.
- **`set_implementation_summary`**, rewritten to describe the work as it now stands. This is also how the system sees that the run advanced the ticket - a run that leaves nothing changed looks wedged and counts against the ticket's attempt limit.
- **`add_comment`** with what you did and what remains, **in the order you would do it next**. The next run works from that list, so name the files, the decisions already made, and anything you ruled out. A vague note costs the next run the time it takes to rediscover what you already knew.
- **Leave the phase in `Implementing`.** Do not call `set_phase` - sitting in `Implementing` is exactly what tells the heartbeat to pick this ticket back up and carry on.

Do not stop early for safety's sake; stop when the work genuinely does not fit. When you do stop, spend the last of the run on the handoff rather than on one more edit.

## If you cannot honestly finish

Three things, routed two ways:

- **The workspace or the repo is in a bad state** - wrong branch, a dirty tree you did not make, a build that will not run, a command that hangs -> `add_comment` with the details and `set_phase` to `Blocked` with what stopped you in the `reason`, then stop.
- **The ticket itself does not add up.** A work item contradicts another, or the acceptance criteria contradict the description or the plan, or an item cannot be built without guessing at something central that a wrong guess would throw away. -> Finish and commit whatever items you have already done, `set_implementation_summary` so that work is not lost, then `set_phase` to `Blocked` with the contradiction or the one question in the `reason`. Name the two things that conflict: "work item 3 says soft-delete, AC 2 says the row disappears from the audit log - which wins?" is answerable; "requirements unclear" is not. Blocking is available from any phase and costs the ticket nothing.
- **An item needs a decision nothing the owner has said settles.** Building it means choosing something a user sees, an amount, or a rule, and no option is in the description, its attachments, the master notes or an approved `expansion` item. -> The same route as a contradiction: commit what is done, then `Blocked` with the choices and the in-scope one `recommended`. Do not pick one and leave it for the reviewer - by then it has been built and tested, and taking it out costs another cycle.

None of these is a failure. What is a failure is guessing at a central decision and building on it: that produces a plausible wrong answer that QA passes and review has to catch. But do read first - the repo, this project's notes store, the ticket's attachments, the master notes and the linked issue answer most questions, and a block on a question you could have answered yourself costs a full cycle. And if the requirements *moved* rather than being wrong all along, that is `request_respeculation`, not a block.
