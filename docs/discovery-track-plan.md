# Discovery: a track that uses the product and proposes what to change

Implementation plan. A third pipeline track beside **Delivery** (our own work) and **Review**
(someone else's branch). A Discovery ticket's description is a **direction** - "plan your month
of events as someone in a nearby town", "invite a new member to your team as an admin" - and the
track sends agents to use the running product the way that person would, then hands the owner a
short list of proposals to approve, edit or reject. Approved proposals become ordinary Delivery
tickets, linked back to the discovery, and the discovery waits for them, re-checks the product
once they are done, and comes to the owner for a last sign-off.

The working name during the pilot was "scout", and the agent that drives the browser is still the
scout. The track is **Discovery**.

## What it is for

The owner checks finished work by walking through it, screen-recording, and narrating what to
add, change and remove. Most of that is obvious once someone looks; some of it takes imagination.
The pipeline before this point already reviews and tests every change, so what is missing is not
QA but a user: someone who uses the feature for a real purpose and notices what is missing,
awkward or worse at scale. Discovery is that, run on demand, against the project's showcase data,
with the owner's time spent only on the verdicts.

## What the pilot showed

A hand-run pilot (one project's folder holds it: `data/projects/<key>/scout/pilot/` - a script,
the scout prompt, directions, a scoring sheet and four runs) settled the questions that decide
whether this is worth building:

- **It finds real things, and they repeat.** Across four runs and two models, the same problems
  came up independently, and each run added at least one the others missed. The best single
  finding was one only a user would hit: a detail page silently overwrote the place filter the
  visitor had chosen. One proposal in about twenty was wrong, and it was wrong because it read a
  spec without the decision that came after it.
- **It is steerable.** Changing the direction from a task ("sign up for updates") to a job ("plan
  your month") moved the headline finding at once. One paragraph telling it to judge a feature at
  the scale it is for, not at the seed's, changed how it reasoned within one run.
- **Its ceiling is boldness, not perception.** It saw the problem a bigger idea solves and then
  chose the cheapest fix inside the spec, because the prompt asked it to cap itself at five safe
  bets. The track below moves that cap out of the agent that explores (see Scout and Sift).
- **The direction is part of the design.** Directions written as a job a person comes to do
  find more than directions written as a feature to look at.
- **Cost and model.** Claude Opus 5.5 at high effort: about five minutes and $3 a run. Fable 5.2
  found slightly more for about twice the cost; Opus is the default, configurable per project.

What carried over into the rules below: use the product first and read about it after; judge at
the scale the feature is for; write the direction as a job; one fresh agent critiques what
another found.

## The track

| Phase | Kind | Who | What happens |
|---|---|---|---|
| `Backlog` | parking (shared) | - | Filed; the description is the direction |
| `NeedsCharting` / `Charting` | working | agent | Turns the direction into a **route** |
| `NeedsRouteApproval` | gate | owner | Only when the ticket's route-review switch is on; otherwise skipped |
| `NeedsScouting` / `Scouting` | working | agent + browser, recorded | Uses the product; logs **observations** |
| `NeedsSifting` / `Sifting` | working | a different agent | Turns observations into **proposals** |
| `NeedsVerdict` | gate | owner | Approve, edit, reject or snooze each proposal; Complete files them |
| `AwaitingWork` | parking | - | Until every filed ticket is settled |
| `NeedsRecheck` / `Rechecking` | working | agent + browser | Replays the route on the new code |
| `NeedsReview` | parking (shared) | owner | Final sign-off |
| `Done`, `Blocked` | parking (shared) | - | As on the other tracks |

After Rechecking: proposals filed -> `NeedsVerdict` again (a recheck round); nothing filed ->
`NeedsReview`. After a verdict: tickets filed -> `AwaitingWork`; none approved -> `NeedsReview`.
Rounds are capped (`discovery.recheckRounds`, default 2); past the cap Rechecking can still
propose, but only to `NeedsReview`, where the owner sees them.

Names avoid every word already taken: "Refine" and "Review" are Delivery phases, "plan" was the
steward's and was removed on purpose, "owner" and "orchestrator" mean what they mean everywhere.

**Why a track and not a kind of Delivery ticket.** `TicketTrack`'s own comment is the argument: a
phase is a tuple of behaviours (which skill runs, the rank it claims at, what its hand-off owes,
which bucket it costs into), and every one of those differs here. Discovery shares only the
parking phases, as Review does, and its working phases are its own. The track is fixed at
creation and no MCP tool reads or changes it, exactly as for Review.

## Each phase

### Chart

Reads the direction and writes a **route**: who the person is, three to five **jobs** they came to
do ("plan this weekend", "follow a place", "come back a week later and see what changed"), which
showcase cases to start from (from the index the showcase build writes), and the widths to use.
Goals, not clicks: the pilot's best findings came from the scout reacting to what it saw, and a
click-by-click script would make Scout a scripted QA walk.

- **It never reads specs, plans or decisions.** Those anchor the explorer on what is already
  planned. They are Sift's to read.
- **The route is kept.** Recheck replays it, and that replay is the main reason Chart is its own
  phase.
- No stack, no browser, no lease. Cheap model acceptable; configurable.

### Route approval (gate)

`NeedsRouteApproval`, skipped unless the ticket's switch is on - the same pattern as
`RequiresSpeculationReview` and its approval gate on Delivery. The owner can edit the route
before it runs. Off by default.

### Scout

The pilot, made a phase. A browser on the run's own stack and the showcase database; video
recording on; a chapter marked at each job and each step.

- **Observations, uncapped.** Scout records every observation through a tool - what it saw, what
  it tried, whether it was a wall it hit or a judgement, a bold idea or a rough edge - each
  stamped against the video and carrying a screenshot (optionally with a focus rectangle, the
  `QaShot` way). There is no "at most five" here: the cap is what made the pilot play safe, so
  Scout's prompt can say *be ambitious* and Sift does the cutting.
- **Use first, read after**, with one change from the pilot: Scout reads nothing about the
  product at all. Checking against what is known is Sift's job now.
- **Judged at the scale the feature is for**: the seed's counts are an accident, real data grows,
  and "fine because there is little of it" is itself an observation.
- **At human pace on camera**: `browser_type` with `slowly: true`. No fake mouse movement.
- **Confinement**, as in the pilot: no shell, no file written outside its run folder, the browser
  limited to its stack's origin (`--allowed-origins`), and `browser_run_code_unsafe` refused -
  it runs code in the browser server's own Node process, which is a shell by another name. The
  local-only rule binds it like every worker.
- **Resources**: the project's `app-stack` lease for its slot, the same as QA, plus whatever the
  project's `resourcePaths` would give a run that drives the app. The database is rebuilt from the
  project's showcase command (`discovery.seedCommand`; the ordinary seed when a project has no
  showcase).

Recording is the browser server's own: `--caps devtools` gives `browser_start_video`,
`browser_stop_video`, `browser_video_chapter` and action overlays, in the version pinned under
`data/playwright-mcp`. Chapter times are the clip boundaries.

### Sift

A fresh agent, not the one that explored, so it has no attachment to the ideas. It reads the
observations, their shots and the route, and does the critic's work:

- Drops what is generic ("add search" could be said of any site).
- Checks each against what is known: the project's specs and their "not in this slice" sections,
  the decisions register **including decisions made after a spec** (the pilot's one false positive
  was exactly that), open tickets, and earlier discoveries' proposals and verdicts.
- Merges duplicates, ranks, and writes the survivors as **proposals**, at most
  `discovery.maxProposals` (default 6), with a **dropped** list of one line each and its reason.
- Attaches evidence to each: a clip span (chapter to chapter) or shots, or both.
- Reads the project's **taste file** (below).

A proposal is one of four kinds:

| Kind | What Complete does with it |
|---|---|
| **Ticket** | Files a Delivery ticket from the draft (title, description, work items), linked to the discovery |
| **Bug** | The same, with `IsBug`, so its fix is blamed back as bugs are today |
| **Slice** | On a project with a steward: writes a slice idea (title and paragraph) into `steward.slices` as the owner's commit, for the steward to draft. Elsewhere: a briefing the owner takes to a session |
| **Decide** | A question only the owner can settle (a rule in a spec that the finding argues against). Answered in the verdict; the answer is recorded through the decisions register's existing route, and may turn into a ticket |

### Verdict (gate)

The owner's page for one discovery: each proposal with its clip or shots, editable in place.
Per proposal: **approve** (as written or edited), **reject** with a one-tap reason (generic /
already decided / not now / wrong), or **snooze** (until a date or until a named slice is
released; it comes back in a later discovery's Sift as "snoozed, due"). **Complete** files every
approved proposal at once.

Filing is a REST route with no tool behind it, like every other owner action on this board. Each
filed ticket carries the discovery it came from, and the discovery lists its tickets.

### Awaiting work

Parking: nothing claims it. It moves on by itself when every ticket it filed is **settled**:

- a Delivery ticket that reached `Done`;
- a ticket the owner dropped (counts as settled, not as blocking forever);
- a Slice proposal, once its idea is filed - the slice's own walk-through and release carry it
  from there;
- a Decide proposal, once answered.

A filed ticket in `Blocked` holds the discovery, and the drawer says which ticket it is waiting
on, so a stuck discovery is never a mystery.

### Recheck

Replays the route on the code as it now is, focused on the approved goals: for each one, did it
land, judged by using the product rather than by reading the tickets. It may file **anything** it
finds, each proposal marked **fix-up** (an approved goal not met) or **new**; the owner rejects
what they do not want. Its proposals go to `NeedsVerdict` like Sift's, which is a recheck round;
rounds are capped (above).

Same resources and confinement as Scout. No separate Sift pass: Recheck writes proposals itself,
held to Sift's rules (checked against what is known, evidence attached).

## The taste file

Rejections are not fed back as a growing log. Per project, `data/projects/<key>/discovery/taste.md`
is a short file (about 40 lines) of what this owner does and does not want, which Sift and
Recheck read. Every N rejections (`discovery.tasteFoldEvery`, default 10), a fold pass rewrites it
from the rejections since the last fold and commits it in the project folder, so a bad lesson can
be found and reverted. The owner can edit it like any other file there.

Not every reason is taste: **generic** and **wrong** fold into it; **already decided** means a
decision is missing from the register, and the verdict offers to record it; **not now** is a snooze
on that one proposal and teaches nothing.

## What is enforced, not asked for

| The limit | Where |
|---|---|
| A ticket's track is fixed at creation | `CreateTicketRequest.Track`, as for Review |
| Only the owner files tickets, ideas or decisions from a discovery | the Complete route, REST with no tool behind it |
| Only the owner answers a Decide proposal | the same route; Sift and Recheck can only ask |
| Scout and Recheck have no shell and no writes outside their run folder | the run's allowed and disallowed tools in `providers.ps1`, claude-only like the worker guard; the prompt on the other providers |
| The browser reaches only the run's stack | `--allowed-origins` on the run's browser server |
| A phase sees only its own tools | Scout records observations; Sift and Recheck write proposals; neither can file or approve |
| Rounds end | `discovery.recheckRounds` |
| New routes that reach a stack or an agent run | `QaIsolation`'s table |

The steward takes no part: it neither files discoveries nor gives verdicts. Letting it file one
after a slice is released is a later idea, not this plan.

## Where it touches the code

- **Phases and track**: `TicketTrack.Discovery`, the new `TicketPhase` members, and
  `PhaseMachine` (`IsOnTrack`, `NextPhases`, `SkillFor`, `ProgressRank`), `HandoffRequirements`,
  `PhaseCost` and the metrics buckets.
- **Dispatch**: the claim and the heartbeat give the working phases their skills; Scout and
  Recheck take the stack lease like QA.
- **Skills**: `data/skill-templates/{chart,scout,sift,recheck}/skill.md`, and each project's copy
  under `data/projects/<key>/skills/` - written once by the scaffolder, so a project that exists
  gets them by the usual route (the Skills page and its drift view).
- **Run setup**: `providers.ps1` builds the browser server's arguments for Scout and Recheck
  (devtools caps, allowed origin, output directory) and the disallowed tools.
- **Entities**: the route, observations, proposals (kind, status, evidence, reason, the ticket it
  became), the run's video and chapters, and a discovery id on the tickets it filed.
- **Files**: video, clips and shots live under the data root at the same path on every machine
  (`HostDataMirror`), so a worker host can run Scout later without a second layout.
- **Client**: the track in the new-ticket form, the drawer for a discovery (route, observations,
  proposals, linked tickets and what it waits on), and the Verdict page, built from the motion and
  theme system like every other page.
- **Setup**: cutting clips needs `ffmpeg`. It is a new dependency, so it gets a row in
  `docs/setup.md` and in `Services/SetupCheck.cs`, and a missing ffmpeg means shots only, said
  out loud - the same shape as the narrator's "not set up".

## What is kept, and for how long

A full recording is the evidence for one verdict, not an archive. A run is five to ten minutes of
mostly still UI, roughly 15-40MB of video, plus a few MB of shots; a discovery with a recheck is
on the order of 50-100MB, and nothing about that stops growing on its own. (The stores that exist
already do: on the board host today `data/qa-captures` is about 1.1GB and `data/qa-shots` about
270MB, with no retention on either.) So retention is part of the video step, not a clean-up for
later:

| What | Kept until | Why |
|---|---|---|
| The structured record: route, visits, observations' text, proposals, verdicts, links | never pruned | It is small, and it is what Recon maps |
| A run's full recording | 7 days after the verdict it fed is completed | The owner scrubs it while deciding; once decided, the clips carry the evidence |
| Clips and shots of **approved** proposals | 90 days after the discovery reaches `Done` | Evidence on the tickets they became, and the "before" a recheck is judged against |
| Clips of **rejected** or **snoozed** proposals | deleted at Complete; one still kept per proposal | The still is enough for history and the taste file; a snoozed one gets a fresh run when it is due |
| Shots no proposal used | deleted at Complete | Nothing points at them |

- **A budget as a backstop.** `discovery.storageBudgetMb` per project (default 2048): past it, the
  oldest prunable files go first, never the structured record, never a discovery still open.
- **The owner can keep one.** A pin on a proposal keeps its clip until unpinned.
- **Pruned is said, not broken.** Deleting a file records that it was pruned and when, so the
  drawer shows "recording removed 12 Dec" rather than a player that fails.
- **Every machine prunes its own copies.** Files sit at the same path under each machine's data
  root (`HostDataMirror`), so a worker host deletes what it holds once its run's files are handed
  back, and the board host's policy governs from there.

## Build order

1. **The core loop.** Track, Chart (no gate), Scout without video, Sift, Verdict, Complete filing
   linked tickets. The pilot, made real, and already useful.
2. **The long tail.** Awaiting work, Recheck and its rounds, and the route-approval gate.
3. **Video.** Recording, chapters, clips on proposals, ffmpeg in setup, and the retention above -
   the video and its pruning ship together.
4. **Taste.** The taste file and its fold, once there are verdicts to fold.

Board host only at first; a worker host can run Scout once its stack leases and the data-root
mirror cover the video.

## Set up for what comes next: the Recon tab

Not in this plan, but planned after it: a **Recon** tab for the before and after
of scouting (the name is the tab's alone: **Scout** stays the phase that does the scouting, and
the two are never called by each other's name) - what has been scouted, how recently and as whom, where scouting paid off, what
changed since, and what is worth scouting next, with directions it can suggest for the owner to
file. Nothing here builds it. What this plan owes it is the record: a tab can only map coverage
that the runs wrote down, and history that was never recorded cannot be backfilled from video.
So from the first run, Discovery keeps these, structured rather than in prose:

- **Where it went.** Every page a run visited (the URL and, where the project can say, its route
  pattern), at which width, signed in as which role, and for how long. Scout and Recheck log it
  from their browser; it is not left to be read back out of a recording.
- **What it ran on.** The commit of the code each run used, so "scouted before this changed" is a
  diff, not a guess.
- **Which area.** A project names its areas once (`discovery.areas` in `project.defaults.json`:
  each area's page patterns and code paths - its showcase modules are the natural first list), and
  every visit, observation and proposal carries the area it belongs to. Page patterns give the
  tab coverage from the user's side; code paths give it what changed since, from git.
- **Which showcase cases.** A run that starts from a case, or lands on one, records its id, so
  cases never visited show up as cold spots.
- **Who it was.** The persona and role on the route, so coverage is by kind of user as well as by
  page.
- **What it yielded.** Proposals and verdicts stay linked to the run, the area and the tickets
  they became, so the tab can say where scouting found things worth doing and where it has
  stopped finding them.
- **Directions as things to reuse.** A route and its direction can be copied into a new
  discovery, so a direction that paid off is run again after its area changes rather than
  rewritten.

The rule the tab will need, and that the record keeps possible: suggestions are the owner's to
file, the same way ideas and verdicts are. A suggested direction becomes a discovery ticket only
when the owner files it.

## Decided

- A track of its own, named **Discovery**.
- Chart writes goals and jobs, not clicks; a route-approval gate exists and is off by default.
- A Slice proposal counts as settled once its idea is filed.
- Recheck re-drives the product, focused on the approved goals, and may file anything it finds;
  the owner rejects what they do not want. Rounds are capped.
- Opus 5.5 at high effort by default, configurable per project and per phase.
- Scout runs on the project's showcase data, never the owner's database or QA's.

## Open for whoever builds it

- Final phase names, once they are seen in the drawer.
- How a phase is shown only its tools on the one `/mcp` server: follow whatever already limits a
  worker's tools by phase, or a second path as `/mcp/steward` is, whichever is the smaller change.
- How a proposal's clip is cut: on demand when the Verdict page opens it, or once when Sift
  finishes.
- What Recheck reads of the original observations: enough to know what was seen, not so much that
  it re-proposes the same things.
