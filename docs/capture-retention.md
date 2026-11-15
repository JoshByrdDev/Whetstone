# Capture retention: what QA's pictures keep, and for how long

The board keeps two stores of QA pictures under its data root, and until this policy neither had
any retention. On the board host they had reached about 1.1GB (`qa-captures`) and 270MB
(`qa-shots`, ~500 tickets) and were still growing. This is the same shape as the Discovery
track's retention (`docs/discovery-track-plan.md`, "What is kept, and for how long"), applied to
the stores that already exist. `CaptureRetention` (`Services/CaptureRetention.cs`) enforces it.

## The two stores

| Store | Written by | Read by |
|---|---|---|
| `qa-captures/<ticket>/` | the QA browser (Playwright's `--output-dir`, set in `worker.ps1`) during a `qa-test` or `review-pr` run; on a worker host, uploaded to the board host as each one lands (`PUT /api/hosts/<name>/qa-captures/...`) | `set_qa_shots`, during that run only: it copies the chosen files into `qa-shots`. Nothing else reads it - no page shows it |
| `qa-shots/<ticket>/` | `QaShotStore`, from `set_qa_shots`; replaced whole on every QA run | the drawer, the Classroom ("What QA saw"), the review panel and analysis cards (`/api/tickets/<id>/qa-shots/<n>`); a walkthrough deck, which links to the same URL at play time; a walkthrough or unit report being **written** (which reads the files) |

A unit report copies the shots it cites into `unit-reports/<id>/shots`, so it never depends on
`qa-shots`. The prototype captures (`PrototypeCaptureService`) live with the issue attachments,
which are a cache refilled from the tracker, and are outside this policy.

So `qa-captures` is intermediate: everything in it that matters has been copied into `qa-shots`
by the time the run ends. `qa-shots` is evidence a person goes back to after the ticket is done.

## What is kept, and for how long

| What | Kept until | Why |
|---|---|---|
| The record: each shot's caption, order and focus, and the ticket's waiver | never pruned | It is small, and it is what the drawer says when the picture has gone |
| A ticket's QA captures | 7 days after the ticket last reached `Done` | Nothing reads them once QA has picked its shots; the week covers a ticket reopened straight after it closed |
| A ticket's QA shots | 90 days after the ticket last reached `Done` | The drawer, the Classroom and walkthrough decks show them while anyone is still likely to look back |
| Either store, for a ticket that no longer exists | the next pass | Nothing can show them |

- **An open ticket is never touched.** Open is any phase but `Done` - `Blocked` and `Backlog`
  included. Its pictures are kept whatever their age or size, by the time rule and by the budget.
  A ticket reopened after its shots went shows them as removed until QA runs again, which
  replaces them.
- **When it reached Done** is the last `PhaseTransition` into `Done`, as for the worktree reaper,
  falling back to the ticket's last edit for a ticket older than that table.
- **A budget as a backstop.** Each store has a budget (`qaCapturesBudgetMb`, default 2048;
  `qaShotsBudgetMb`, default 1024). Past it, a pass prunes whole tickets, the one that has been
  Done longest first, until the store is back under. Open tickets count towards the size and are
  never among what goes, so a store whose open tickets alone are over budget stays over it, and
  the log says so.
- **Pruned is said, not broken.** A pruned shot keeps its row, with `QaShot.PrunedAt` set. The
  drawer and the Classroom say "removed on <date>" in its place, and its image URL answers with a
  placeholder picture saying the same - so a walkthrough deck, which carries the URL and not the
  shot, shows the notice rather than a broken image. Every pruned folder is also appended to
  `data/logs/capture-retention.jsonl`: which store, which ticket, why (`age`, `budget`, `orphan`)
  and how many bytes.
- **Every machine prunes its own copies.** A ticket's files sit at the same path under each
  machine's data root (`HostDataMirror`). The board host runs this policy over its own stores. A
  worker host keeps nothing a later run needs - captures are uploaded as they land, attachments
  and owner files are fetched again when a run starts - so its runner removes a ticket's
  `qa-captures`, `issue-attachments` and `ticket-files` folders once no run there holds the
  ticket and nothing in them has been written for an hour (`Remove-HostTicketCopies`,
  `data/runner.ps1`).

## Settings

`retention` in the global config (`data/config.defaults.json`, overridable in `data/config.json`):

```json
"retention": { "qaCapturesDays": 7, "qaShotsDays": 90, "qaCapturesBudgetMb": 2048, "qaShotsBudgetMb": 1024 }
```

A days value of zero or less turns that store's time rule off; a budget of zero or less turns its
budget off. A pass runs a few minutes after start and then every six hours. It touches nothing
past the data folder, so it runs on a QA copy as well.

## The Discovery track's media

The same pass prunes `discovery/<ticket>/` - a run's recording, the clips cut from it and the
screenshots - on the Discovery plan's terms rather than these (`docs/discovery-track-plan.md`,
"What is kept, and for how long" and "Step 3 as built"), with the ledger's store `discovery`. Its
days are `retention.discoveryRecordingDays` (7, from the verdict a recording fed) and
`discoveryClipDays` (90, from the discovery's Done), and its budget is per project,
`discovery.storageBudgetMb` (2048). Unlike the two stores above it prunes file by file, because a
discovery keeps one still per proposal and whatever the owner pinned.

## Not covered

- An open ticket's captures grow with every QA run on it, since each run adds to the same folder.
  The rule above keeps them all until the ticket is done; a long-lived ticket bounced through QA
  many times is the one case this policy lets grow.
- `walkthrough-clips` (voiced lines, shared between decks by hash) and `unit-reports` have their
  own lifetimes and are not pruned here.
