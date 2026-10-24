# A plan is its slices

> Design, 2026-10-24. Not built. For the owner to mark up before any of it is.
> Summary: A plan stops being a document with slices cut from it. It is its slices - short ideas
> that are expanded one at a time when their turn comes - plus a one-page charter that decides
> nothing, and a register of decisions where each decision has one status and an override is
> declared when it is made. Each slice's story is tied to its outcomes, kept true by the decisions
> that change it, and driven end to end before the slice counts as built.

## Why

Today a plan is two texts that overlap by nature: the plan document (`steward.plan.master`), and
unit specs written from its sections in new words. Nothing writes back from a unit to the plan.
The board writes the plan file once, when it starts the plan. The steward changes no file but a
draft spec. The plan is on every project's never-merge list. On the first project to run a steward
for weeks:

- **The plan stopped at the first slice.** Its last edit was by hand, during slice 1. Four slices
  were agreed after that, with about thirty of the owner's decisions between them, and none reached
  the plan.
- **The order of work drifted.** The plan's step 4 became slices 4 *and* 5, so "slice 5" and plan
  step 5 now name different things. The plan's order of work is still marked *Proposed*.
- **Two texts both count as decided.** The steward's hard rule 3 counts both an agreed spec and any
  plain statement in the plan as decided, and says nothing about which wins. The project's own
  config comment says the plan is "decided from by nothing". The two disagree.
- **The next slice would be drafted from stale text.** The steward drafts from the plan's sections,
  as written before the earlier slices changed them.

Decisions have the same fault, five times over:

| Where a decision lives today | Numbered |
|---|---|
| The plan's own decisions section (55 rows, all from the day the plan was written) | No |
| A unit spec's "Decided" section, written by the board | `#n`, unique across the plan |
| The board's questions and their answers | By a text key |
| Owner-said entries, the summaries made when a unit's decisions are settled | No |
| `decisionsFile`, rewritten whole from the last two | No: 202 flat entries, oldest first |

- **One slice decision can be kept three times:** in the spec, as an owner-said summary, and in the
  decisions file.
- **Overrides are not marked.** "The first version: everyone in the team sees the same" still reads
  as decided after a later slice gave technicians their own role. A plan row that said "first
  thought; best practice wins" was settled later by a question, and both still read as decided.
- **Caveats in an answer are not tracked.** "(a) a technician role. *Let's talk about switching to
  a dynamic role*", and "charged as a late skip. *So this will actually need to be a permission
  thing*". Each half-changes its own pick, and is kept only as prose.
- **Day-to-day calls are mixed in with product decisions.** "Raise the test timeout" and "hold
  merges until that test is fixed" were instructions with a lifetime. They sit forever beside "a
  customer the business brought keeps the lower rate".

And the slice's story ("The walk-through this unit makes possible") is checked once, before it is
agreed:

- Nothing changes it when a decision made during the slice changes one of its steps.
- Nothing ties its steps to the outcomes that make them true.
- Nothing has ever driven one: Z1, "the walk-through above, driven end to end", has nothing behind it.
- Most stories are long. A long story is the first sign of a slice that is too big.

## The rule

1. **Every fact has exactly one home.** Anywhere else that needs it points to it by number and
   never restates it.
2. **Text that is finished is never edited.** A built slice is history. A later slice that changes
   what it built says so, and the later one wins.
3. **A status lives in one place.** Text about a decision may be copied when it is made, because
   that text never changes. Only the status changes, and only the register holds it.

Nothing is kept in step, because nothing is duplicated that can change, and nothing old is revised.

## The three things a plan is

| What | Says | Home | Edited |
|---|---|---|---|
| **The slices** | What each piece of work builds, and the story it makes true | One file per slice, in the plan's folder, on the base branch | An idea freely; a draft by the steward; agreed text only by a decision; built text never |
| **The charter** | Why the plan exists, its words, where things are | One file, beside the slices | Freely, by the owner. Nothing copies it |
| **The register** | What is decided, and whether it still is | The board's database; written out to the repo | Never rewritten. A decision is replaced by a newer one |

A plan's documents no longer have a decisions section, an order of work or a parts section.
Those are the register, the slices in order, and the slices.

## 1. A plan is its slices

### A slice's life

| Stage | What its file holds | Who writes it | Filed against |
|---|---|---|---|
| **idea** | A title, and one paragraph: who, what they can do at the end, what it builds on, what it is not | The owner (on the board, or in a session) | No |
| **draft** | Today's unit spec in full: the story, what is fixed, outcomes, decisions to settle | The steward, by expanding the idea in place | No |
| **agreed** | The same, with its status line committed as agreed | The owner, on the board or by hand | Yes |
| **building** | The same; the story and "Decided" change only through the owner's decisions (section 4) | The board, at the owner's answer | Yes |
| **built** | Every outcome done, the walk-through driven | Nobody: it is history from here | - |
| **released** | Unchanged | Nobody | - |

The status line gets a third reading beside draft and agreed: `**Status: idea.**`, configurable
like the other two (`StewardPlan.Idea`, default `\*\*Status:\s*idea\b`). An idea has no outcomes
and nothing is filed against it.

**Expanding.** `steward_draft_unit` rewrites the next idea in place. The next idea is the first
idea after the last agreed slice. The rules are the same as today: one draft ahead, never two; a
draft must say it is a draft. The idea's paragraph stays at the top as the seed of the story, and
git keeps the original. The steward drafts from:

- the idea
- the charter
- the register's decisions that apply to it, including those provisional "until" it (section 3)
- the slices before it
- the code as it now stands

A plan written weeks ago is not on that list. A draft starts from what is real.

**Numbers.** A slice's file is named number first, as now (`NN-slug.md`). An idea's number can
change freely, because nothing points to an idea: reordering, splitting or merging ideas is
renaming files. A slice's number is fixed once it is agreed, because tickets and outcomes carry it.
Ideas only ever come after agreed slices, so renumbering ideas never touches an agreed slice. The
board flags an agreed slice whose number or file has changed, rather than silently treating it as
new.

**Who changes ideas.** The owner only. The steward writes no idea. When it finds an idea too big to
draft, or work that belongs in no idea, it asks: `steward_ask_owner` with the proposed text. The
board commits the change on a yes, the same way agreeing works.

### The plan, as read

The board generates the whole plan; nobody maintains it. The Plan tab, and a file written out
beside the slices for readers of the repo, show:

1. the charter
2. the project-wide decisions in force, from the register
3. every slice in order:
   - built and released slices folded to their title, story and status
   - the slice being worked in full
   - ideas as their paragraph

The slices' stories, in order, are the readable plan. Ideas are on the Plan tab, so "how much is
left" is visible without anyone writing specs ahead.

### Starting a plan

The New plan form commits the charter from its template and one or more ideas, from an idea
template. The steward then expands the first idea into a draft, which is today's first unit
written as a draft. The refusals are unchanged: none while a run is open, none while the plan being
worked has a ticket open. In config, `steward.plan.master` becomes `steward.plan.charter`.

## 2. The charter

**The test for each line.** Could a later decision ever override it? Then it is a decision, and it
goes in the register as project-wide. That includes what is fixed ("built in this codebase") and
what is not in the plan ("no reviews in the first version"): both change over time, so both need a
status. Is it about how one part behaves? Then it goes in the slice that builds that part. Neither?
Then it belongs in the charter.

**What is left:**

- **Purpose.** Why this plan exists, who it is for, and what is different in the world when it is
  finished. Intent, in a few paragraphs: not a summary of the slices.
- **Words.** The plan's vocabulary, each term defined once. Slices, tickets and questions use the
  terms and never redefine them.
- **Where things are.** Mockups, sources, and the earlier plan or phase it builds on.

About a page. It overlaps nothing, so it is simply edited when it changes.

**The charter decides nothing.** It is direction: what a draft should serve, what fits the whole,
what the words mean. It is never the authority for answering a question or filing a ticket. Hard
rule 3 in `data/steward-prompt.md` becomes:

> A decision is made when it is in an agreed slice (never a draft or an idea), or in force in the
> register. A ruling of yours the owner has not reversed is your reading of those, and stands as
> long as they do. The charter is direction and decides nothing. Where none of those settles a
> question, it is the owner's.

The charter is not slice 0. A slice has a number, outcomes and a life from idea to released; a
living page has none of those, and making it a slice would need special cases throughout.

## 3. The register of decisions

### A decision

| Field | |
|---|---|
| **Number** | `#n`, one sequence per project, never reused. A project that already numbers decisions continues after its highest number |
| **Rule** | One or two sentences, present tense: what is true. Not the question and its answer |
| **Applies to** | The project as a whole, a slice of a plan, or some of a slice's outcomes |
| **Status** | In force; replaced by `#m`; provisional until a named point; withdrawn |
| **Replaces** | The numbers it overrides, each with one line on why. Stored only on the newer decision. The older one's status follows from it, so it is never written twice |
| **Came from** | A question (its key, its options, what was picked and what was not), a slice decision, the plan's setup, or a ruling the owner confirmed |
| **Follow-ups** | Open items the owner left in an answer, until each becomes a question or an idea |

A new table, `StewardDecision`, with a child table for what each replaces. "Provisional until" is
a slice, or "the owner says". When that slice is built, its provisional decisions come up for the
owner to confirm, replace or let lapse, and the steward drafting the next idea is handed them.

### How a decision is made

- **The owner answers a question.** The answer becomes a decision applying to whatever the question
  was about.
- **The owner settles a slice's decisions** (the Decision Sheet, or agreeing a draft). Each answer
  becomes a decision applying to that slice. The board writes its text into the slice's "Decided"
  section in the same commit, as it does now. That copy is written once and never changes.
- **The owner records one directly**, or changes one, from the register page. A change is a new
  decision that replaces the old.
- **The plan's setup.** What would have been the plan document's decisions section is entered as
  project-wide decisions when the plan starts, or at any time after.

### Overrides are declared when they happen

`steward_ask_owner` takes `touches`: the numbers of the decisions the question bears on, and it is
required (an empty list is allowed). The owner's card shows "this changes #41: ..." beside each
one. An answer that differs from a decision it touches replaces it. The override is deliberate,
and seen by the person making it.

The steward is also told, in the tool's description, to check `steward_decisions` for the subject
before asking. Most overrides are found there: a question that touches nothing is a claim that no
decision bears on it.

### What is not a decision

- **Instructions.** "Hold merges until that test is fixed" is an instruction with an end: a record
  of its own kind, with the condition that ends it. The steward closes it when the condition is met
  and says so; the owner can close it any time. It is never in the register.
- **Rulings.** A ruling stays the steward's reading, and `steward_rule` gains `reads`, the numbers
  it reads. When one of those is replaced, the ruling is flagged on the Record tab and in the next
  briefing, rather than standing quietly on a dead decision.
- **Follow-ups.** The steward records each caveat it reads in an answer (`steward_follow_up`, against
  the decision). Each one waits on the register page until a question or an idea cites it.

### How it is read

- **`steward_decisions`** returns what is in force by default. It can filter by slice, outcome or a
  search over rule, question and options. Replaced and withdrawn decisions come back only when asked
  for, each with what replaced it.
- **The briefing** at `steward_begin` holds the decisions in force for the slice being worked, the
  provisional ones coming due, flagged rulings, and open follow-ups.
- **The register page** on the Steward page shows what is in force, project-wide and by slice, then
  provisional decisions, follow-ups, and history.
- **`decisionsFile`**, written out whole from the register each time it changes, in the same order.
  Replaced decisions are listed in a history section with what replaced them. A slice cites `(#68)`,
  and anyone reading the repo looks it up there.

### Where the home is

The register lives in the board's database, and the repo's copies are written out from it. That is
how answers are kept already, and a status needs a structured field. Files as the home would mean
the board parsing "replaces #41" out of prose in a spec nobody may edit once it is built.

## 4. The slice's story

**Idea.** The idea's paragraph is the story's seed.

**Draft.** The steward expands it into the walk-through, in the same voice as now: one paragraph,
present tense, a person using what the slice builds from start to finish. Each step cites the
outcomes that make it true, inline, so it still reads as a story:

> ...signs in with Google or Apple (A1), adds their two dogs and their yard (B1, B2), picks weekly
> and a day on a calendar (C1)...

`steward_draft_unit` checks the draft:

- every cited id is an outcome of the draft
- every sentence of the story cites at least one outcome
- the story is at most 250 words. Past 180, the reply says the slice may be two

An outcome no step cites is allowed (an edge, a closing check), and the reply lists those so the
steward can say why.

**Agreed.** The story is the promise.

**Building.** The story changes only with the owner's decisions:

- A question about a slice being built may carry `story`: the sentence the answer would change, and
  its rewording under each option.
- When the owner answers, the board commits the reworded sentence and the decision together, in the
  slice's file, as it writes "Decided" now.
- A ruling never changes the story. A sentence nobody's answer changed stays as agreed.

**The walk-through is driven.** Z1 is always the walk-through. When every outcome outside the Z
group is done, the board files the walk-through as a ticket of its own, linked to Z1, at the start
of the next steward run. Its QA drives the story end to end and captures each step. Z1 is done when
that ticket passes, and the slice is built when Z1 is. A failing step goes back like any QA failure,
with the step it failed at. The unit's Classroom report tells the story with those captures.

**Built.** Frozen. A later slice that changes the behaviour says so in its own story ("this changes
slice 2's story: an invite now ..."), and the old story stays as what was delivered and proven on
its date.

## Moving a plan that already exists

Rebuilding finished slices is not part of this. For a plan already being worked:

1. **The register.** Entries from the questions, owner-said entries, the specs' "Decided" sections
   and the plan's decisions section are imported:
   - **Numbers.** A slice decision keeps its number. Plan rows take numbers after the highest.
   - **Duplicates.** One decision kept in two places becomes one entry, from the spec's text.
   - **Overrides.** Overrides found during the import are proposed to the owner as replacements,
     never assumed. The import report lists every pair it thinks conflict.
   - **Day-to-day calls.** These become instructions, closed if already done.
2. **The charter.** The plan's "what it is" becomes the purpose; its earlier-phase and sources
   sections become where things are; its vocabulary becomes words. Its rules go to the register.
3. **Ideas.** What is left of the plan's order of work becomes idea files, numbered after the last
   agreed slice.
4. **The old plan document.** It stays where it is, with a line at its top saying the slices, the
   charter and the register replace it. Config points `charter` at the new file.
5. **The slice being built.** It gets outcome citations in its story as a one-off edit by the owner.
   Its walk-through becomes the first driven for real.

## Order of work

Each piece ships on its own, and each leaves things better than before:

1. **The register.** Table, import, `steward_decisions` in force, the register page, `decisionsFile`
   from it. Hard rule 3 points at it.
2. **Overrides declared.** `touches` on `steward_ask_owner`, the card showing what an answer
   changes, `replaces` written on an answer.
3. **Not decisions.** Instructions with an end, `reads` on rulings and flagging, follow-ups.
4. **The charter.** Template, `steward.plan.charter`, the prompt saying it decides nothing.
5. **Ideas.** The idea status, expanding in place, numbers fixed at agreement, the generated plan
   view, the New plan form writing a charter and ideas.
6. **The story checked.** Citations, the draft checks, the length guide, in the template and the
   skill.
7. **The story kept true.** `story` on questions about a slice being built, committed with the
   answer.
8. **The walk-through driven.** Filed by the board, Z1 gating built.
9. **Moving the plan that exists**, by the steps above.

Every skill change goes to the template and every project's copy, as `CLAUDE.md` says.

## Decided

The choices this design had to make, answered as recommended. Mark any of them up.

1. **The charter is its own file, not slice 0.** It is a living page; a slice is not.
2. **The charter decides nothing.** Hard rule 3 names agreed slices and the register only.
3. **Only the owner changes ideas.** The steward proposes through a question; the board commits on a
   yes.
4. **A slice's number is fixed when it is agreed**; ideas are renumbered freely.
5. **The register's home is the database**, written out to the repo. A slice keeps a write-once copy
   of the decisions made about it.
6. **`touches` is required on every question**, an empty list allowed.
7. **The steward records follow-ups** from the owner's caveats; the owner fills in no extra field.
8. **Instructions are not decisions.** They are their own kind, with an end condition.
9. **Decision numbers are one sequence per project**, not per plan, because decisions outlive the
   plan they were made in.
10. **The story is at most 250 words**, refused past it; the steward is warned past 180.
11. **The board files the walk-through ticket**, not the steward, so it cannot be forgotten.
12. **A built slice's story is never rewritten**; a later slice says what it changes.

## Open for the build

- **A ticket that changes no code.** The walk-through ticket has nothing to implement. It needs a way
  to go straight to QA on the base branch, and a QA run that drives a story rather than a diff.
  The pipeline has no such path today.
- **Where the generated plan file goes**, and whether writing it on every change is noise in the
  base branch's history. One option is to write it only when a slice's stage changes.
- **What counts as a sentence** for the citation check: a period-split is enough to start with, and
  the check reports rather than refuses on anything it cannot read.

## Related, and not in this design

Found in the same review of how plans are worked, and worth doing alongside:

- **An outcome waiting on the owner.** An outcome whose tickets are merged but which waits on an
  owner's action holds its unit as the current one, so the unit actually being built shows as only
  "agreed". It needs a waiting state with a reason, and the current unit should be the earliest one
  with something the steward can still file or move.
- **"Done" set only by hand.** An outcome whose linked tickets are all merged, with nothing left to
  file, should read as built and wait for the steward to confirm it. An outcome marked in progress
  with every ticket merged should be flagged as a contradiction.
- **Outcome ids that disappear.** An id with tickets or a state but no longer in its agreed spec
  should be flagged on every read of the plan.
- **Release, apart from slices.** Release notes pile up while release is tied to a unit. A release
  as a cut of the base branch at a commit, covering whatever is merged by then, matches how a
  project that deploys on push actually ships.
