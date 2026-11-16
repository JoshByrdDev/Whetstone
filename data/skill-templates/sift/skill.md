---
name: sift
description: A fresh pair of eyes on what a scout logged - check every observation against what is already known, drop the generic and the decided, merge, rank, and write the few proposals worth the owner's verdict, with evidence.
argument-hint: "[TICKET_ID]"
---

# Sift a discovery ({{PROJECT_NAME}})

**Before starting:** Read what this repo already knows - the notes store named in your prompt - and follow the working directory's conventions file (named in your prompt). This is a generic template; this project's copy says where its specs, its plan and its decisions live.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service (Expo's or Apple's) or released to a store or over the air, and no phone: an app is built here and run on an emulator or simulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A direction that would need a deployed site is not this track's.** A discovery uses this machine's copy of the product on its showcase data, and nothing else: if the direction can only be followed somewhere real, block with that as the reason.

You are handed a ticket on the **Discovery track**, in the **Sifting** phase. A scout has used the product as the person on the route and logged everything it noticed, uncapped. You did not explore, on purpose: you have no attachment to its ideas. Your job is the critic's - to turn a long list of observations into the short list of proposals the owner would actually want to build, each standing on what was seen.

## Several repos - only when your prompt lists **Repos**

Most projects are one repo, and this section does not apply to them. When your prompt has a **Repos** line, the working directory is a workspace of several git repos side by side, and every rule in this skill about "the branch" or "the base ref" holds once per repo:

- **Run git inside a repo, never at the workspace root**, which is not one: `git -C <repo> ...`. Each repo with a base ref in the list is on the ticket's branch; a read-only one is there to be read and must never be changed.
- **`<base-ref>` is that repo's own** - the one the Repos list names beside it. Diff, log and count each repo against its own, never against another repo's.
- **Commit in every repo you change, and only in those.** A repo you did not change is delivered as nothing - do not commit to it to keep it in step. "One ticket, one commit" means one commit in each repo the ticket changed.
- **Name the repo whenever you name a file or a result.** A file is `<repo>/<path>` (`alpha-api/src/Foo.cs`), and every outcome, QA note and comment says which repo it is about.
- **Build and test each change in the repo it is in.** The ticket is not done until every repo it changed is.

**On a discovery, only half of the section above holds.** Read inside a repo and name the repo when you name a file; nothing about committing applies, because this track changes nothing in any repo.

## Read

- `get_discovery`: the route, every observation with its screenshot path, the pages visited, and `known` - the project's open tickets and earlier discoveries' proposals with what the owner said about them.
- **`taste`** in `get_discovery`: the project's taste file - what this owner's verdicts have taught about what they want proposed, folded every few verdicts. **The owner's** section is their own words and outranks **Learned**. Empty means no verdicts are folded yet: judge by the rules here alone. Which version you read is recorded with your run.
- **Look at the screenshots** (open each with Read). An observation whose shot does not show what it claims is weaker than it reads.
- **What the product is meant to be**, in this worktree: the plan, the specs and each spec's "not in this slice" section, and **the decisions, including those made after a spec** - this project's copy of this skill says where each lives. The pilot's one wrong proposal in twenty was wrong for exactly this reason: it read a spec without the decision that came after it.
- You may read the code to understand a behaviour. It does not replace what the scout saw.

## Cut

Take every observation to an outcome - kept in a proposal (`observationIds`) or dropped (`drop_idea`), never left over.

- **Drop the generic.** "Add search", "add onboarding tooltips" - anything that could be said of any product is not a proposal.
- **Drop the decided and the planned**, naming which: the decision's number, the slice that already has it, the open ticket that already asks for it. A snoozed proposal that is due (`known.earlierProposals`, `snoozeDue`) may come back if this run saw it again.
- **Do not re-propose what the owner rejected**, unless what was seen is new and you say why.
- **Apply the taste.** What it says this owner does not want is dropped - "against the taste file", and which line - unless this case is different, and then `why` says how. What it says they want ranks up. It is a guide to taste, not a check: it never excuses skipping the specs and decisions above, and never outranks a decision.
- **Merge duplicates** - several observations of one problem are one proposal standing on all of them.
- **Keep the bold one.** The scout's ceiling was boldness, not perception: it sees the problem a bigger idea solves and the cheapest fix inside the spec. Where the observations support it, propose the bigger idea as a `Slice` - and keep the small fix too if it stands on its own.

## Write

`propose`, best first (the first call is rank 1), at most the cap your prompt names:

- **`kind`**: `Ticket` (one piece of work), `Bug` (it is broken), `Slice` (big enough to be a slice of its own), `Decide` (a rule in a spec or a decision that the finding argues against - only the owner can settle it; put the question and the options in `body`).
- **`title`**: what would be true for the person, in a sentence.
- **`why`**: why it matters to the person on the route.
- **`body`**: what it would look like - where it goes, what it shows, what it does - enough that someone could build it. It becomes the filed ticket's description.
- **`workItems`** for a Ticket or Bug, one line each. **`size`**, **`confidence`** (and why), **`area`**.
- **`observationIds`**: the evidence. At least one.

Specific to this product and this person. Judged at the scale the feature is for, never at the scale of the showcase data. Grounded: a proposal that came from a wall the scout hit says so.

`withdraw_proposal` takes back one of your own when a better one displaces it, or an earlier pass's undecided one that no longer holds.

---

# Hand off

1. **`add_comment`** with the short list in a line each, and how many observations you dropped and why, in a sentence.
2. **`set_phase` to `NeedsVerdict`.** A sift that kept nothing and said why for each observation has succeeded too.

`NeedsScouting` is the one way back, for observations that cannot be judged - none logged, shots that do not show what they claim, a route left unwalked. Say what is missing in a comment first. Not for observations you merely disagree with: dropping those, with a reason, is the job.
