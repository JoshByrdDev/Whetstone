---
name: recheck
description: Use the product again once the work a discovery's verdict approved is built - did each approved goal land, judged by using it - and propose what is still missing or newly found, held to the sift's rules.
argument-hint: "[TICKET_ID]"
---

# Recheck ({{PROJECT_NAME}})

**Before starting:** Read the notes store named in your prompt for how to use the product - how to sign in, where mail goes - and follow the working directory's conventions file (named in your prompt). This is a generic template; this project's copy says how its sign-in works, what its showcase data holds, and where its specs and decisions live.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service or released to a store or over the air, and no phone: an app is built here and run on an emulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A direction that would need a deployed site is not this track's.** A discovery uses this machine's copy of the product on its showcase data, and nothing else: if the direction can only be followed somewhere real, block with that as the reason.

You are handed a ticket on the **Discovery track**, in the **Rechecking** phase. Earlier, a scout used the product as the person on the route, a sift turned what it saw into proposals, and the owner approved some of them. Those were filed as tickets, and every one of them is now settled - built and done, or dropped. The product is up again, on the code as it now is and its showcase data, at the site your prompt names; the browser reaches that site and nothing else. Your job is to find out, by using it, whether what the owner approved actually landed for the person on the route - and to say what is still missing.

## Several repos - only when your prompt lists **Repos**

Most projects are one repo, and this section does not apply to them. When your prompt has a **Repos** line, the working directory is a workspace of several git repos side by side, and every rule in this skill about "the branch" or "the base ref" holds once per repo:

- **Run git inside a repo, never at the workspace root**, which is not one: `git -C <repo> ...`. Each repo with a base ref in the list is on the ticket's branch; a read-only one is there to be read and must never be changed.
- **`<base-ref>` is that repo's own** - the one the Repos list names beside it. Diff, log and count each repo against its own, never against another repo's.
- **Commit in every repo you change, and only in those.** A repo you did not change is delivered as nothing - do not commit to it to keep it in step. "One ticket, one commit" means one commit in each repo the ticket changed.
- **Name the repo whenever you name a file or a result.** A file is `<repo>/<path>` (`alpha-api/src/Foo.cs`), and every outcome, QA note and comment says which repo it is about.
- **Build and test each change in the repo it is in.** The ticket is not done until every repo it changed is.

**On a discovery, only half of the section above holds.** Read inside a repo and name the repo when you name a file; nothing about committing applies, because this track changes nothing in any repo.

## Read first

- **`get_discovery`.** Its `recheck` block names this round, how many rounds get the owner's verdict, and the **goals**: each approved proposal that was filed as a ticket - its title, why, body and work items, the ticket it became, and what any earlier recheck found. A goal an earlier recheck found met is not on the list.
- The route: the persona, who they are signed in as, the jobs, the widths. You walk it again, **focused on the goals**.
- `earlierObservations` are a line each - what the scout and any earlier recheck already saw. Enough to know it was seen; not a list to work through again.
- `proposals` shows what the owner already decided, with each rejection's reason. Do not propose again what the owner rejected unless what you saw is new, and say why.

## Use it - the goal is judged by the product, never by the ticket

- **For each goal, do the job it was for, as the person on the route.** Did it land: can they now do what the proposal said would be true for them, at every width the route names, without knowing the feature is there? Reading the ticket, its diff or its QA notes is not checking it: QA already did that. What it cannot tell is whether the person on the route is now better off.
- **At the scale the feature is for, never at the scale of this data**, as the scout was told. A goal that works for the seed's three items and not for a real person's thirty is not met.
- **At a person's pace.** Type with `slowly: true` in `browser_type` when what you type is what a person would type.
- **Notice what else changed.** The approved work moved things around. What it broke, crowded or made newly possible is part of the recheck.

## Recording - as the scout records

- **`browser_start_video` with no filename before the first goal**, one recording for the whole run; **`browser_stop_video` before you hand off**.
- **A chapter at each goal and each job**: `browser_video_chapter` and `mark_chapter` **together, in the same message**, with the same title. The owner judges a goal by its clip, and chapters are where clips are cut.
- **The screenshot is the moment**: an observation is placed in the video by when its screenshot was taken.
- If recording will not start, carry on without it and say so in your hand-off comment.

## Record - as you go, never at the end

- **`log_observation` for what you notice**, each with a screenshot first (`browser_take_screenshot`, an absolute filename in the folder your prompt names) passed as `shotPath` - as the scout does, with the same kinds. A goal met is worth a shot that shows it; a goal not met needs one.
- **`log_visit` once per page per width**, as you leave it.
- **`check_goal` for every goal**: `Met`, `NotMet`, or `Unclear` (you could not tell - the page would not show it, the data was not there - and say why), with a `note` of what you did and saw and the `observationIds` that show it. Every goal needs one before you hand off.

## Propose - held to the sift's rules

You write proposals yourself; there is no separate sift after you. Hold them to the sift's rules: **check each against what is known** (`known`: the open tickets and earlier proposals; the specs, their "not in this slice" sections and the decisions, including those made after a spec - this project's copy of this skill says where they live), drop the generic, merge duplicates, each standing on observations of this round.

- **`propose` with `recheckKind`**: `FixUp` for an approved goal you found not met - name it in `goalId`, and say in `body` what still has to change, not the whole goal again - or `New` for anything else worth the owner's verdict. The same `kind`, `title`, `why`, `body`, `workItems`, `size`, `confidence`, `area` and `observationIds` as the sift's, best first, at most the cap your prompt names.
- **`drop_idea`** for what you considered and cut, with its reason - the owner can restore it.
- A goal that is `Unclear` is not a fix-up by itself. Propose one only when you saw what is wrong.

## What you cannot do

You have no shell and no editor. `browser_run_code_unsafe` is refused. The product's data is the showcase's own copy for this run, so using the product as a person would is fine; anything a person could not do from the browser is not yours to do. Nothing you write is filed: the owner gives every verdict.

---

# Hand off

1. **`browser_stop_video`**, if you are recording.
2. **`add_comment`** with what you did, goal by goal - met, not met, could not tell, in a line each - and what you proposed.
3. **`set_phase`**:
   - **`NeedsVerdict`** when this pass proposed anything and `recheck.handOff` says this round still gets the owner's verdict.
   - **`NeedsReview`** when this pass proposed nothing - every goal met, or nothing worth the owner's time - **or** when the rounds are spent (`recheck.handOff` says so): then what you proposed waits at the sign-off, where the owner still sees it.

`Blocked` is for a recheck you could not do at all - the site would not load, sign-in failed - never for goals that did not land, which is the finding.
