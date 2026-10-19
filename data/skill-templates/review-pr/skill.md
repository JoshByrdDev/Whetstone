---
name: review-pr
description: Review someone else's branch - read their diff, drive-test it, and stage line-level feedback plus a verdict for the ticket's owner to send. Never changes their code.
argument-hint: "[TICKET_ID]"
---

# Peer-review someone else's branch ({{PROJECT_NAME}})

**Before starting:** Read what this repo already knows - the notes store named in your prompt - and follow the working directory's conventions file (named in your prompt). This is a generic template - defer to that file for how to build, run, and log into the app, and for any environment check it adds on top of the local-only rule below. That rule is the floor: a conventions file can tighten it - naming the exact local host this project's database must be on, say - but it can never relax it.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service or released to a store or over the air, and no phone: an app is built here and run on an emulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A ticket that asks for work on a deployed environment is not blocked and not refused - it is prepared.** Running it is a human's job; yours is the investigation and the artifact they run it with: the exact statements, proven locally against the local database, with a rollback and a verification step, committed to this branch, and a runbook `add_comment` saying what the human runs, against what, in what order, what they should see, and how to undo it. That is a normal successful outcome of this ticket, not a lesser one. Your prompt's hard rules state this in full; what this phase owes it is below.

You are handed a local ticket on the **Review track**, in the **PeerReviewing** phase. The branch it names belongs to **another developer**. Your job is to read it, exercise it, and write down what you find, so the ticket's owner can send that feedback to the pull request in their own name.

## The one rule this phase is built around

**You do not change their code. Not one line, not even an obvious one-character fix.**

This is not the usual "be careful" advice. On every other phase in this pipeline the branch is ours and the working tree is committed for us when the run ends. Here it is not ours, and it is not going to be: your worktree is checked out **detached**, the heartbeat will not commit anything you leave behind, and the dashboard refuses to push, rebase or force-push a review ticket's branch at all. If you find yourself editing a file, you have misread the assignment.

The reason is not politeness. A change you make here would be one their review never saw, their CI never ran and their next push would silently clobber - and the person whose name is on the pull request would be answering for it.

**Everything you find becomes a staged comment instead.** `add_review_comment` is where a fix you are itching to make belongs; write what is wrong and what it costs, and let them make it.

## What "staged" means

Nothing you write leaves this machine. `add_review_comment` and `set_review_verdict` write to the local ticket; the ticket's owner then reads what you staged, edits it, throws some of it away, and sends the rest under their own name. There is no tool here that posts to the pull request, and that is deliberate.

Write for **the author**, not for the ticket. They will read your words with their name on the change.

## Ticket state - use the `tickets` MCP tools

- `get_ticket` for the ticket: `description`, `qaItems`, `skipQaTesting`, `review` (the branch, the PR, and what a previous pass already read), and `reviewComments` (what a previous pass already said).
- **Read the attachments.** `get_ticket` returns the linked issue's attachments with a `localPath` you open with the Read tool. On this track they matter more than usual: the diff tells you what they built, and the design PDF or annotated screenshot is what tells you whether it is what was *asked for*. Call `get_issue_context` (READ-ONLY) for the full acceptance criteria - on a review ticket that issue is the closest thing you have to a spec, because you were not in the room when the work was planned.
- `add_review_comment` per finding, as you find it. `set_review_verdict` once, at the end.
- `set_reviewed_commit` once, at the start.

## Code search - `codebase-retrieval` first for "where" and "how"

When the `auggie` MCP server is in your tool list, its `codebase-retrieval` tool (`mcp__auggie__codebase-retrieval`) is a semantic index of this worktree. If your tools are deferred, **load it in the same call as the ticket tools** - no step below names it, so a worker that does not load it up front never uses it.

- **Ask it first when you do not yet know which files hold the answer** - where a screen is rendered and which endpoint feeds it, how a record gets from the form to the table. Ask in full sentences naming the screen, the concept and the layers you need (UI, API, SQL): one specific question beats three thin ones.
- **What it returns is a map, not a verdict.** Open the files it names before you rely on them. It can answer a backend question entirely from frontend service wrappers; when the layer you asked about is missing from the answer, go and find it with Grep rather than asking again.
- **Grep and Glob, not retrieval, for exact names and for "every"** - a known symbol, an error string, every writer to a column. Retrieval returns the dozen most relevant files, never an exhaustive list, and an enumeration built on it looks complete while missing call sites.
- A call takes around half a minute. Worth it for an unfamiliar area, not for a question one grep answers.
- **You are reading someone else's change in code you may not know.** Use it to understand the design around the diff before you judge it, and to find the other callers of anything the diff changes.

---

# Part A - Read the branch (always runs)

### A1. Confirm your workspace, and record what you are reading

Your working directory is a git worktree checked out **detached** at the head of the branch under review. Confirm it before anything else - reviewing the wrong commit is worse than not reviewing at all:

```
git rev-parse HEAD
git log --oneline -5
```

Call **`set_reviewed_commit`** with the full sha `git rev-parse HEAD` prints. Every comment you stage afterwards is anchored to it, and that anchor is what tells the *next* pass which of your findings still describe the branch and which were written against code the author has since pushed over. Skip this and your work silently loses its expiry date.

Do **not** run `git checkout`, `git switch`, `git commit`, `git push`, or create a branch. If HEAD is not what the ticket's `review.sourceBranch` should resolve to, `add_comment` with what you actually got and `set_phase` to `Blocked`.

### A2. Read the diff

Against the target branch named on the ticket (`review.targetBranch`, falling back to this repo's base branch from the conventions file):

```
git log --oneline <target>..HEAD
git diff <target>...HEAD
```

**Coming back to a branch you have seen before?** If `review.reviewedAtSha` on the ticket names an earlier commit, read `git diff <thatSha>..HEAD` **first** - that is what has changed since the last pass, and it is where the author has responded to feedback. Then skim the full diff for context. Do not re-litigate the whole branch: the fastest way to make a review worthless to someone is to repeat, in round three, the six things you already said in round one.

You are answering one question - **is this change correct, and does it do what its issue asked?** Specifically:

- Defects you can see without running anything: an unhandled path, an inverted condition, a null that can reach a dereference, an `await` that was dropped, a query that cannot use its index.
- Wiring that stops short - a service written but never registered, an endpoint added but not routed, a migration added but the model snapshot untouched.
- **A rule the diff takes away.** A dropped index or constraint, a validation narrowed, a required field made optional, a uniqueness assumption relaxed. The database enforced it against *every* writer, including the ones that carry no check of their own because they never needed one - so the call sites this breaks are the ones with nothing to grep for. Find the writers to that column yourself and confirm each is either newly guarded or genuinely fine. This is the defect class a review most often misses, and it will not be visible in the diff of the file it breaks.
- Acceptance criteria the diff does not actually deliver, or delivers only halfway.
- Whether it matches the repo's conventions as the conventions file states them, and what the notes store already says about this corner of the system.

### A3. Stage what you found, as you find it

One `add_review_comment` per finding, with the file and line wherever you can name one. **Call it as you go, not in a batch at the end** - a finding you are holding in your head is invisible to anyone watching, and lost outright if the run is cut short.

`severity` is the field that decides whether this review is useful:

- **`Blocking`** - this is wrong and must change before merge. Name the input or state that reaches the problem and what happens then. If you cannot say what breaks, it is not Blocking.
- **`Question`** - the author should answer; it may or may not be a defect. The honest choice when the code looks wrong but you do not know the domain well enough to be sure, and much better than a Blocking you have to walk back.
- **`Nit`** - worth saying, not worth blocking on. Naming, a stray log line, a comment that no longer matches its function.
- **`Praise`** - worth saying because it is good. Use it where it is genuinely earned: a review that is only ever a list of faults teaches the author to read it defensively, and the useful half then lands worse.

**Be honest with severity.** A review where everything is Blocking is one the ticket's owner has to re-triage by hand before sending, which is most of the work you are here to save them.

Write each comment to be actionable on its own. "This is fragile" is not a finding. "`PlanService.cs:212` dereferences `plan.Owner` before the null check on line 218, so an ownerless plan 500s on export" is.

**If the ticket is marked `skipQaTesting`, you are done with the work: skip Part B and go to Hand off.**

---

# Part B - Drive it (skipped when `skipQaTesting`)

## Prerequisite
Requires the **Playwright** MCP server (`mcp__playwright__*`). If unavailable, `add_comment` noting it and `set_phase` to `Blocked`.

### B1. Safety check (MANDATORY)
**Confirm where you are pointed - every run, whether or not the conventions file mentions it.** Every connection string, host and base URL this run touches must name this machine: `localhost`, `127.0.0.1`, or a service on the local docker stack. Where the conventions file names the exact value this project expects, check against that; where it says nothing, the rule still holds and you check anyway. If anything points elsewhere, `add_comment` with the actual value and `set_phase` to `Blocked`. Stop. Do not test.

### B2. Run the app
Read the QA ports and login from this project's **dashboard config** - `project.json` (falls back to `project.defaults.json`) in the dashboard project directory that contains this skill's `skills/` folder. Use `qaApiPort`, `qaWebPort` and the `qaLogin` object. Build and start the app as the conventions file describes (background shells), then log in.

**Restart it from scratch - do not reuse whatever is already running.** A running instance may be someone else's build. You hold this project's `app-stack` lease, so nothing else can be using it while you do.

**A container reporting healthy is not evidence it compiled this branch.** Where a project shares build output between checkouts, an incremental build can decide nothing needs recompiling and start cleanly on someone else's binaries. Confirm the code you are reviewing is in the artefact that actually runs. On this track that mistake is especially expensive: a clean pass on code the branch never changed becomes an approval you send to another person.

**Stop the app tier only - not the database.** Name the services you stop; a bare project-wide `docker compose down` takes shared infrastructure other agents are using with it.

### B3. Write the checklist

Their pull request does not come with one. Derive it from **the linked issue's acceptance criteria** and the diff you just read, then call `add_qa_items` once with the list.

Word each item as what a tester should do and assert - "Open a plan with no participants and confirm the export button is disabled" - never as a restatement of the diff. Cover the paths a *user* takes. The acceptance criteria are the spec here: your job is to find out whether their change satisfies the thing that was asked for, which is a different question from whether the code does what its author intended.

If a previous pass already left a checklist, re-run every item whose `verified` is false rather than writing a new one.

### B4. Test each item

One at a time, all the way round the loop before starting the next:

1. **`start_qa_item`** - before you touch the app for it, so a human watching the board can see where you are.
2. **Test it.** Navigate, interact, screenshot, check the console for errors.
3. **`set_qa_status`** - immediately: `Passed`, or `Failed` with "observed vs expected", or `Blocked` with the reason.

**A `Failed` here is a result, not a problem.** It is the most valuable thing this phase produces, and the hand-off does not require your items to pass - only that each one was taken to an outcome. Do not send the ticket back or block it because you found defects; that is the pass working.

**When an item fails, stage the comment straight away** - `add_review_comment` with the `qaItemId`. A failing check with a reproduction and a screenshot is the single most useful thing you can hand the author, and it is worth more than any amount of prose about the diff.

**Do not hold the comment back waiting for the screenshot.** Shots are attached in B5, in one call with the whole set, so the slot number does not exist yet at the moment the finding does - and a finding you are holding in your head is invisible to anyone watching the board and lost outright if the run is cut short. Write it now; **keep the comment id** `add_review_comment` hands back, and point it at its frame in B5 with `cite_qa_shot`.

Some items genuinely cannot be driven. Pass `passedByInspection: true` with a note saying what you read and why the app could not be driven. It is a weaker claim, recorded separately, and it is never the shortcut for "driving it is slow".

### B5. Capture the shots

Up to 6 screenshots, attached with `set_qa_shots` - aim for at least 3 wherever there is more than one thing worth showing. On this track they are the evidence that travels with your feedback - a screenshot of the thing going wrong is what makes a report impossible to argue with, and a second failure deserves its own frame rather than a mention in a caption.

- **Frame the subject before you shoot**, or pass `fullPage: true`.
- Capture to an **absolute path inside the screenshot directory named in your prompt**, never into the repo.
- **Open every shot with the Read tool before attaching it** and check the caption matches what is actually in the frame.
- Then `set_qa_shots(ticketId, [{ imagePath, caption }, ...])` once with the whole set.
- Prefer shots of **failures** where there are any; a passing screen is worth less to the author than the one that broke.
- **Then cite them.** `cite_qa_shot(ticketId, commentId, slot)` for every comment you staged that one of these frames shows - slots are the order you just passed them in, 0 first. This is the step that puts the screenshot beside the finding in the reader's drawer instead of in a grid they have to match up by hand, and it is the one most easily forgotten because the shots look done without it. One frame can legitimately back several comments; many comments have no frame, and a citation that does not hold up costs the reader more than none.

If the change has no visible surface, call `set_qa_shots(ticketId, [], noShotsReason: "...")` with one sentence saying why.

### B6. Close the browser
`mcp__playwright__browser_close`.

---

# Hand off

**There is one way out of this phase, and you take it whatever you found.**

1. **`set_review_verdict`** with your conclusion and a 2-4 sentence summary for the ticket's owner:
   - `Approve` - nothing outstanding.
   - `ApproveWithComments` - fine to merge, but there are notes worth reading.
   - `RequestChanges` - something must change first. A normal, successful outcome.
   - `CannotTell` - the branch cannot be judged: it would not build, or the acceptance criteria contradict each other. Say which in the summary.
2. **`add_comment`** summarising the run: what you read, what you exercised, what you staged.
3. **`set_phase` to `NeedsReview`.**

That is the only forward move, and it is correct for a clean review and a damning one alike. **There is no route back to implementation** - the code belongs to someone else and this pipeline does not fix it. If `set_phase` is refused, it names what is missing: usually the verdict, or a checklist item nobody took a view on.

`Blocked` remains available for the case where you genuinely cannot judge the branch - it will not build, or you cannot tell what "correct" would be and the acceptance criteria do not settle it. Prefer a `CannotTell` verdict where you got far enough to say something useful; block when you did not.
