---
name: refine
description: Final pass on a QA-passed ticket - squash the branch into one well-written commit, check the ticket's own record (work items, QA items, QA shots) holds up, leave the reviewer a short brief on the part of the domain it touched, then move to NeedsReview (or back for rework)
argument-hint: "[TICKET_ID]"
---

# Refine a Local Ticket ({{PROJECT_NAME}})

**Before starting:** Read what this repo already knows - the notes store named in your prompt - and follow the working directory's conventions file (named in your prompt). This is a generic template - defer to that file for the base branch and any project-specific rules.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service or released to a store or over the air, and no phone: an app is built here and run on an emulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A ticket that asks for work on a deployed environment is not blocked and not refused - it is prepared.** Running it is a human's job; yours is the investigation and the artifact they run it with: the exact statements, proven locally against the local database, with a rollback and a verification step, committed to this branch, and a runbook `add_comment` saying what the human runs, against what, in what order, what they should see, and how to undo it. That is a normal successful outcome of this ticket, not a lesser one. Your prompt's hard rules state this in full; what this phase owes it is below.

You are handed a local ticket in the **Refining** phase. It has already been implemented and it has already passed QA. Refinement is a pass over **the ticket and its commits**, not a second pass over the code. You have two jobs, in this order:

1. **Turn the branch into one commit with a message worth keeping in the repo's history.**
2. **Check the ticket's own record holds together** - work items, QA items, QA shots, description - and that the diff plausibly delivers what was asked.

Communicate via `add_comment` on the ticket (the `tickets` MCP server), never the terminal.

## What refinement is NOT

This is the phase that goes wrong by doing too much. QA has already driven the app and signed off on every QA item; repeating that work costs the whole cycle and leaves the commit history - the thing only this phase does - untouched.

**You do not change what the code does. At all.** Not a one-line fix. Not a defect QA wrote up and passed anyway. Not something you are certain about and could prove in two minutes. Diff size is not the test: a one-character SQL change is a behaviour change, and behaviour changes belong to implementation because they need a build and a QA pass standing behind them - which is exactly what this phase does not run. Your own edits stop at text no runtime ever reads: a stale comment, a leftover debug line, a stray file.

So:

- **Do not start the app, the dev server, docker, or the database.**
- **Do not query the database** (`sqlcmd`, `psql`, or any client) to verify behaviour.
- **Do not open a browser or replay QA steps.**
- **Do not run the test suite or a full build** to re-prove the work. The tree-hash check in step 2 is what guarantees your squash changed nothing.
- **Do not re-derive whether the feature works** by reading the implementation line by line, tracing call chains, or reasoning through the logic from scratch. QA's `Passed` verdict is the evidence that it works; trust it unless the ticket's own record contradicts it - or an independent analysis's finding questions it, which step 3b handles. On a code-review-only ticket the evidence is QA's review comment instead, and it is trusted the same way - the absence of a test run is not an invitation for you to become one.
- **Do not re-implement.** Real gaps are handed to another phase - see the routing below.
- **Do not run the ticket's own deliverable.** Where this ticket produced a script for a human to run on a deployed environment, QA has already executed it locally and reported its counts. Re-running it here is the same mistake as re-querying the database, with a sharper edge: the artifact is written to *change data*, so a run that "just confirms it still works" mutates the very state QA's evidence was recorded against. Read it as text, like any other file in the diff.

Reaching for any of those tools is a symptom, not the mistake itself. Nobody opens `sqlcmd` in refinement for its own sake; you open it because you have already decided to fix or re-verify something and now need to prove it. **The decision is the thing to catch.** The moment you think "this is small, I'll just fix it" - that is the drift, and everything after it follows logically from a wrong premise.

You **do** read the branch diff - once, as a skim, looking for the things that survive a passing QA run: debug leftovers, commented-out code, stale or wrong comments, TODOs, stray files. That level of reading is welcome. Tracing business logic is not.

**One exception, and it is about intent rather than depth.** Step 5 asks you to leave the human a short brief on the part of the system this ticket touched, and writing that honestly means understanding the area - so you may open source files to work out what this part of the system is for and how it behaved before. **Reading to explain is allowed; reading to verify or to fix is not.** Everything above still holds: no app, no database, no browser, no change to anything a runtime reads, and anything you notice while reading gets routed through the table below like any other finding rather than acted on. Budget it in files, not hours.

## Where findings go

You are never stuck holding a problem. Every finding has a phase that owns it, and handing it over IS the work - not a failure to do your job, and not a lesser outcome than fixing it yourself. A ticket that goes back and returns clean costs one extra cycle; a behaviour change smuggled in during refinement was never built, never reviewed and never tested by anyone.

| What you found | Where it goes |
| --- | --- |
| Anything that changes what the code does at runtime - a bug, a one-line fix, a missing branch, a stub, an unaddressed requirement, a defect QA noted and passed anyway | `set_phase` **NeedsImplementation**. It returns through QA and refinement automatically. |
| The code looks right but its testing does not - a requirement with no QA item against it, a QA item that passed without really exercising the thing, missing or meaningless QA shots | `add_qa_items` naming the missing cases, then `set_phase` **NeedsQa**. Use this when nothing needs to be *written*, only checked - it costs a QA pass instead of a full implementation cycle. **Never on a code-review-only ticket** - see below. |
| The requirements themselves moved, or the plan no longer matches what is being asked for | `request_respeculation`. |
| A comment, a debug line, a stray file - text no runtime reads | Fix it yourself, `git commit --amend` (step 3). |
| Behaviour a user sees that the ticket never asked for, and no `expansion` work item covers | Lead your hand-off comment with it under **Scope expansion**, before anything else, then carry on to **NeedsReview**. Never as one item in a list of notes. |
| A question, a trade-off, or something a human should weigh but the work is finishable as it stands | `add_comment` and carry on to **NeedsReview**. Writing it up for the reviewer is a real outcome. |
| The ticket contradicts itself - the acceptance criteria disagree with the description, the work items or each other - so you cannot tell whether the branch satisfies it | `add_comment` with the detail, `set_phase` **Blocked**, with the two conflicting things named in the `reason`. Different from the row above: there, the work is done and a human gets an extra note; here, nobody can say whether it is done. |
| The repo or worktree is in a state you cannot safely refine | `add_comment` with the detail, `set_phase` **Blocked**. |

Say what you found in an `add_comment` either way. A finding you fixed silently teaches the pipeline nothing; a finding you routed with a clear description is what lets the next phase act on it without rediscovering it.

Rule of thumb: if you are writing a SQL query, waiting on a container, or three files deep in a call chain, you have drifted into QA's job. Stop, pick the row above that matches what you found, and hand it over. Refinement is minutes of work.

## Several repos - only when your prompt lists **Repos**

Most projects are one repo, and this section does not apply to them. When your prompt has a **Repos** line, the working directory is a workspace of several git repos side by side, and every rule in this skill about "the branch" or "the base ref" holds once per repo:

- **Run git inside a repo, never at the workspace root**, which is not one: `git -C <repo> ...`. Each repo with a base ref in the list is on the ticket's branch; a read-only one is there to be read and must never be changed.
- **`<base-ref>` is that repo's own** - the one the Repos list names beside it. Diff, log and count each repo against its own, never against another repo's.
- **Commit in every repo you change, and only in those.** A repo you did not change is delivered as nothing - do not commit to it to keep it in step. "One ticket, one commit" means one commit in each repo the ticket changed.
- **Name the repo whenever you name a file or a result.** A file is `<repo>/<path>` (`alpha-api/src/Foo.cs`), and every outcome, QA note and comment says which repo it is about.
- **Build and test each change in the repo it is in.** The ticket is not done until every repo it changed is.

## Ticket state - use the `tickets` MCP tools

- `get_ticket` to read the ticket: its `description`, `workItems` (should be `done`), `qaItems` (should be `Passed`, with notes), `qaShots` (the captioned screenshots QA attached), `implementationSummary`, and any linked `issueKey`.
- **Check `skipQaTesting` before you judge the QA record.** On a ticket where it is `true`, QA reviewed the diff and deliberately never ran the app: **no QA items and no QA shots is the correct, complete record**, not a gap. Sending such a ticket back to `NeedsQa` for the testing it was explicitly excused from is a loop it cannot get out of - QA will read the diff again and hand it straight back. Everything else about your job is unchanged: the squash, the brief, and routing a real defect to `NeedsImplementation` all apply exactly as usual. If you think the flag was the wrong call for this work, say so in `add_comment` for the human and carry on to `NeedsReview`; it is theirs to change, not yours.
- **Read the attachments.** `get_ticket` returns `attachments` - the linked issue's attachments already downloaded to this machine - and an `attachmentsNote` saying what that list does and does not cover. Each `localPath` is a real file; open it with the Read tool. Refinement is where "we chose this rather than derived it" gets written down for a human, and a design PDF or annotated mockup is usually the thing that would have let you derive it. Do not hand that sentence over about something you could have opened.
- If `issueKey` is set, call `get_issue_context` (READ-ONLY) to read the original acceptance criteria. This is the source of truth for "what was actually asked." Call it too whenever `attachmentsNote` says something is not downloaded yet.
- `get_ticket` also returns `nextPhases`, each with a `meaning` describing what that hand-off is for and, where the hand-off demands something of the ticket, a `requirement` naming it. Read them before choosing; they are the authoritative version of the routing table above.
- Load `add_qa_items` alongside `add_comment` and `set_phase` when you select your tools. You need it for the NeedsQa route, and a worker that did not load it tends to talk itself into a route it can already reach.
- **Load `rule_on_finding` in the same call when `get_ticket` returns an `independentReview` with findings.** Like the brief, the move to NeedsReview is refused until every one is ruled on - see step 3b.
- **Load `set_learning_brief` in the same call.** The move to NeedsReview is refused until this ticket carries a brief written on this pass, so a run that did not load the tool is a run that cannot finish. Load it now, not when you get there.

## Workflow

### 1. Confirm your workspace
Your working directory is a git **worktree** that belongs to this ticket alone, already checked out on its feature branch by the heartbeat. Verify it before you rewrite any history:
- `git rev-parse --abbrev-ref HEAD` should report the ticket's recorded `branch`. If it does not, `add_comment` with what you actually got and `set_phase` to `Blocked`, then stop.
- `git status --porcelain` must be empty. If it is not, `set_phase` to `Blocked` (something upstream left the tree dirty) and stop.

Do **not** run `git checkout`, `git switch`, or create a branch. Other checkouts of this same repo exist - the human owner's, and the worktrees of other tickets.

### 2. Tidy the commit history - THE MAIN JOB (local only, never push)

**Do this first, every time.** It is not optional and it is not conditional on the history "looking messy". A branch reaches this phase as the implementation commit plus a trail of fix-ups from QA bounces - each one a fix to the commit before it, none of them meaningful to anyone reading the history later. Those belong folded into the work they fix. The rule for this pipeline is **one ticket, one commit** - on a workspace of several repos, one commit in each repo the ticket changed, with every step below run once per repo against that repo's own `<base-ref>`.

It goes first because it is the one thing no other phase does. If the run is cut short after this step, the branch is still in good shape.

**Measure against the base ref named in your prompt - `<base-ref>` below - and no other.** It is the ref your worktree was forked from, and the heartbeat squashes and rebases against the same one after you. Which ref it is depends on how the project delivers (`<base>` is its base/integration branch per the conventions file). Where work leaves by pull request it is `origin/<base>`: the local `<base>` lives in the human's own checkout and is usually behind. Where finished tickets are merged into the local `<base>` and nothing is pushed, it is that local `<base>`: every merged ticket is there and origin has none of them. Pick the wrong one and other people's commits are inside your squash range - squash that and you destroy merged work, and the tree-hash check will not save you, because `reset --soft` preserves the tree no matter how much history it swallows.

**Then check this branch has not already merged through a pull request.** A ticket can sit in this pipeline while the human pushes it and opens a PR, and that PR can complete while later rounds are still running. When it does, nothing local moves: HEAD still sits on the old fork point, so `git merge-base HEAD <base-ref>` still returns it and the range below still contains every commit of the already-merged feature. Squash that and you get one commit that re-adds the entire feature on top of a base that already has it, on a branch that now reads as if none of it had ever landed. The tree-hash check will not save you here either - the tree is preserved exactly; it is the base underneath it that is wrong.

```console
git fetch origin
git rev-parse --abbrev-ref HEAD                              # this ticket's branch, <branch> below
git rev-parse --verify origin/<branch>                       # fails = nothing published yet
git merge-base --is-ancestor origin/<branch> <base-ref>       # exit 0 = its PR has completed
```

- **`origin/<branch>` does not exist** -> nothing of this ticket is published. Carry on; the squash below is safe.
- **It exists and the last check exits 0** -> the branch has merged into `<base>` mid-flight. **Stop before touching the history.** `add_comment` saying so, naming the commits on HEAD that are not in `<base>` and the files they touch, then `set_phase` to `Blocked`. Do not squash, do not rebase, do not reset: whether the follow-up work belongs on a fresh branch cut from `<base>` or on a force-pushed old one is the human's call, and squashing first buries the evidence they need to make it.
- **It exists and the last check exits 1** -> published but not merged, so there is probably a live PR built on those commits. Squash as normal, but say in your hand-off comment that the branch now needs a force-push, so the human does not review or merge the stale PR by mistake.

**A ticket merged locally and then sent back needs no such check.** Where `<base-ref>` is the local `<base>`, whatever of this ticket was merged is already in it, so `<base-ref>..HEAD` lists only the commits made since and the squash below cannot reach the rest. Squash those as normal - the result merges as a follow-up on top of the earlier merge - and say in your hand-off comment that this is a second merge of the same ticket.

Then run `git log --oneline <base-ref>..HEAD` and look at what is actually there:

- **More than one commit** -> squash. Non-interactively:
  ```console
  git rev-parse HEAD^{tree}                     # note this
  git reset --soft $(git merge-base HEAD <base-ref>)
  git commit
  git rev-parse HEAD^{tree}                     # must be identical
  ```
  The two tree hashes **must match** - that is your proof the squash preserved the work exactly, and it is the reason you do not need to re-run a build. If they differ, something went wrong: `add_comment` with the detail and `set_phase` to `Blocked`.
  Only ever squash the commits belonging to THIS ticket (everything since the branch diverged from `<base>`); never touch commits already on `<base>`.
- **Exactly one commit** -> nothing to squash, but still do the message check below.

**Then write the message.** This is what lands in the repo's history for good, and it must describe **the whole branch diff against `<base>`** - the finished state - not the sequence of commits that produced it, and not the sequence of rounds this pipeline took to get there:

- Subject: `<TICKET-KEY>: <what changed>`, imperative, no trailing full stop. Match the subjects already in this repo's history (`git log <base-ref> --oneline`).
- Body: what the change does and **why**, as one coherent description. A reader six months from now has the diff already; what they need is the reason and the shape of the solution. The ticket's `implementationSummary` is a good starting point - but it was written to describe a ticket, and you are writing for the repo's history, so rework it rather than pasting it.
- **Format it so it can be skimmed.** A lead paragraph saying what this delivers, then bullets for the specifics (approach, notable fixes, what it touches). Wrap at ~72 columns and indent wrapped bullet lines by two spaces. One dense block of prose is technically complete and practically unread - find a couple of the better-written commits already on `<base>` and follow their shape.
- **Never a list of the commits you squashed.** "Fix casing in Postman assertions" describes a detour taken on the way; after squashing, that detour does not exist, and naming it only tells the reader about work they cannot see.
- **Nothing about how the work was produced.** No rounds, passes, phases, attempts, QA bounces, reviews or re-plans. No "a follow-up round then fixed...", "initially", "later", "after review", "on a second pass" - nothing that implies the ticket went back and forth before it landed. This is the mistake that survives the rule above, because it does not look like a commit list: it is ordinary prose that happens to narrate our pipeline. **"A follow-up round then fixed X"** tells the reader nothing about the change and one thing about us, which is that QA failed this ticket and it went back to implementation. That is internal to this system and none of a reviewer's business. Cut the clause and lead with the verb: **"Fixed X"**.
- **A fix to something this branch itself introduced is not a bullet at all.** It is invisible in the finished state - a test assertion added and then corrected within the branch is simply an assertion that passes, and nobody diffing against `<base>` will ever see the broken version. Only a fix to something that was already on `<base>` is a change worth naming.
- No "oops", "address QA feedback", "wip" residue absorbed from fix-up commits.
- **No attribution trailer.** The squashed message ends on your last bullet. Never a `Co-Authored-By: Claude ...` line, never a "Generated with" line, never any mention of Claude, Anthropic, Codex, Gemini or a model - however strongly your own CLI suggests one. Squashing is where this slips in most, because the message is written fresh. `git log -1 --format=%B` and read the tail before you hand off.
- If the message falls short, rewrite it: `git commit --amend`.

**Never run `git push`.** The workflow is entirely local.

> The heartbeat enforces one-commit-per-ticket after this phase: if you leave more than one, it squashes them and uses the ticket's implementation summary as the body, then notes on the ticket that it had to. That fallback keeps the history clean; it does not make the message right. Write it yourself.

### 3. Check the ticket's record holds up

A paperwork check on the ticket, not a re-test of the app. You are asking "does this ticket read like finished, honestly-recorded work?" - and the material for that is the ticket, its QA record, and a skim of the diff:

- **Work items:** every one marked `done`, and nothing in the diff left as a stub, an empty method, or a TODO where a work item claims completion. *A stub behind a done work item -> **NeedsImplementation**.*
- **QA items:** every one `Passed`. Read them as a set against the description and acceptance criteria - do they actually cover what was asked, or does a requirement have no QA item pointing at it? Read the notes too, not just the statuses: a `Passed` item whose note describes something going wrong is the single most common way a real defect reaches this phase. Do **not** re-run the ones that passed. *Missing or hollow coverage -> **NeedsQa**. A defect described in a note -> **NeedsImplementation**.*
- **QA shots:** the ticket should carry 3-6 captioned screenshots showing the delivered result, and fewer than three only where the work genuinely is one screen. Check they exist and that the captions describe this ticket's work rather than something generic. *No shots on visible UI work, or captions that say nothing -> **NeedsQa**.*
- **Description / acceptance criteria:** skim the diff and satisfy yourself each one is addressed somewhere. Absence is what you are looking for - a requirement with no corresponding change - not a proof of correctness for the ones that are there. *An unaddressed requirement -> **NeedsImplementation**.*
- **Test data that would ship:** a fixture, seed or sample row added in a migration, a seed script, or anything else that runs when this code is deployed. Find what keeps it out of production - the tag, profile or environment check this repo's other fixtures sit behind - and confirm it in the diff. This is a reading check and has to be: every run QA made was against a development database, so an ungated fixture passes QA exactly like a gated one. A guard in the script that skips when its data is missing is not a gate. The reverse is also wrong - a schema change or reference data production needs must not be gated. *Test data with nothing stopping it reaching production, or a real change hidden behind the gate -> **NeedsImplementation**, naming the file and the gate.*
- **Leftovers in the diff:** debug output, commented-out code, dead code, stale or misleading comments, stray files (screenshots, logs, `.env`, editor droppings) that should never have been committed. *Yours to fix, below - unless removing it changes behaviour, in which case it is code, not a leftover.*

Text-only tidy-ups you may make yourself: a stale comment, a leftover debug line, a stray file. Fix it, then fold it into the single commit with **`git commit --amend`**. Never plain `git commit` in this phase - once step 2 has left the branch at one commit, a bare `git commit` silently makes it two again, and every later step reads as if the history were still clean. If the amend changes the tree (it will - you changed a file), that is expected here; the tree-hash check belongs to the squash in step 2, not to this.

Anything that is not text-only is not yours to fix, however small the edit would be - route it with the table above. That includes the case that most often catches this phase out: QA writing up a defect in its notes and passing the ticket anyway. A defect QA described is still a defect nobody fixed, and the fact that it is already diagnosed makes fixing it feel free. It is not - it is a behaviour change, and it goes to **NeedsImplementation** with QA's write-up quoted so implementation does not have to find it again.

### 3b. Rule on the independent analysis - AN EXIT GATE, when there is one

If `get_ticket` returns `independentReview`, a separate Classroom analysis read this commit before you were handed it. It is asked different questions from every phase before you - how the system worked before, the route a user takes, each requirement line by line - and its findings are its `risk` callouts plus the requirements its coverage map found unaddressed or only partly met. Every phase before you checked the work against the plan; these are what that reading was positioned to miss. The analysis can also be wrong, which is why you rule rather than obey.

For each finding, open what it cites, check the claim against the code, and call `rule_on_finding`:

- **Defect** - real, and this ticket's to fix. Route it like any other defect: **NeedsImplementation**, with the finding quoted and what to change.
- **NotADefect** - with the evidence that settles it: the line that already handles it, the requirement that does not ask for it, the sibling ticket that owns it. "The plan says so" is not evidence; the plan is what the finding is questioning.

This is the one place refinement reads code to verify rather than to explain, and it is scoped to the finding: the lines it points at and what they depend on, not the feature. Nothing else in **What refinement is NOT** moves - no app, no database, no fix of your own. `set_phase` to NeedsReview is refused while any finding is unruled or ruled Defect. No `independentReview`, or one with no findings, means there is nothing to do here.

### 4. Re-check the history before you hand off - THE EXIT GATE

Step 2 ran near the start of this phase, and everything since then could have undone it. Run this again now, immediately before `set_phase`, every time - including when step 2 found only one commit and you thought you were done with history:

```console
git log --oneline <base-ref>..HEAD
```

**Exactly one commit, with a message you wrote, or you are not finished** - in each repo the ticket changed, on a workspace of several. If there is more than one, collapse it now with the step 2 recipe (or `git reset --soft HEAD~1 && git commit --amend` for the common two-commit case) before doing anything else. If there is one commit but its message is machine-written - a bare subject with no body, or a subject truncated with a trailing `...` - the heartbeat wrote it as a safety net for a phase that forgot to commit, and replacing it is your job: `git commit --amend`.

Do not `set_phase` until this check passes.

### 5. Write the learning brief - THE OTHER EXIT GATE

**Only on the clean hand-off to NeedsReview.** By now you know which row of **Where findings go** you are taking. If the ticket is going back for rework, skip this step entirely and go to step 6 - the existing brief stays untouched and gets rewritten when the ticket comes back through. Never write a brief describing work that is about to change.

On the clean hand-off it is not a step you can drop: **`set_phase` to NeedsReview is refused until this ticket carries a brief written on this pass.** A brief left over from an earlier pass does not count either - if the ticket bounced and came back, the old one describes a solution that no longer exists. Nothing else in the pipeline can write this for you, because nothing else read the diff: the safety nets that squash your commits and rebase your branch have no equivalent here, which is precisely why this one is a locked door rather than a request.

The person who signs this ticket off did not write any of it, and did not watch it being written. Left alone, that is how someone stops understanding their own codebase. This brief is the counterweight: a few hundred words that leave them knowing something about {{PROJECT_NAME}} they did not know before.

It is not the commit message (what changed, for the repo) and not the implementation summary (what was delivered). It is **what someone would have needed to know to make this change themselves.**

Call `set_learning_brief` with 350-600 words of markdown, in five short sections:

1. **The area** - which part of the system this lives in, in domain terms, what it is for, and where the code sits.
2. **How it worked before** - the rule or behaviour that was in place. This is the section that makes everything after it legible, and the one most often skipped.
3. **What changed, and why** - the business reason, tied to the acceptance criteria rather than to the diff.
4. **Why this way** - an alternative that was considered and the constraint that ruled it out. If the ticket's record genuinely shows no alternative was weighed, say instead what constraint forced the shape of the solution. Do not invent a debate that never happened.
5. **Where to look** - 3-5 `file:line` pointers ordered as a reading path ("start here, then here"), plus `[[note-name]]` links to any notes in this repo's store that already cover this area.

Close with one question worth sitting with - something this change raises but does not answer.

What decides whether it gets read:

- **Concrete or nothing.** Name real files, real rules, real numbers, real edge cases. A brief that would read the same on a different ticket is worse than no brief: it costs the reader time and teaches them to skip the next one.
- **Assume the notes are read.** Where this repo's notes store already explains the area, link it and write only the delta. Do not re-explain the same subsystem from scratch on every ticket that touches it.
- **Say what would have caught you out.** The thing that surprises a competent reader is the highest-value sentence in the brief.
- **Plain language, skimmable.** Short sections, short paragraphs. The reader knows this codebase in general and this corner of it barely.
- **This is not a knowledge store.** A durable fact about the repo belongs in the notes store through the normal route; the brief only cites it.

Your material: the ticket's description and acceptance criteria (what was asked), its `implementationSummary` and QA record (what happened), the branch diff you have already skimmed, this repo's notes store, and - within the read-to-explain limit above - the source around the change.

### 6. Decide

Pick the row from **Where findings go** that matches the most serious thing you found, and hand the ticket to that phase. Whichever you pick, squash first - a branch going back for rework should still be tidy - and `add_comment` describing what you found in enough detail that the receiving phase does not have to rediscover it.

- **Clean, or only things a human should weigh** -> `add_comment` summarizing what you did: how many commits you squashed and the subject you landed on, what you checked on the ticket record, and any trade-off or open question you are deliberately leaving to the reviewer. Then `set_phase` to `NeedsReview`. This is the human's final sign-off before Done - it is never skipped, so never move a ticket to `Done` yourself. If that call comes back refused, it is telling you step 5 or step 3b never happened - the refusal says which: write the brief, then call `set_phase` again with the same phase. It is still the right hand-off - do not go looking for a different one.
- **Something needs writing** - a bug, a stub, an unaddressed requirement, a defect QA noted and passed anyway -> `add_comment` naming it specifically, then `set_phase` to `NeedsImplementation`. It passes back through QA and refinement automatically.
- **Something needs testing, but nothing needs writing** - a requirement with no QA item against it, a QA item that passed without exercising the thing, missing or generic QA shots - **and this ticket is not code-review-only** -> **`add_qa_items` first**, one per thing that went unverified, each worded as what a tester should actually do and assert. Then `add_comment` explaining why they were added, and `set_phase` to `NeedsQa`. Seeding the items is not optional: QA skips items that are already `Passed` and runs only `Todo` ones, so a ticket sent back with nothing new to run is a wasted cycle that returns unchanged. Do not route this through implementation; there is nothing there to implement.
- **The requirements moved under the ticket** -> `request_respeculation`.

- **You cannot tell whether the ticket is satisfied**, because it contradicts itself - the acceptance criteria disagree with each other, or with the description, or with the work items -> `add_comment` with the detail and `set_phase` to `Blocked`, naming the conflict and the question that would settle it. Sign-off is the one thing you must not fake: a `NeedsReview` hand-off says "as far as this pipeline can tell, this is finished", and a ticket whose own requirements disagree cannot honestly carry that. If the requirements *moved* instead of having always disagreed, use `request_respeculation`.

If the repo is in a bad state you can't safely refine, `add_comment` with the details and `set_phase` to `Blocked`, then stop. Either way the block reason is answered by a person and quoted back to the next run, so write the question, not just the symptom.

Handing off is not a bad outcome. The failure mode this phase has is a worker that finds something, decides the hand-off is too much ceremony for how small the fix looks, and fixes it - leaving a change that nothing in the pipeline ever built, reviewed or tested. Every route above is cheaper than that.
