# Intake: tickets filed from a project's own error reports and alarms

A project's production can already tell its owner when something breaks: an error tracker groups
exceptions into issues, and cloud alarms go off and clear. Intake is the board reading those reports
and filing each one as a ticket the fleet works, with the details already in it, and attributing
every such ticket so that later we can ask how many bugs came from production reports and how many
were found in-house.

Two halves, kept apart on purpose:

- **The project's half** lives in the project's repo and is the project's own work. It puts every
  report on one **outbox queue** in its own cloud account, in the formats its tools already produce
  (an error tracker's issue JSON, a cloud provider's alarm notification). It names no reader and is
  worth having with nobody reading the queue.
- **The board's half**, this document, is platform work. It reads a queue, understands those
  vendor-standard formats, and files tickets. It names no project, follows the house conventions
  (CLAUDE.md, "Whetstone knows no project"), and the examples here use the placeholder project
  `alpha`.

Neither half knows the other. They meet only at formats that existed before either was written.

The decisions marked *settled* came from the owner (2026-11-16). The rest are decided here, each
with a reason, and the implementer may revisit one by saying why in the change.

---

## 1. The one exception to "the fleet never leaves this machine"

**Settled: the board may read one queue in a project's cloud account.** CLAUDE.md says nothing in
the pipeline may touch production, reads included. Intake is a narrow exception, the same shape as
the worker-host one:

- **The board, never an agent.** The poller is board code (a hosted service in this app). No worker,
  steward or MCP tool reaches the queue, the tracker or the cloud console, and nothing an agent is
  given carries the credential. A worker knows only what the ticket says.
- **One queue wide.** The credential the project's half makes can receive, delete and read the
  attributes of that one queue and nothing else. The board does not call the tracker's API, does not
  follow links in a report, and does not write anywhere in the project's account but the deletes
  that acknowledge a message.
- **Written down.** The change that lands this adds the exception to CLAUDE.md under "The fleet
  never leaves this machine", beside the worker-host paragraph, in those words. The worker guard and
  `data/heartbeat-prompt.md` do not change: their rule is about agents, and it still holds.
- **Refused on the QA copy.** `QaIsolation` gets a row for intake polling, so a QA copy never reads a
  real queue. Intake's tests use a stand-in queue (section 9).

## 2. Configuration

Per project, in the machine-local `project.json` (it holds a credential, so never
`project.defaults.json`, and never part of a worker host's kit):

```json
"intake": {
  "enabled": true,
  "sources": [
    {
      "name": "prod",
      "kind": "sqs",
      "queueUrl": "https://sqs.us-east-1.amazonaws.com/000000000000/alpha-outbox",
      "region": "us-east-1",
      "accessKeyId": "…",
      "secretAccessKey": "…",
      "pollSeconds": 60
    }
  ],
  "limits": { "newPerHour": 5, "newPerDay": 20 },
  "runbook": "docs/runbook.md"
}
```

- `kind: "sqs"` is the only transport for now. A transport is a class behind one interface
  (`IIntakeTransport`: receive a batch, delete a message), so a second one is a class, not a branch
  in the poller.
- What is *in* a message is worked out per message by its **adapter** (section 3), not by the
  source, so one queue can carry several kinds of report.
- `runbook` is optional: a file in the project's repo, read from the project's base at filing time,
  whose headings are the alarms' names (section 5).
- The Settings page shows the source with the secret masked and a "Test connection" button that
  reads the queue's attributes and nothing else. `docs/setup.md` and `Services/SetupCheck.cs` each
  get a row: intake configured, the credential answers, the queue is not backing up.

The .NET AWS SDK (`AWSSDK.SQS`) is the one new package. It comes through NuGet, so a fresh clone
needs nothing installed by hand.

## 3. Adapters: from a vendor's format to one shape

An adapter recognises a message and turns it into an `IntakeReport`:

| Field | Meaning |
|---|---|
| `Channel` | Where the report came from, as attribution counts it: `sentry`, `cloudwatch-alarm` |
| `Fingerprint` | What makes two reports the same problem: the tracker's issue id, the alarm's name |
| `State` | `firing` (new or seen again), `regressed` (came back after being resolved), `cleared` |
| `Title`, `Body` | The ticket's title and the Markdown of its description (section 5) |
| `Severity` | Mapped to the project's priority values |
| `OccurredAt`, `Count`, `Release` | When, how many times so far, which commit was live |
| `OwnerLink` | The report in the vendor's own UI, for the owner only (section 5) |

The first two adapters:

- **`sentry-issue`**, when a message has the attribute `kind = sentry-issue`: the body is
  `{ "issue": <Sentry API issue>, "event": <Sentry API latest event> }`. Fingerprint is the issue
  id. `State` is `regressed` when the issue's substatus says so, else `firing`. The body is built
  from the event's exception chain (in-app frames, file and line), its tags (route, step,
  environment), its release, and the issue's `count`, `firstSeen` and `lastSeen`. An event tagged
  `test: true` is filed as a ticket that says it is a test, and goes straight to Done with a note,
  so the project's end-to-end proof does not cost a fleet run.
- **`cloudwatch-alarm`**, when the body is an SNS notification envelope whose `Message` parses as a
  CloudWatch alarm state change: fingerprint is `AlarmName`, `State` is `firing` for `ALARM` and
  `cleared` for `OK` (an `INSUFFICIENT_DATA` is noted, never filed). The body carries the alarm's
  description, `NewStateReason` and the threshold.

A message no adapter recognises is kept (section 6, "Unrecognised") and deleted from the queue, so
one malformed message never blocks the rest.

## 4. One problem, one ticket

A table, `IntakeIssue` (project, channel, fingerprint, ticket id, first seen, last seen, occurrences,
last state, muted), is what makes a repeat a note instead of a ticket.

| A report arrives and… | The board |
|---|---|
| No `IntakeIssue` for its fingerprint | Files a ticket (section 5) and records the issue |
| The issue's ticket is open | Updates the counts and last seen; adds at most one comment per hour to the ticket ("seen 40 more times, last at …") |
| The issue's ticket is Done, and the report is `firing` or `regressed` | Files a **new** ticket, linked to the old one as a regression (`Ticket.RegressionOf`), and points the issue at it |
| The report is `cleared` | Comments "cleared at …" on the open ticket. Clearing never moves a ticket: an alarm that cleared still had a cause |
| The issue is muted | Updates the counts and files nothing |

The owner mutes an issue from the ticket's drawer ("Never file this again"), by REST route with no
tool behind it, which also closes the ticket. Unmuting is on the Intake page (section 7).

Messages are processed at least once: the board deletes a message only after its database
transaction commits, and keeps each SQS `MessageId` it has handled for 14 days (the queue's own
retention), so a redelivery is skipped.

## 5. The ticket

**Settled: straight into the fleet.** An intake ticket is filed in the phase a ticket the owner
files with no phase lands in, so the heartbeat picks it up like any other. There is no triage step.

- **Base:** the project's base, never an open slice's. A production error is a fix to what is
  deployed. It is filed with no `BaseBranch` and no `FoundOn`, so `SliceBranches.ForTicketAsync`
  already gives the right answer.
- **Title:** the adapter's title, prefixed by the channel: `[sentry] TypeError: Cannot read
  properties of null (reading 'id') in bookVisit`.
- **Description:** what happened, the exception and in-app stack at file and line, the release, the
  route or step, first and last seen and the count, and for an alarm the runbook section whose
  heading is its name, quoted. Then a fixed paragraph telling the worker that this came from
  production, that the release sha is the commit to read the stack against, that nothing outside
  the ticket can be opened, and that the proof is a failing test reproducing it locally, then the
  fix. For an alarm, that the cause may not be code, and a cause outside the code is routed the way
  the pipeline already routes production work: the investigation, a runbook comment, and an item on
  the owner's to-do list.
- **The owner's link** goes in a part of the description marked for the owner. A worker cannot open
  it, and the paragraph above says so.
- **`IsBug: true`** for a `sentry` report, so the fix is traced (docs/bugs-and-experiments-plan.md).
  For an alarm, not set: an alarm is as often an outage elsewhere as a bug here. The owner can set or
  clear it from the drawer as with any ticket.
- **Limits:** at most `newPerHour` and `newPerDay` new tickets from one project's intake. Past
  either, reports are still recorded, and the board files one **intake storm** ticket for that
  window instead, listing the fingerprints held back, each with its count. The next window starts
  fresh, and an issue held back is filed then if it is still firing.
- **The steward's open-at-once limit** counts intake tickets like any other. They are real work, and
  a limit that skipped them would let a production outage stop the steward's sense of how much is in
  flight.

## 6. Attribution: where every ticket came from

**Settled: knowing later how many bugs came from production reports, and how many from inside the
pipeline, is part of this work, not a follow-up.**

`Ticket.CreatedBy` already says who filed a ticket when it was not a person (`Steward`, and so on).
It answers "who" and not "from what", so it is not enough. Add:

- **`Ticket.Origin`**, written once at filing and never changed, by the code that files, never a
  tool or the client:

  | Origin | When |
  |---|---|
  | `owner` | A person filed it (the default, and what every ticket from before this reads as) |
  | `steward` | A steward filed it from a slice |
  | `found-in-run` | A worker's run noticed it in passing (`FoundOn` is set) |
  | `regression` | Filed because a Done ticket's problem came back (`RegressionOf` is set) |
  | `intake` | Filed by intake |

- **`Ticket.IntakeChannel`** (`sentry`, `cloudwatch-alarm`, `storm`, `unrecognised`) and
  **`Ticket.IntakeIssueId`**, set only on intake tickets.
- One function, `TicketOrigin.Of(ticket)`, is the only place the origin of a ticket is derived for
  filing, so the create route, the steward's filing and intake cannot spell it three ways.

A regression filed by intake is `intake`, with `RegressionOf` set: what it came from is production.

On the Metrics page, a **Bugs** panel cuts `IsBug` tickets by origin and channel, per week: how many
were filed, how long they took to Done, and how many were regressions. With bug tracing on, the same
cut answers "which phase, skill or provider wrote the bugs that reached production", because an
intake bug's blame links point at the tickets that introduced it. An intake panel beside it shows
reports received, tickets filed, the repeat ratio, storms and muted issues.

## 7. The Intake page

Under the project's settings, or a tab beside Steward, whichever fits the client's layout:

- The sources and whether each is answering, and when it last received a message.
- Every `IntakeIssue`: channel, fingerprint, title, occurrences, last seen, its ticket, muted or not.
- Unrecognised messages, kept 14 days, with their raw body, so a new kind of report is visible before
  anyone writes its adapter.

## 8. Order of work

1. This document, CLAUDE.md's exception paragraph, and the `QaIsolation` row.
2. Attribution first, on its own: `Origin`, `TicketOrigin.Of`, the create route and the steward's
   filing setting it, `RegressionOf`, the migration (every existing ticket reads as `owner`, except
   those with `FoundOn`, which read as `found-in-run`, and the steward's, from `CreatedBy`), and the
   Bugs panel. It is worth having before the first intake ticket exists.
3. The intake model: `IntakeIssue`, the handled message ids, the config, the transport interface and
   its SQS class, and the stand-in.
4. The two adapters, with fixtures copied from each vendor's documented format (no real project's
   data), and the dedupe and lifecycle rules of section 4.
5. Filing: the ticket text, the limits, the storm ticket, mute.
6. The poller as a hosted service, the Settings source and its test button, the setup rows, the
   Intake page.

## 9. Tests

`dotnet test Whetstone.Tests -c Release --filter "FullyQualifiedName~Intake"`, against an in-memory
transport, for: each adapter's fixtures, one problem one ticket across a repeat, a reopen after Done,
a clear, a mute, a redelivered message, an unrecognised message, the limits and the storm ticket,
the origin of every filing route, and the QA copy refusing to poll. Plus one test that drives the
SQS class against a local SQS-compatible stand-in (LocalStack or ElasticMQ in docker) when one is
running, and is skipped, saying so, when none is.

## Not here

- Writing back to a tracker (resolving an issue when its ticket is Done). It would need a credential
  that writes into a production service, which section 1 rules out.
- Pushing to the board from outside. The board has no public address, and intake keeps it that way.
- A project's own half: its error capture, scrubbing, outbox queue and the credential. That is in
  the project's own plan.

## Done means

A message from a project's queue, of either kind, becomes one ticket with the problem in it and
the right origin; a repeat becomes a note on it; the problem coming back after Done becomes a linked
regression; a storm becomes one ticket; and the Metrics page answers how many bugs came from
production reports this month against how many were found in-house.

## Decided while building

Where the plan was silent, or turned out wrong once it met the code, this is what was decided and why.

- **`FoundOn` is a column now.** It used to be a request field and was never stored, so "found in a
  run" could not be told apart after filing. The migration backfills it from `DiscoveryId` and from
  the reflection's own sentence ("Filed by the heartbeat's reflection after a run on ticket #N").
- **A walk-through counts as the steward's.** The board files it, not the owner, and the steward's
  is the nearest origin. A discovery proposal counts as found in a run.
- **`TicketOrigin.Of` has an order.** Its intake channel comes first, then `RegressionOf`, then
  `FoundOn`, then a steward or system creator, and otherwise the owner. One function decides
  `Origin`, and `CreateAsync` calls it for every filing path.
- **The stand-in is a source kind.** `kind: standin` in `project.json` is the in-memory transport.
  The QA copy refuses `sqs` at the transport and the poll and test routes in `QaIsolation`, so the
  copy can file from the stand-in and from nothing else (`POST /api/projects/{key}/intake/standin`).
- **Sentry `fatal` is High; `warning`, `info` and `debug` are Low.** Everything else stays Normal.
- **Mute and test reports close through `CloseUnworkedAsync`.** `PhaseMachine` reaches Done only
  from NeedsReview. The narrow close is refused while a run holds the ticket, so a mute never
  pulls a ticket out from under a worker. It records the transition and its reason.
- **A test report files at most one ticket an hour.** That ticket is not a bug, it creates no issue,
  and it is closed at once. The ticket proves the path works and changes no count.
- **The limits are counted from the message log.** The count is `filed` outcomes since the start of
  the hour or the day, which needs no counter to keep in step. There is one storm ticket per window,
  and its title and description are rewritten as the window holds more.
- **Every intake ticket lands in NeedsSpeculation**, storm tickets included. Nothing from outside is
  started without the steward or the owner looking first.
- **The Intake page's counts are live, not a mart.** They cover 14 days, the life of the message
  log. The Metrics page's weekly cut by origin is the mart (`mart_bugs_by_origin`).
- **An unrecognised message is kept, raw, and never filed.** The `unrecognised` channel is reserved
  for a later decision to file them.
- **The SQS class's live test needs a local stand-in.** It skips unless ElasticMQ (9324) or
  LocalStack (4566) answers, and says how to start one. The class itself was not run against SQS.
- **The Intake page is a tab shown only when a project has intake**, so a board without it looks the
  same as before.
- **The Settings editor writes the machine-local `project.json`**, never the committed defaults,
  because the source holds a credential. A blank secret keeps the saved one, and a read masks it.
- **A new issue's `LastSeen` is the report's time, not the clock's.** A later, earlier-dated report
  never moves it back.
