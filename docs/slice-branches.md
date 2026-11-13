# A slice is the top level

> Design, 2026-11-09. The owner chose three things the same day: a branch per slice, decisions that
> retire with their slice, and finishing the slices already in flight on the old shape before
> switching. Built 2026-11-10, every piece of "Order of work", behind the per-project switch
> `sliceBranches`; a project moves onto it from the Steward page (piece 8).
> Summary: The plan goes away as a structure. A project has slices, and each slice is one branch
> off the base, cut when the slice is agreed and merged back as one piece when the owner releases
> it. A slice may be built on one other open slice, never two. A slice's decisions are in force
> while it is open and retire when it is released. What has to outlive a slice is promoted, on
> purpose, into a short conventions file the owner keeps.
>
> Finished 2026-11-13: the one project on the old shape switched, and the switch itself went with
> everything else of the plan - `sliceBranches`, `plan`, `pastPlans`, `syncFrom`, the charter, the
> import and its pairs, the plan-key columns. Slices are the only shape; a steward exists only
> where `ProjectConfig.SliceBranchesOn` holds. The order of work below is kept as the record of
> how it got here.

This replaces parts of `docs/plans-as-slices.md`: its plan (section 1's "a plan is its slices"
keeps, the plan around them does not), its charter as a structure, and section 3's register as a
list that only grows. Its slice story (section 4) is unchanged.

## Why

On the first project to run a steward for weeks, three things went wrong, and they share a cause.

**A slice cannot be cut.** The project's base branch is a long-lived integration branch for the
whole plan, and every ticket of every slice merges into it. When this was written, that branch was
308 commits ahead of the project's main branch, made of four slices' work interleaved. A slice
reaches main only when everything before it on the integration branch is finished too. To ship one
slice alone, its commits would have to be picked out from between the others'. Testing one slice
on its own means testing all of them.

**The register only grows.** 675 decisions in force, written out as a 263 KB decisions file. Most
are answers about how a slice already built and merged should behave, like which name an email
uses. Once that code is merged, the code is the decision. Keeping it "in force" beside today's work
costs a steward run reading it every time, an import's duplicate pairs to settle, and a "whole
project" group of 328 entries that is partly a catch-all: an answer whose question named no unit
lands there whether or not it is project-wide. Workers never read the register at all. Only the
steward does, and the owner.

**The plan is a layer nobody works in.** The owner adds a slice when they want a new feature, and
new slices often have nothing to do with the plan they are filed under. Yet the board carries a
plan key beside every unit number, past plans, a one-plan-at-a-time lock, and a refusal to start a
plan while a ticket of the last one is open. All of that exists so there can be more than one plan.

The cause they share: **the board's unit of delivery is the plan, while the owner's is the slice.**

## The rule

1. **A slice is as high as planning goes in the board.** A larger idea that ties slices together
   is background: a document a slice may point to, which the board neither counts nor numbers
   against.
2. **One slice, one branch.** Its tickets fork from it and merge into it. It merges into the base
   as one piece, when the owner releases it.
3. **A decision lives as long as its slice.** Open slice: in force. Released: history, readable,
   numbered for good, and kept up by nobody.
4. **What outlives a slice is promoted, never assumed.** A short conventions file, edited by the
   owner, holds the rules that every slice must keep. Nothing reaches it without the owner's yes.

## 1. Slices, with no plan around them

- A project has one folder of slice specs (today `steward.plan.units`) and numbers its slices from
  one, for good. A new slice takes the next number. Numbers already given keep their meaning, so
  the migration renumbers nothing.
- What goes: `Ticket.PlanKey` and `StewardOutcome.Plan` as part of a unit's identity, `pastPlans`,
  the one-plan lock and its refusals, `OwnerStartsPlanAsync`, and the charter template in
  `data/plan-templates/`.
  A ticket names its slice by number alone. Existing rows keep their plan key as history and are
  never read for counting again.
- The charter stops being structure. A project may keep any background document it likes, and a
  slice may cite it. It is not on the board, not in the steward's briefing as a rule, and not a
  source of decisions.
- Ideas, drafts and agreement work as now (`plans-as-slices.md` sections 1 and 4). Agreeing a
  slice is the moment its branch is cut (section 2).

The configuration shrinks to match:

```jsonc
"steward": {
  "slices": "docs/slices",              // was plan.units
  "conventions": "docs/conventions.md", // new, section 4
  "slicesInFlight": 3,                  // section 3
  "decisionsFile": "docs/decisions.md"  // in force only, section 4
}
```

`plan`, `pastPlans`, `syncFrom` and the plan's `charter` and `master` are read once by the
migration and then ignored.

## 2. A branch per slice

### Its name and when it exists

`slice/<NN>-<slug>`, from the spec's file name (`slice/13-cards`). It is cut when the owner agrees
the slice: from the base's tip, or from its parent's tip when it is built on another slice
(section 3). It is deleted when the slice is released, and the merge commit that carried it names
it. A slice abandoned unreleased keeps its branch until the owner deletes it from the Steward page.

Only projects that merge locally (`mergeInsteadOfPr`) get slice branches in the first build. With
pull requests, the slice branch would have to live on the remote, and the fleet never pushes. A
project delivered by pull request keeps one base until that is designed (Open, below).

### The base becomes the ticket's, not the project's

Today "the base" is one branch per project, resolved by two functions that must agree:
`Resolve-BaseRef` in `data/worker.ps1` and `BaseRefAsync` in `Services/TicketGitService.cs`
(`CLAUDE.md`, "The base is not always origin's"). Both take the branch from the project
(`baseBranch`, `ResolveBaseBranch(cfg)`, `RepoTarget.BaseBranch`).

After this change, both take the branch from the ticket:

| The ticket | Its base branch |
|---|---|
| Belongs to an open slice | That slice's branch |
| Belongs to no slice (a bug on what is released, a chore) | The project's base, as today |
| Belongs to a released slice | The project's base. Released work is fixed where it now lives |

The local-or-origin rule inside each function is unchanged. A slice branch is local only, so it
resolves to the local ref. The two functions still have to agree, and `worker-base.tests.ps1` and
the C# tests grow a case for each row above. The heartbeat passes the ticket's base to the worker
instead of the project's (`$BaseBranch` in `heartbeat-common.ps1` becomes per claim), and
`ClaimResult` carries it, so a worker host is told it rather than working it out.

Everything that does git for a ticket already goes through those two functions: the fork, the
hand-off rebase, the squash net, the diff a person reviews, the board's rebase, the steward's merge
and its checks. That is the reason the change is small in code and large in effect. A place that
spells out the project's base directly instead of asking is a bug this change will find.

### What runs against the slice branch

- **The steward's merge** of a finished ticket goes into the slice branch. Its checks
  (`mergeChecks`, flags, `neverMerge`) are unchanged and run on the slice branch's tree.
- **The walk-through** is filed on the slice branch and run there, so the owner tests one slice
  with nothing else mixed in. Today it goes straight to NeedsQa on the base.
- **Keeping up with the base.** When the base moves, every open slice branch takes it with a
  `merge --no-ff`, the way `syncFrom` does now for the one integration branch. It is never a rebase:
  a slice branch has had tickets merged into it, and rewriting it would orphan every branch forked
  from it. A merge that stops on a conflict is handed to the project's `rebase-resolve.ps1` first,
  as a rebase is today, and otherwise asks the owner. A stacked slice takes its parent's branch,
  and the base reaches it through the parent.
- **Worker hosts** mirror every open slice branch, not one base (`worker-hosts-plan.md`, "mirrors
  the board host's refs"). The rest of a worker host is unchanged, because it already takes its base
  from what it is told.

### Releasing a slice

Releasing stays the owner's: a REST route with no tool behind it, from the Steward page. What it
does changes. Today `OwnerReleasesUnitAsync` only records that the owner merged and pushed by hand.
Under this design, the board makes the merge, locally, on the owner's press:

1. It refuses as today while an outcome is not done or a release note that is something to do is
   not ticked off. It also refuses while the slice's walk-through has not passed, while the slice's
   parent is unreleased, or while a ticket of the slice is open.
2. It brings the slice branch up to the base (as above), runs the merge checks on that tree, and
   refuses if they fail.
3. It merges the slice branch into the local base with `--no-ff`, a commit named for the slice
   (`Slice 13: cards`), and deletes the branch.
4. Its decisions retire (section 4), and every slice built on it is re-parented onto the base
   (section 3).

It never pushes. On a project where a push to the base is a deploy, the push stays the owner's, as
it is today.

## 3. Slices in parallel, and one built on another

Several slices may be open at once, up to `slicesInFlight` (the owner's setting, 3 by default). An
open slice is one that is agreed and not released. Ideas and drafts do not count.

Most parallel slices start from the base and never meet until each is released. The ones that
need each other are the delicate part. A slice that needs another's work can be built on it:

- **A slice may name one parent**, in its spec's header (`Built on: slice 11`). The parent must
  be open, and must itself start from the base. One level, never a chain.
- **It may not name two.** A slice that needs two unreleased slices is too entangled to build in
  parallel. It waits until one of them is released, then names the other. The board refuses
  agreeing it otherwise, and says which to wait for.
- **Its branch is cut from the parent's tip**, and keeps up with the parent, not the base.
- **It does not guess ahead.** A ticket in the child that needs an outcome of the parent's that is
  not built yet waits on that parent's ticket, through the waiting-on-another link tickets already
  have. The steward files the child's tickets for what can be built on what the parent already has,
  and holds the rest. That is the half of the work the child can do without the parent.
- **It is released after its parent**, never before (the release refusal above).
- **When the parent is released**, the child's branch merges the base, which now holds the parent,
  and its parent line is cleared. From then on it is an ordinary slice.
- **When the parent is abandoned**, the child cannot go on silently. The board asks the owner to
  re-cut it from the base, or abandon it too.

## 4. Decisions retire with their slice

### What stays

The register's table, numbers, question links and replacing stay as they are. They are what make
a decision citable as `#n` and its question readable beside it. A number is still the project's
for good.

### What changes

- **Every decision applies to a slice or is a convention.** "The whole project" as a group goes
  away. A question asked about a ticket applies to that ticket's slice. A question about no ticket
  must say its slice when it is asked (`steward_ask_owner` takes it), or say that it is about a
  convention. The fallback that put an answer about no unit under the whole project is removed.
- **A new status, retired.** It is derived, never written: a decision is retired when its slice is
  released. It is not replaced, because a later slice that changes released behaviour does not
  need to find and replace the old decision. The code is the old decision now, and the new slice's
  decision simply stands.
- **What is in force** is the conventions, plus the decisions of open slices. A run on slice 13 is
  shown slice 13's decisions, its parent's if it has one, and the conventions. Nothing else.
- **`decisionsFile`** holds what is in force, and nothing retired. Retired decisions are read on
  the Steward page by slice, and through `steward_decisions` with history. They are not written to
  the repo again. The file's size is bounded by how much work is open, not by how long the project
  has run.

### The conventions file

One file in the repo (`steward.conventions`), on `neverMerge`, edited by the owner. It is a short
list of rules every slice keeps, the way `CLAUDE.md` is for a codebase. Its size is measured in
dozens of lines. Each line may cite the decision it came from (`(#16)`).

The steward reads it whole at every run. A spec or ticket that would break one says so, and that
is a question for the owner, not a ruling.

**Promoting at release.** Releasing a slice shows its decisions. The steward has already marked
the few it thinks outlive the slice. These are rules about money, data, or how every page must
behave, rather than how this slice's feature works. The owner ticks those to keep, and the board
appends each as a line to the conventions file, in the same commit as the release. That is the
only way anything reaches it besides the owner's own edit. The owner can also promote any decision
later from the page.

### What goes

- The import and its "may disagree" pairs, once the migration below has run. Nothing imports again.
- The register's "whole project" group, and the duplicates it held.
- Replacing as routine upkeep. It stays for the rare case inside an open slice, where a later
  answer changes an earlier one in the same slice, through `touches` as today.

## Moving a project that already exists

The owner chose to finish the slices already in flight first. For a project on the old shape:

1. **Finish on the old base.** Every open slice is built and walked through on the integration
   branch, as now. The pieces below are built behind a per-project switch (`sliceBranches`), so a
   project not yet switched runs exactly as today.
2. **Merge once.** The owner merges the integration branch into the base for the last time, by
   hand, as they have been doing.
3. **Switch.** The migration, one owner action from the Steward page:
   - points `baseBranch` at the base, and drops `syncFrom` and the plan keys from the config
   - records every slice whose work is in the base as released, and its decisions as retired
   - drafts a conventions file from what is in force project-wide, deduplicated and shortened,
     for the owner to edit before it is committed (the steward drafts, the owner commits)
   - retires the decisions that were left in "the whole project" and not taken into the
     conventions

   As built (piece 8): the draft is mechanical - one line per decision in force, a rule said
   twice said once - and the shortening is the owner's, in the same form. A steward run is not
   needed to switch, and nothing a steward can run reaches it. The register takes in what the
   project kept one last time first, and never imports again. The plan being worked becomes the
   slices' folder: its rows lose their plan key, so a draft goes on with the number it has, while
   its tickets keep the key they were filed under, as history.
4. **New slices** get a branch when they are agreed. Drafts that already exist become slices of
   the new shape as they are.

The integration branch is kept until the owner deletes it. Nothing on it is rewritten.

## Order of work

Each piece is one change with its tests, behind `sliceBranches`. Proved on the QA copy before the
live board, since pieces 1 to 3 change what every worker's git does.

1. **The ticket's base.** Both resolvers take the branch from the ticket (section 2's table). The
   heartbeat and `ClaimResult` carry it. A slice branch is cut when a slice is agreed.
   `worker-base.tests.ps1`, the `UnderReview` and `MultiRepo` tests, and the C# base tests grow a
   case per row.
2. **Work on the slice branch.** The steward's merge, its checks and the walk-through target the
   slice branch. Each open slice keeps up with the base.
3. **Release.** The owner's route merges the slice branch into the local base, with its refusals.
   It also lets the steward mark the decisions to promote, and appends the ticked ones to the
   conventions.
4. **Built on one slice.** The parent line, the cut from the parent, the refusal of two parents
   and of chains, re-parenting on release, and the question on abandonment.
5. **Worker hosts.** Mirror the open slice branches, and take each run's base from the claim
   (`worker-board.tests.ps1`, `heartbeat-hosts.tests.ps1`).
6. **Decisions.** Slice-or-convention on every new decision, retired as a derived status, the
   in-force view and briefing, and `decisionsFile` written as in force only.
7. **The plan goes.** Plan keys stop being read for counting. `pastPlans`, the one-plan lock and
   the plan's start route are removed, and so are the import and its pairs.
8. **The migration**, as above.
9. **Words.** Every skill copy and template that speaks of "the plan", `<base-ref>` as the
   project's, or the decisions register. Each is a pipeline rule, so it goes into every copy
   (`CLAUDE.md`, "A skill exists more than once"). The same goes for `data/steward-prompt.md`, and
   `CLAUDE.md`'s sections on the base and on the steward, which describe what this changes.

## Decided

1. **A branch per slice**, cut when the slice is agreed, merged into the base as one piece on
   release. (Owner, 2026-11-09.)
2. **Decisions retire with their slice.** What outlives one is promoted, on purpose, into a
   conventions file. (Owner, 2026-11-09.)
3. **Slices in flight on the old shape are finished there.** The switch comes after. (Owner,
   2026-11-09.)
4. **A slice may be built on one other open slice, never two**, and only one level deep. A slice
   that needs two unreleased slices waits for one of them. (Owner, 2026-11-09.)

## Open for the build

- **Projects delivered by pull request.** A slice branch would have to be on the remote, and the
  fleet never pushes. The first build covers local-merge projects only.
- **`slicesInFlight`'s default.** 3 is a guess. The real limit is how much the owner can walk
  through at once, so it is theirs to set.
- **A fix found in a released slice while a later slice is open.** As a ticket of no slice, it goes
  to the base and reaches every open slice through its next sync. Whether the steward may file
  such a ticket itself, or only the owner, is left to the build.
- **Abandoning a slice.** What becomes of its decisions: retired with no release, or withdrawn.
