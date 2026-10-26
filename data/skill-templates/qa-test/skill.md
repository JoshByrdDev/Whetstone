---
name: qa-test
description: Review a local ticket's branch diff, then - unless the ticket is code-review-only - test its QA items via Playwright, record outcomes, capture 3-6 QA shots, then move to NeedsRefinement or back to NeedsImplementation
argument-hint: "[TICKET_ID]"
---

# QA a Local Ticket ({{PROJECT_NAME}})

**Before starting:** Read what this repo already knows - the notes store named in your prompt - and follow the working directory's conventions file (named in your prompt). This is a generic template - defer to that file for how to build, run, and log into the app, and for any environment check it adds on top of the local-only rule below. That rule is the floor: a conventions file can tighten it - naming the exact local host this project's database must be on, say - but it can never relax it.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service or released to a store or over the air, and no phone: an app is built here and run on an emulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A ticket that asks for work on a deployed environment is not blocked and not refused - it is prepared.** Running it is a human's job; yours is the investigation and the artifact they run it with: the exact statements, proven locally against the local database, with a rollback and a verification step, committed to this branch, and a runbook `add_comment` saying what the human runs, against what, in what order, what they should see, and how to undo it. That is a normal successful outcome of this ticket, not a lesser one. Your prompt's hard rules state this in full; what this phase owes it is below.

You are handed a local ticket in the **QaTesting** phase. This phase has two parts, and they run in this order:

- **Part A - review the code.** Read the branch diff and judge whether it delivers what the ticket asked for. **Always runs.**
- **Part B - test the QA items.** Stand the app up and exercise it. Runs unless this ticket is marked code-review-only.

Part A comes first because reading is cheap and building is not: a defect you can see in the diff costs one file read to find, and finding it after a full stack rebuild costs the whole cycle. It also means a ticket sent back for rework never spent a QA stack on its way there.

Record everything on the ticket through the `tickets` MCP server, never the terminal.

## Which parts you are running

`get_ticket` returns **`skipQaTesting`**. When it is `true` this ticket is **code-review-only**: do Part A, then hand off. Do not build, do not start the app or the database, do not open a browser, and do not write QA items - `add_qa_items` is refused on these tickets and will tell you so.

That flag is a deliberate decision by the ticket's owner, for work small or mechanical enough that standing a stack up to click through it costs more than it proves. It is not an oversight for you to correct, and an empty QA checklist on such a ticket is the intended state rather than a gap. If the ticket has QA items on it anyway - a human may add them by hand - the flag still wins: leave them alone.

If Part A leaves you genuinely convinced the work needs exercising after all, you are not stuck: route on the strength of what you found (a defect goes back to implementation like any other), and say plainly in `add_comment` that this one deserved a real QA pass. Only a human can clear the flag; you cannot, and neither can any tool you hold.

## Several repos - only when your prompt lists **Repos**

Most projects are one repo, and this section does not apply to them. When your prompt has a **Repos** line, the working directory is a workspace of several git repos side by side, and every rule in this skill about "the branch" or "the base ref" holds once per repo:

- **Run git inside a repo, never at the workspace root**, which is not one: `git -C <repo> ...`. Each repo with a base ref in the list is on the ticket's branch; a read-only one is there to be read and must never be changed.
- **`<base-ref>` is that repo's own** - the one the Repos list names beside it. Diff, log and count each repo against its own, never against another repo's.
- **Commit in every repo you change, and only in those.** A repo you did not change is delivered as nothing - do not commit to it to keep it in step. "One ticket, one commit" means one commit in each repo the ticket changed.
- **Name the repo whenever you name a file or a result.** A file is `<repo>/<path>` (`alpha-api/src/Foo.cs`), and every outcome, QA note and comment says which repo it is about.
- **Build and test each change in the repo it is in.** The ticket is not done until every repo it changed is.
## Ticket state - use the `tickets` MCP tools

- `get_ticket` to read the ticket: its `description`, `workItems` (what implementation was asked to build), `qaItems`, and `skipQaTesting`.
- **Read the attachments.** `get_ticket` returns `attachments` - the linked issue's attachments already downloaded to this machine, each with a `localPath` you open with the Read tool - and an `attachmentsNote` saying what that list does and does not cover. This matters as much in Part A as in Part B: the diff tells you what was built, and a design PDF or annotated screenshot is what tells you whether that is what was asked for. If `attachmentsNote` says something is not downloaded yet, call `get_issue_context` (READ-ONLY) to fetch it.
- `get_ticket` also returns `nextPhases`, each with a `meaning` and, where the hand-off demands something of the ticket, a `requirement`. Read them before choosing. On a code-review-only ticket you will notice the QA-shots requirement is simply absent - that is the pipeline agreeing with the flag, not an omission.
- Running Part B? Load `set_qa_shots` alongside `start_qa_item`, `set_qa_status`, `add_comment` and `set_phase` when you select your tools. Step B5 is what unlocks the hand-off, and a worker that did not load the tool tends to decide the step was optional.

## Code search - `codebase-retrieval` first for "where" and "how"

When the `auggie` MCP server is in your tool list, its `codebase-retrieval` tool (`mcp__auggie__codebase-retrieval`) is a semantic index of this worktree. If your tools are deferred, **load it in the same call as the ticket tools** - no step below names it, so a worker that does not load it up front never uses it.

- **Ask it first when you do not yet know which files hold the answer** - where a screen is rendered and which endpoint feeds it, how a record gets from the form to the table. Ask in full sentences naming the screen, the concept and the layers you need (UI, API, SQL): one specific question beats three thin ones.
- **What it returns is a map, not a verdict.** Open the files it names before you rely on them. It can answer a backend question entirely from frontend service wrappers; when the layer you asked about is missing from the answer, go and find it with Grep rather than asking again.
- **Grep and Glob, not retrieval, for exact names and for "every"** - a known symbol, an error string, every writer to a column. Retrieval returns the dozen most relevant files, never an exhaustive list, and an enumeration built on it looks complete while missing call sites.
- A call takes around half a minute. Worth it for an unfamiliar area, not for a question one grep answers.
- **Use it to find the surface the diff reaches** - the screen and route that render a changed component, the other callers of a changed method - so the checklist covers what a user would actually touch, not only the files in the diff.

---

# Part A - Review the code (always runs)

### A1. Confirm your workspace

Your working directory is a git **worktree** that belongs to this ticket alone, already checked out on its feature branch by the heartbeat. Verify it before you read or test anything - QA against the wrong code is worse than no QA: `git rev-parse --abbrev-ref HEAD` should report the ticket's recorded `branch`. If it does not, `add_comment` with what you actually got and `set_phase` to `Blocked`, then stop.

Do **not** run `git checkout`, `git switch`, or create a branch. Other checkouts of this same repo exist - the human owner's, and the worktrees of other tickets.

### A2. Read the branch diff

Against the base ref named in your prompt (`<base-ref>` - the ref this branch was cut from, which is not always the bare branch name):

```
git log --oneline <base-ref>..HEAD
git diff <base-ref>...HEAD
```

Read it against three things: the ticket's `workItems` (what was planned), the description and the linked issue's context (what was asked), and the attachments (what it was meant to look like). You are answering one question - **does this diff deliver the ticket, and is it sound?** Specifically:

- A work item that is ticked off but has no code behind it, or code that only half does what the item says.
- Defects you can see without running anything: an unhandled path, an inverted condition, a null that can reach a dereference, a query that cannot use its index, a new setting nothing reads.
- Wiring that stops short - a service written but never registered, an endpoint added but not routed, a migration added but the model snapshot untouched.
- **Where the diff meets code it did not touch.** The defect class this phase most often misses, because it is not visible in the diff at all: it lives at the seam between new code and unchanged code that relies on it or that it relies on. Both directions come down to one question - do the two sides agree about what is valid?
  - *A rule the diff takes away.* A dropped index or constraint, a validation narrowed, a required field made optional, a uniqueness assumption relaxed. The database enforced it against *every* writer, including the ones that carry no check of their own because they never needed one - so the call sites this breaks are the ones with nothing to grep for. Find the writers to that column or field yourself and confirm each is either newly guarded or genuinely fine.
  - *A rule the diff does not inherit.* New code that saves, resolves or lists something existing code already serves to the same user - the ids a picker offers, the rows a report counts, the states a workflow allows - through a query of its own. Open the existing one and compare the filters. A narrower or wider set on the new side is a record the user can create but not see, or be shown but not pick.

  Name every seam you checked in your Part A comment, with what the unchanged side assumes - however short the list. A seam you did not name is one you did not check.
- **Behaviour nobody asked for.** Something a user sees, or a business rule, that the description, its attachments, the master notes and the owner's answers do not ask for, and no work item marked `expansion` covers. QA items do not count as asking - speculation wrote them. `Blocked`, naming the behaviour and asking keep or remove with remove `recommended`: passing it hands the owner the same question a full QA cycle later.
- Anything left behind: debug logging, commented-out code, a stray file, a TODO written during the work.
- Whether it matches the repo's conventions as that file states them, and what the notes store already says about this corner of the system.

Read to judge, not to rewrite. **You change nothing in Part A** - not a one-line fix, however obvious. A behaviour change made here would be one no implementation run planned and no QA run tested, which is exactly the hole this phase exists to catch.

### A3. Route what you found

- **Anything that changes what the code does at runtime, a work item the diff does not actually deliver, or an acceptance criterion it does not meet** -> `add_comment` naming the file, the line and what is wrong, then `set_phase` to `NeedsImplementation`. Stop here. Do not go on to Part B: it would test code you already know is going back.
- **Something a human should weigh, but nothing is actually wrong** -> `add_comment` and carry on. Writing it up is a real outcome.
- **Nothing** -> `add_comment` with a short note on what you read and what you checked, and carry on.

An `add_comment` either way. A code review that found nothing and said nothing is indistinguishable from one that never happened.

**An acceptance criterion the branch does not meet goes back - even when the gap is old, small, or nobody planned for it.** This holds wherever you find it: reading the diff here, or driving an item in Part B. The criteria are the contract; the work items are only speculation's plan for meeting them, and where the two part ways the criteria win. None of these is a reason to pass:

- *"That line is byte-identical on the merge base, so it is pre-existing."* Pre-existing settles behaviour the ticket does not ask about. When a criterion names it, the ticket **is** the request to change it - a story that says "review and enhance" is exactly that.
- *"No work item covers it"* or *"speculation judged it already satisfied."* Then speculation was wrong, and implementation adds the work item. That is the route working, not a reason to skip it.
- *"It is minor"* or *"cosmetic until something else happens."* The owner wrote the criterion. Deciding it does not matter is their call, and they make it by changing the criterion, not by a QA note.

So record it where the hand-off can see it: unless this ticket is code-review-only, add a QA item worded as the criterion demands (`add_qa_items`) where none covers it, and `set_qa_status` it `Failed` with what you observed. A finding that lives only in a comment is invisible to the gate and gets rediscovered a full cycle later - one ticket passed QA with two unmet criteria written up "for the reviewer", refinement sent it straight back, and the round trip cost an implementation run and a whole QA pass. "Something a human should weigh" above is for what the ticket does not settle - a pre-existing bug it never mentions, a label that could read better - never for a criterion it states. And a criterion you cannot judge because it is ambiguous is not a pass either: that is `Blocked`, with the question (Hand off, last bullet).

**If `skipQaTesting` is true, you are done with the work: skip Part B entirely and go to Hand off.**

---

# Part B - Test the QA items (skipped when `skipQaTesting`)

## Prerequisite
Requires the **Playwright** MCP server (`mcp__playwright__*`). If unavailable, `add_comment` noting QA is blocked and `set_phase` to `Blocked`, then stop. (This is a Part B requirement only - a code-review-only run is given no browser on purpose and must never Block over its absence.)

### B1. Safety check (MANDATORY)
**Confirm where you are pointed - every run, whether or not the conventions file mentions it.** Every connection string, host and base URL this run touches must name this machine: `localhost`, `127.0.0.1`, or a service on the local docker stack. Where the conventions file names the exact value this project expects, check against that; where it says nothing, the rule still holds and you check anyway. If anything points elsewhere, `add_comment` with the actual value and `set_phase` to `Blocked`. Stop. Do not test.

### B2. Run the app
Read the QA ports and login from this project's **dashboard config** - `project.json` (falls back to `project.defaults.json`) in the dashboard project directory that contains this skill's `skills/` folder (i.e. `../../` from this skill file's own directory; NOT the target repo). Use `qaApiPort`, `qaWebPort`, and the `qaLogin` object. Build and start the app as described in this repo's conventions file (background shells), then log in with the `qaLogin` credentials. If build/startup fails, `add_comment` and `set_phase` to `Blocked`; do not keep retrying.

**Restart it from scratch - do not reuse whatever is already running.** Stop the app first, then start it, every attempt, even when it already looks up. A running instance may be someone else's build: another ticket's worktree, the human's own session, or your own previous turn. QA against the wrong build is worse than no QA, because it produces a confident pass on code that was never tested. If the project has a stop command (`docker compose down`, killing the dev server), run it before the start command. You are safe to do this: the heartbeat gave you this project's `app-stack` lease before handing you the ticket, so nothing else can be using it while you hold it.

**A container reporting healthy is not evidence it compiled your code.** Where a project shares build output between checkouts - a cache directory, a named volume, a common `bin`/`obj` - an incremental build can decide nothing needs recompiling and start cleanly on someone else's binaries. Find out how this repo's build is invalidated and confirm your change is in the artefact that actually runs, not just in the source file you can read. A pass obtained against code the ticket never changed is the most expensive result this phase can produce, because it is reported as a success.

**Stop the app tier only - not the database it sits on.** The lease you hold covers the things that run this branch's code: the services, the front ends, the proxy, and any run-once migration step (leave that in place and your branch's migrations never apply). It does not cover the database, the object store, or anything else the whole machine shares - and those must stay up, because the other phases use them and hold no lease. Implementation and planning runs query the database constantly without ever starting the app; that is the intended workflow, not a mistake to design around. Take the database down and they scramble to restore it, and their restore lands on top of your test run. So name the services you are stopping. A bare project-wide `docker compose down` is the thing to avoid, and a project whose stop command is written that way should have it narrowed in that project's own copy of this skill.

> `qaLogin` is a single username/password, which only fits an app with one login. If the project has several front ends or needs a different persona per ticket, do not try to model that in the dashboard config - replace this step in the **project's own copy** of this skill with a Credentials section covering each site and how to obtain an account for it.

### B3. Make sure there is a checklist to run

**Your test cases are every item whose `verified` is `false` - not just the `Todo` ones.**

`get_ticket` gives each QA item a `verified` flag alongside its `status`. It is `true` only for an item that is `Passed` **and** was passed since this ticket last went back for rework. A `Passed` older than that bounce is a verdict about a version of the branch that no longer exists, and the pipeline has stopped counting it: `verified` is `false`, the drawer draws it "expired", and `set_phase` to `NeedsRefinement` will refuse while it stands. Re-run it like any other item.

Do not reason your way out of this. "The diff since that run does not touch this file, so the item still passes" is the argument that has cost this pipeline the most, and it is wrong in the one case that matters: a change to a schema, a migration, a shared config or a dropped constraint has a blast radius that appears in no file's diff. If you genuinely cannot drive an item, B4 has a way to say so honestly - but it is a recorded weaker claim, not a shortcut, and it is never the default.

**If the ticket has no QA items at all, write them before you test.** Derive them from the ticket's description, its acceptance criteria, the work items and the diff you just read in Part A, then call `add_qa_items` once with the list. Word each one as what a tester should actually do and assert ("Open a plan with no participants and confirm the export button is disabled"), never as a restatement of the work item. Cover the paths a user takes, not the lines the diff touched.

This is the step that makes the phase worth running. A QA pass over an empty list proves nothing, and handing the ticket on because nobody had written the checklist yet is how untested work reaches a reviewer looking tested - so the hand-off refuses an empty checklist too.

### B3a. Make the data an item needs

An item you cannot run because the data for it does not exist is not an item you cannot run. It is one whose setup nobody has done yet, and doing that setup is part of testing rather than a precondition for it. "No account here has zero allocations, so I read the code instead" is the most common way a branch reaches production having never once executed.

Create what the case needs. Where the app has a screen for it, build it the way a user would - that path is itself worth walking, and it fails honestly when the thing you were setting up is the broken thing. Where there is no screen, write it straight into the QA database with the tools this project's conventions file names. Either way it is data for **this run**: say in the item's note what you created, so the next person reading a surprising row knows where it came from.

Two things this does not license:

- **Not a licence to edit the project's shared fixtures.** Where this project keeps a seed, a factory or a migration that CI also runs against, adding to it is a code change and belongs to implementation - route it there like any other gap. Part A's rule holds through all of Part B: you change nothing in the repo.
- **Not a way to make a failing item pass.** If the feature only behaves once you have shaped the data unusually, that is the finding, not the setup. Write down what you had to do, because the next person in that state will be a user who cannot do it.

Creating data has a cost this project may care about - a shared QA database accumulates whatever every run leaves in it, and assertions elsewhere may rest on counts. Read what the notes store says about that before you write rows directly, and prefer the app's own path, which leaves the kind of data the app itself makes.

### B3b. When the deliverable is a script a human will run somewhere else

Some tickets deliver an artifact rather than a feature: a cleanup script, a data correction, a one-off migration that a **human** will later run against a deployed environment. QA it the way you would anything else, which here means **running it** - locally, on data you put into the state the deployed environment is in. A script nobody has executed is not a deliverable, and "I read it and it looks right" is a `Failed` item with a reason, not a pass.

Four things to prove, each its own QA item if the checklist does not already carry them:

- **The selector matches what it claims.** Run the `SELECT` on its own first and compare the count against what implementation reported. This is the number the human will check against production before they run anything irreversible, so a wrong one here is the most expensive defect on the ticket.
- **It does not match what it should leave alone.** Seed a row that is *close* to the target but must survive - same table, same shape, one field different - and prove it is still there afterwards. A `WHERE` that is right about the example and wrong about the general case is the failure this phase exists to catch, and it is invisible to a test that only seeds rows the script should hit.
- **The rollback works.** Run it; confirm the rows came back as they were.
- **The verification query says what the runbook says it will**, before and after.

Then read the runbook comment as a stranger would: could someone who has not read this ticket follow it, in order, and know at each step whether to carry on or stop? A runbook missing its rollback, its expected counts, or its stopping condition is an incomplete deliverable - route it back like any other gap.

You run it nowhere but here. If the artifact genuinely cannot be exercised locally, say so in the item and fail it: that is a real finding about the deliverable, not a limitation of your run.

### B4. Test each unverified QA item

Work the items **one at a time**, and take each one all the way round this loop before you pick up the next:

1. **`start_qa_item`** for the item you are about to test - before you touch the app for it. This is what puts the "testing this now" marker on it, so a human watching the board can see where you are while the run is still going. It matters more here than during implementation: an item reads `Todo` both before anyone looks at it and while it is being tested, so without this call a run halfway through fourteen items is indistinguishable from one that has not started. Coming back to retest an item after a fix or a flake? Call it again on that item - it drops back to `Todo` and the marker moves back up with you.
2. **Test it.** Navigate, interact via Playwright, screenshot, check the console for errors, and judge PASS/FAIL/BLOCKED.
3. **`set_qa_status`** - immediately, and only now:
   - Pass -> `set_qa_status(qaItemId, "Passed")`
   - Fail -> `set_qa_status(qaItemId, "Failed", note: "observed vs expected ...")`
   - Blocked -> `set_qa_status(qaItemId, "Blocked", note: "reason")`

**A bare `"Passed"` means you exercised this item against the branch as it stands now.** That is the claim, and everything downstream reads it that way.

Some items genuinely cannot be driven - an import path nothing in the diff reaches, a guard settled by the query plan, a code path with no UI in front of it. For those, pass `passedByInspection: true` **with a note saying what you read and why the app could not be driven**. It is a real outcome and it does not block the hand-off; it is recorded and displayed separately because it is a weaker claim than an executed test, and the human signing this ticket off is entitled to know which of the two they are looking at. The note is required - a pass by inspection with no reasoning is refused.

Reach for it because the item cannot be driven, never because driving it is slow or because the diff looks unrelated. If you find yourself writing "nothing changed here since it last passed", that is the argument B3 tells you not to make: re-run the item.

**"The data for it does not exist" is not one of these reasons.** It is B3a, and it means go and make the data. The three cases above have in common that no amount of setup would help - the app has no screen for the path, or nothing you can do from outside reaches it. A case you could drive on a row that happens not to be there yet is a case you can drive, and recording it as read-only converts a half-hour of setup into a branch nobody has ever executed. Where the setup genuinely defeats you, say in the note what you tried to create and what stopped you - that sentence is what tells a reviewer whether they are looking at an unreachable path or an untested one.

**Record each outcome as you get it, never in a batch at the end.** An outcome you are holding in your head is invisible to anyone watching the board, and lost outright if the run is cut short - a QA pass that tested eleven of fourteen items and died has to start again from nothing if it recorded none of them. Write the note at the same time as the status, while you can still see what you observed; a `Failed` you come back to label later gets a vaguer note than the one you would have written at the time.

### B5. Capture QA shots - THE HAND-OFF WILL NOT GO THROUGH WITHOUT THIS

While the app is still open on the finished work, capture **3 to 6** screenshots that best show the completed feature working, and attach them with `set_qa_shots`. These are the at-a-glance visual proof of what you QA'd - not per-test-step captures, but the money shots of the delivered result. They are the only part of your run a reviewer takes in at a glance, and the thing refinement checks the ticket's record against.

- **Frame the subject before you shoot.** A screenshot captures the fold the browser is parked on, not the thing you are about to describe. Scroll the proof into view first, or pass `fullPage: true` when it spans more than a screen - a shot of a page header is not a shot of the table underneath it.
- Take each one with `browser_take_screenshot`, and set `filename` to an **absolute path inside the screenshot directory named in your prompt** (e.g. `<screenshot dir>/shot-1.png`). A bare `shot-1.png` would be written into the repo instead, dirtying the working tree - always give the full path.
- **Open every shot with the Read tool before you attach it**, and check that the thing your caption names is actually in the frame. A caption is a claim about the pixels and this is the only step that tests it - if the shot does not show what you meant, retake it rather than attaching it with a caption it does not support.
- Then call `set_qa_shots(ticketId, [{ imagePath, caption, focus }, ...])` **once** with the whole set - it replaces any previous shots. `imagePath` is the same absolute path you just captured to.
- `caption`: **2 sentences max**, plainly describing what the shot shows. Describe what is **in the frame**, not what you verified elsewhere in the run.
- `focus`: `{ x, y, w, h }`, the rectangle **in the screenshot's own pixels** that shows what the caption claims - the new row, the error message, the changed value. The ticket's walkthrough zooms straight to it, so a full-page shot of a one-line change is only useful with one. Measure it, do not eyeball it: before capturing, read the element's `getBoundingClientRect()` and `window.devicePixelRatio` with `browser_evaluate` and multiply through (add the scroll offset for a full-page capture). Pad it a little so the label beside the value is in it too. Leave `focus` out only when the whole frame is the point. A rectangle that misses the image is dropped and reported back to you - fix the numbers and call again.
- **Three is the floor, six is the ceiling.** Take three on any ticket with more than one screen worth of result, and keep going to six whenever another shot shows something the earlier ones do not - a second state, an edge case, an empty or error path, the before beside the after. Fewer than three only where the work genuinely is one screen. What does *not* earn a slot is a near-duplicate: three shots that each show a different thing are worth more to a reviewer than six of the same view, and anything past six is dropped on the way in.

**The call is not optional, even where the screenshots are.** `set_phase` to `NeedsRefinement` is **refused** until this ticket carries either shots or a recorded reason it has none. The one thing you cannot do is say nothing - and taking screenshots into the capture directory is not the same as attaching them, which is exactly how a ticket reaches a reviewer with an empty panel.

Some work genuinely has nothing to photograph: a stored-procedure or migration change, a build fix, an API change with no screen behind it. For those, call `set_qa_shots(ticketId, [], noShotsReason: "...")` with one sentence naming the kind of change and why no screen shows it. That escape hatch is narrow on purpose - if the work is visible **anywhere** in the app, screenshots are the right answer, and "I ran out of time" is not a reason. An empty list with no reason is rejected.

If QA failed or is blocked you are going back to implementation rather than handing off, so neither is required - skip this step.

### B6. Close the browser
Use `mcp__playwright__browser_close` when done. Screenshots belong in the screenshot directory from your prompt - never anywhere inside the repo, which would leave the working tree dirty and get them committed onto the ticket's branch.

---

# Hand off

- `add_comment` summarizing the run: what the code review covered, then the QA outcome (passed / failed / blocked, with details for non-passes) if you ran Part B.
- **Code-review-only ticket, review passed** -> `set_phase` to `NeedsRefinement`. Say in your comment that this ticket was code-review-only and no functional QA was run, so the reviewer is never left guessing why there are no screenshots. Nothing else is required of you: the shots requirement does not apply to these tickets.
- **Every item Passed on this run** -> `set_phase` to `NeedsRefinement`. If that call comes back refused it is telling you one of two things, and it names which: either step B5 never happened (do B5, then call `set_phase` again with the same phase), or the checklist is not finished - it lists the item ids still untested or carrying an expired pass. Go and run those. Do not go looking for a different phase to move to, and do not treat the refusal as a bug in the tool: it is the one part of this phase that cannot be talked out of.
- **Any Failed or Blocked, or a Part A finding that goes back** -> `set_phase` to `NeedsImplementation` (re-enters the pipeline so the failures get fixed). An acceptance criterion found unmet lands here too, even when every item you were handed passed: A3 makes it a `Failed` item, and it goes back rather than to the reviewer as a note.
- **You cannot tell what "correct" would be** -> `set_phase` to `Blocked`. This is the one outcome that is not a pass or a fail: the code disagreeing with the acceptance criteria is a **Failed** item and goes back to implementation, but acceptance criteria that disagree with *each other* - or with the description, or with a work item - leave you with no verdict to give. Say which two things conflict in the `reason` and ask the question that would settle it ("AC 2 wants the row hidden, AC 5 wants it exported - which does the grid do?"). Do not pick the reading you prefer and pass it: a confident pass on a requirement nobody had settled is worse than no QA at all. Record the outcomes you *did* get first (`set_qa_status` on each item you tested), so the next run starts from what you proved rather than from nothing. This route is available from any phase and does not count against the ticket.
