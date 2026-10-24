# A steward without a memo

> Design, 2026-10-23. Replaces the steward's memo with state that lives on the thing it is about,
> is written when it happens, and goes away with that thing. Built in the pieces at the end, each
> shippable on its own.

## Why

A steward run ends by writing `steward_save_memo`: one free-text note to the next run, replaced
whole every time. On the first project to run a steward for a few weeks it reached 26,000
characters (about 6,500 tokens) against a 32,000 limit, and three quarters of it was not what the
memo is for:

| What had piled up | Share | What it really was |
|---|---|---|
| "Taken as decided" | 75% | 45 bullets: repo facts, workarounds for the steward's own tools, copies of the owner's answers, and the steward's own standing rulings |
| Per-unit sections | 21% | Outcome status the outcomes table already holds, finished units never dropped, and "when #N merges, do X" |
| This run + pick up | 3% | The hand-off the memo was meant to be |

The memo is an end-of-session dump, and the faults are the pattern's, not the length's:

- **A killed run loses everything since the last save.** Its merges and tickets are on the board;
  the reasons and the follow-ups it promised itself are gone.
- **It is rewritten whole every run.** About 6,500 tokens out per run just to carry it, and every
  rewrite can silently drop or bend what the last one wrote.
- **Free text has no lifecycle.** A note about a unit never learns the unit finished.
- **The steward has nowhere else to put what it learns.** It changes no file, so repo knowledge
  had no home but here, and cutting the memo would only throw it away.
- **Rulings nobody sees.** "Taken as decided" is where the steward kept its own precedents. They
  steered every later run and the owner never saw one.

A run killed mid-way loses more than the memo today: `steward_begin` marks the owner's new answers
and instructions delivered, and clears the reason the run was due, before the run has done anything
with them.

## The rule

**Every piece of steward state belongs to an object, is written by the call that creates it, and
ends with that object.** Nothing is carried in a run's head to be written down at the end. A run
killed at any point leaves the board exactly as true as its last finished call, and the next run is
briefed from that state, not from what the last one remembered to say.

## Where each thing goes

| What the memo held | Its home | Written | Gone |
|---|---|---|---|
| "When #N merges: set C4 done, note its words" | A **ticket note**, shown by `steward_review` and the briefing | When it reviews or files the ticket | Handled when the ticket reaches Done, or by the steward |
| "E2 waits on the C2/C3 screens" | The **outcome's note** (exists: `steward_set_outcome`) | When it decides it | When the outcome is Done |
| "Group B swept; file once both answers are in" | The **questions** themselves, which already carry their unit and what they hold up, plus the outcome note | `steward_ask_owner` | When answered |
| "A 'by inspection' pass is fine for infra/" | A **ruling** on the record, read by every run, reversible by the owner | When it makes the call | When the owner reverses it |
| Paths, functions, ports, seed data, how a check behaves | The project's **notes store**, through the existing fold | `steward_learned`, queued at once | When stale, like any note |
| "steward_review output is one escaped line and there is no Bash" | **Fixed in Whetstone** | - | Once fixed |
| Copies of the owner's answers and pending questions | **Dropped**: `steward_begin` and `steward_decisions` already hand them over | - | - |
| "This run did X" | The **run summary** (exists: `steward_end`) | At the end | Replaced by the next |
| "Pick up" | **Not stored.** The briefing is built from live state when a run begins | - | Never stale |

## What changes

### 1. A killed run loses only its unfinished step

- `steward_begin` hands over new answers, new instructions and the due reasons but no longer marks
  them delivered or clears them. `steward_end` does, for what was handed over at begin.
- A run that ends without `steward_end` (`/api/steward/abandon`) leaves them undelivered and marks
  the project due again: "the last run did not finish". The next run is told both.
- `MinGapBetweenRuns` still applies, so a run that dies on every start is not restarted in a loop.

### 2. Ticket notes

A `StewardEntry` of a new kind, `TicketNote`: one line for one ticket, written for the steward's
own later runs (unlike a call, which is for the owner and never read back).

- `steward_note_ticket(ticketId, text)` adds one; `steward_done_with(noteId)` closes one.
- `steward_review` lists the ticket's open notes first. The briefing lists every open note by ticket.
- A note on a ticket that reaches Done stays open until the steward closes it, because it is usually
  the thing to do *after* the merge. A ticket that is deleted or taken out of the plan closes its notes.
- Shown on the ticket's drawer under the steward's flags, read-only, so the owner can see what the
  steward means to do.

### 3. Rulings

A `StewardEntry` of a new kind, `Ruling`: a standing call the steward will apply again, with the
place it rests on. "Taken as decided" without the hiding.

- `steward_rule(text, basis)` records one. Every briefing carries every live ruling, so a ruling is
  applied by being read, not by being remembered.
- The Record tab gets a **Rulings** section: each with its basis and date, and two owner actions,
  **Keep** (stamps it seen; the badge stops counting it) and **Reverse** (retires it with the owner's
  words). A reversal is delivered to the next run like an answer.
- The Now tab's Record chip counts rulings not yet seen.
- A ruling is still only a reading of decisions already made. The prompt's hard rule 3 is unchanged:
  a ruling cannot decide something the owner has not, and the skill says so where it introduces them.

### 4. What it learns about the repo

- `steward_learned(fact)` queues a fact in the board's database the moment it is said, so a killed
  run keeps it.
- After a run, `steward.ps1` folds the queue into the project's notes store with the same
  `notes-store.ps1` placement reflection uses (new note, or fold into the one that covers it), and
  commits the store. A project with no notes store keeps the queue and shows it on the Record tab.
- The steward already reads the store's index; nothing changes on the reading side.

### 5. The tool gaps the memo was working around

Each is fixed where it lives, then dropped from the skill and the memo:

| Workaround | Fix |
|---|---|
| A long `steward_review` is saved to a file as one line of escaped JSON, and the steward has no Bash to read it | `steward_review` returns sections (`part`: record, checks, findings, flags, notes) and says which parts were cut |
| "steward_begin's 'because' can be stale" | The due reasons are computed at begin from what is true now, each with its time |
| A check that failed shows only the tail of its output, which may miss the failing test | The answer carries the failing tests' own lines, picked from the whole output, and the tail |
| A ticket can leave review on its own (a rebase conflict) and merge says only "not at review" | The refusal says where it went and why, from its last transition |
| A ticket with no commits answers `nothing-to-merge` with no next step | The answer names what to do: set its outcomes, record the call, send it on |

### 6. The briefing replaces the memo

`steward_begin` (and `steward_status`) carry, beside what they carry now:

- `ticketNotes` - every open ticket note, by ticket.
- `rulings` - every live ruling.
- `reversed` - rulings the owner reversed since the last finished run.
- `recentRuns` - the last three run summaries, newest first, for continuity.
- `learnedPending` - facts said but not yet folded into the notes store.

And lose `memo`, `memoLength` and `memoTooLong`.

### 7. The memo goes

- `steward_save_memo` is removed. `StewardState.Memo` and `MemoUpdatedAt` are dropped by a migration
  after the existing memos have been sorted (below); until then the column is kept and only read.
- `memoMaxChars` leaves `StewardConfig` and the Settings page.
- The Steward page loses the memo toggle.
- The prompt (`data/steward-prompt.md`), the skill template and every project's copy of the steward
  skill change together: step 4 ("The record") is rewritten around the homes above, and "save the
  memo" leaves step 7. This is a pipeline rule, so it goes into every copy verbatim (CLAUDE.md, "A
  skill exists more than once").

## Sorting the memos that exist

A one-off, per project, with its owner. Each bullet of the current memo goes to exactly one place:

1. A **ticket note** or an **outcome note**, when it is about one ticket or outcome still open.
2. A **ruling**, when it is a standing call. The owner reads each one as it goes in: these are the
   bullets that were steering runs unseen.
3. The **notes store**, when it is a fact about the repo.
4. **Nowhere**, when it repeats an answer, a question, an outcome's state, a finished unit, or a
   tool gap fixed in step 5.

Done through the new tools' REST twins from a session with the owner, not by a steward run, so the
owner sees each ruling before it lands.

## Order of work

1. **Kill-safety** (section 1). Small, independent, worth having whatever else happens.
2. **Ticket notes** (section 2), with the drawer's read-only view.
3. **Rulings** (section 3), with the Record tab section and the chip count.
4. **Learned facts** (section 4).
5. **Tool gaps** (section 5), one commit each.
6. **The briefing, and the memo retired** (sections 6 and 7), with the prompt and every skill copy.
7. **Sort each project's memo**, with its owner. Then the migration that drops the column.

Pieces 2-5 add tools the steward is not yet told to use; piece 6 is where the skill switches over,
so the steward never runs with half a model of where things go.

## Decided

- **A ruling applies at once, and the owner can reverse it.** The Record chip counts the rulings the
  owner has not yet seen. The same trust as today, made visible; holding each one until kept would
  stall a run on every new precedent. (Owner, 2026-10-23.)
