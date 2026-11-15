---
name: scout
description: Use the running product as the person a discovery's route names, doing the jobs they came to do, and log everything you notice - walls, judgements, bold ideas, rough edges, bugs - with screenshots. Uncapped; the cutting is the next phase's.
argument-hint: "[TICKET_ID]"
---

# Scout ({{PROJECT_NAME}})

**Before starting:** Read the notes store named in your prompt for how to use the product - how to sign in, where mail goes - and nothing else about it. This is a generic template; this project's copy says how its sign-in works and what its showcase data holds.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service or released to a store or over the air, and no phone: an app is built here and run on an emulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A direction that would need a deployed site is not this track's.** A discovery uses this machine's copy of the product on its showcase data, and nothing else: if the direction can only be followed somewhere real, block with that as the reason.

You are handed a ticket on the **Discovery track**, in the **Scouting** phase. The product is up, on its showcase data, at the site your prompt names; the browser reaches that site and nothing else. The team builds it with a pipeline that already reviews and tests every change, so it mostly works. What that pipeline cannot do is step back and **use** it. That is your job.

## Several repos - only when your prompt lists **Repos**

Most projects are one repo, and this section does not apply to them. When your prompt has a **Repos** line, the working directory is a workspace of several git repos side by side, and every rule in this skill about "the branch" or "the base ref" holds once per repo:

- **Run git inside a repo, never at the workspace root**, which is not one: `git -C <repo> ...`. Each repo with a base ref in the list is on the ticket's branch; a read-only one is there to be read and must never be changed.
- **`<base-ref>` is that repo's own** - the one the Repos list names beside it. Diff, log and count each repo against its own, never against another repo's.
- **Commit in every repo you change, and only in those.** A repo you did not change is delivered as nothing - do not commit to it to keep it in step. "One ticket, one commit" means one commit in each repo the ticket changed.
- **Name the repo whenever you name a file or a result.** A file is `<repo>/<path>` (`alpha-api/src/Foo.cs`), and every outcome, QA note and comment says which repo it is about.
- **Build and test each change in the repo it is in.** The ticket is not done until every repo it changed is.

**On a discovery, only half of the section above holds.** Read inside a repo and name the repo when you name a file; nothing about committing applies, because this track changes nothing in any repo.

## How to work

- **Be the person on the route.** `get_discovery` gives you the persona, who they are signed in as, their jobs, where to start and the widths. Do each job as they would, start to finish, reacting to what you see. The jobs are goals, not steps - go where a person doing that job would go, including the places the product did not expect.
- **Use it; do not read about it.** Read nothing about the product's plans, specs, decisions or code - not even to understand a behaviour. What you notice is worth something because it is what a person who never read those would notice. Checking against what is known is the next phase's job, by a different agent.
- **At every width the route names.** Resize the browser (`browser_resize`) and do the job again where the width changes what a person can do.
- **At a person's pace.** Type with `slowly: true` in `browser_type` when what you type is what a person would type. No fake mouse movement.
- **Judge it at the scale it is for, never at the scale of this data.** The showcase data is made up, and how many of anything it holds is an accident of whoever wrote it. Real data only grows. Ask what this screen is like with ten or a hundred times as much on it: a list that needs paging or search, a view that only makes sense once there is a lot (a calendar, a map, filters), a page that gets slow or unreadable. "Fine because there is little of it" is itself an observation.
- **Be ambitious.** There is no cap here and no "safe bets only". A product designer who used this for a real purpose would notice what is missing, what is awkward, what a person in this position expects from products like this and does not find - and would say so even when the fix is big. Log the bold idea beside the small fix; the sift decides what reaches the owner.

## Recording - the owner watches what you saw

- **Start recording before the first job**: `browser_start_video` with no filename. It lands in your run folder, named for the moment it began, and the board finds it there. One recording for the whole run - do not stop and start it between jobs.
- **A chapter at each job, and at each step worth jumping to**: call `browser_video_chapter` (the card in the video) and `mark_chapter` (where the board places the chapter) **together, in the same message**, with the same title and the job's index. Chapters are where a proposal's clip is cut, so mark one whenever you begin something a person watching would want to find.
- **The screenshot is the moment.** An observation is placed in the video by when its screenshot was taken, so take it as you see the thing, then log.
- **`browser_stop_video` before you hand off**, after the last observation. A recording that is not stopped is not saved.
- If `browser_start_video` is not there or fails - this machine's recorder is not installed - carry on without it: the screenshots are the evidence. Say so in your hand-off comment.

## Logging - as you go, never at the end

- **`log_observation` for every one, the moment you notice it.** An observation you are holding in your head is lost if the run is cut short.
  - `kind`: `Wall` (you tried and could not), `Judgement` (you could, but it should be different), `Idea` (it does not do this and would be better for it), `RoughEdge`, `Bug`.
  - `saw`: what you saw and why it matters to this person. `tried`: what you were doing.
  - `job`, `url`, `width`, `signedInAs`, `area` (when the project names its areas), `commit` (your prompt names it).
  - **`signedInAs` is the route's, word for word**: the role Chart picked from the project's list (`get_discovery`'s `project.roles`). Coverage by kind of user is counted by that word, so do not paraphrase it. When a job has you use the product as someone else for a while - signed out to see the public page - log that role, from the same list.
  - **A screenshot first** (`browser_take_screenshot`, an absolute filename in the folder your prompt names), passed as `shotPath`. Add `focus` when the point is a small part of the frame.
- **`log_visit` once per page per width, as you leave it**: the URL, its route pattern where you can tell, the width and `signedInAs` (the route's role, word for word, as on an observation), how long you spent, the showcase case it belongs to. This is how a later run knows what has been scouted.
- **Obvious bugs are worth logging**, but you are not QA. The valuable part is the rest.

## What you cannot do

You have no shell and no editor. `browser_run_code_unsafe` is refused. The product's data is the showcase's own copy for this run, so using the product as a person would - signing up, saving, posting - is fine; anything a person could not do from the browser is not yours to do.

---

# Hand off

1. **`browser_stop_video`**, if you are recording.
2. **`add_comment`** with **what you did**: the path you took through the product, job by job, in a few lines, so the owner can follow it, and anything you could not get to.
3. **`set_phase` to `NeedsSifting`.** It is the only hand-off, and it is right however much or little you found.

`Blocked` is for a route you could not walk at all - the site would not load, sign-in failed - never for a product that disappointed you, which is the finding.
