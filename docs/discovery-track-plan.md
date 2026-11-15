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

## What is measured

Discovery is worth running only while it saves the owner more than it costs them, and every
change to it (a prompt, a model, the cap, the taste file) is a bet on that. So the track records,
from its first run, what the marts in `analytics/` need to say whether a change helped - the same
way they already answer it for Delivery's phases. What it costs comes free: Discovery's working
phases are cycle runs like any other, and every aggregate mart is already cut by `track_scope`.
What is new is whether the work was any good.

**The question each number answers, and the fix it points at:**

| Measure | What it says | When it is bad, look at |
|---|---|---|
| **Approval rate** - proposals approved / shown, per discovery | Is Sift showing things worth showing | Every row below |
| **Approved per dollar** and **per owner-minute** | The headline: is it worth running | Cost per phase; Verdict time |
| **Rejections by reason** | *Which* thing is wrong: generic -> the prompts and the taste file; already decided -> what Sift is given to check against; wrong -> Sift's checking; not now -> nothing (timing, not quality) | The reason's own fix |
| **New to me** - share of approved proposals the owner had not already thought of | Whether it adds anything beyond what the owner would find walking through it themselves. The value Scout exists for | Scout's prompt: boldness, scale, the direction |
| **Edited before approval** - how much a draft changed between Sift and filing | Whether drafts are ready, or the owner rewrites them | Sift's writing |
| **Restored from dropped** - dropped lines the owner promoted to a proposal | Sift cutting too much | The cap; Sift's ranking |
| **Sift yield** - observations in, proposals out | How much Scout notices, and how hard Sift cuts | Both prompts, read together |
| **Coverage per run** - pages, areas, roles and widths reached; how far it wandered from the route | Whether Scout explores or only follows the route | Chart's jobs; Scout's prompt |
| **Repeats** - proposals matching an earlier one, in this discovery or another | Sift's de-duplication; an area worn out (a signal for Recon) | Sift's lookup of past proposals |
| **Landed** - approved goals Recheck found met, the first time | Whether proposals were clear enough to build right | Proposal drafts; the Delivery tickets they became |
| **Recheck yield** - its fix-ups and new proposals approved | Whether the extra drive is paying for itself | Whether Recheck should run every time |
| **Found by discovery** - bugs it filed that were confirmed, and bugs later traced to an area it had scouted without seeing them | What it catches, and what it walks past | Scout's prompt; the showcase's cases |
| **Time to verdict** - NeedsVerdict to Complete, and active minutes on the page | The owner's cost | Clips and drafts; how many proposals are shown |

Every measure is cut by what can be changed: model, effort, provider, each phase's skill version,
the taste file's version, the direction, the area, and the A/B arm - so an experiment can put two
Scout prompts or two models side by side with approval rate as its guardrail, the way
`mart_experiment_arms` already does for Delivery.

**What the track records for that**, as append-only events and links rather than prose:

- **Every verdict**, as an event: the proposal, approve / reject / snooze, the reason, whether it
  was edited and how much (the draft is kept beside what was filed), the owner's one-tap
  **new to me**, and when. Never overwritten: a changed mind is a second event.
- **Lineage**: each proposal names the observations it came from and the Sift or Recheck run
  that wrote it; each observation names its run and video time; each filed ticket names its
  proposal.
- **The dropped list as rows**, each with its reason, and a **restore** on the Verdict page that
  is recorded as an event.
- **Versions**: each run records its phase's skill version and the taste file's version (both are
  git history in the project folder, read the way `stg_git__skill_versions` already reads skills).
- **Recheck outcomes per approved goal**: met, not met, or could not tell.
- **Bug lineage**: a Bug proposal's ticket carries `IsBug` and its discovery, so the existing
  bug-tracing marts count discovery as a finder beside QA and the owner.

The marts themselves come after the core loop has run long enough to have rows: a
`mart_discovery_funnel` (observations -> proposals -> verdicts -> tickets -> landed), a
`mart_discovery_rejections`, a version trend for the scout, sift and recheck skills and the taste
file, and a Discovery section on the Metrics page. That is build step 5.

## What is kept, and for how long

A full recording is the evidence for one verdict, not an archive. A run is five to ten minutes of
mostly still UI, roughly 15-40MB of video, plus a few MB of shots; a discovery with a recheck is
on the order of 50-100MB, and nothing about that stops growing on its own. (The stores that exist
already do: on the board host today `data/qa-captures` is about 1.1GB and `data/qa-shots` about
270MB, with no retention on either, until `docs/capture-retention.md` gave them the same shape as
this.) So retention is part of the video step, not a clean-up for later:

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
   linked tickets. The pilot, made real, and already useful. **Built** - see "Step 1 as built".
2. **The long tail.** Awaiting work, Recheck and its rounds, and the route-approval gate.
   **Built** - see "Step 2 as built".
3. **Video.** Recording, chapters, clips on proposals, ffmpeg in setup, and the retention above -
   the video and its pruning ship together. **Built** - see "Step 3 as built".
4. **Taste.** The taste file and its fold, once there are verdicts to fold. **Built** - see
   "Step 4 as built".
5. **Measuring.** The Discovery marts and their Metrics section, once step 1 has produced a few
   weeks of verdicts. What they read is recorded from step 1 on ("What is measured"). **Built** -
   see "Step 5 as built".

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

## Step 1 as built

What step 1 settled, and where it stopped short of the design above:

- **Phases** are `NeedsCharting`, `Charting`, `NeedsScouting`, `Scouting`, `NeedsSifting`,
  `Sifting`, `NeedsVerdict`; Complete moves the ticket to `NeedsReview` (shown as *Sign-off*), and
  the owner moves it to Done. Awaiting work is step 2.
- **Tools** are on the one `/mcp` server (`Mcp/DiscoveryTools.cs`) and refuse outside their phase,
  as the delivery tools already do. There is no tool that approves, files or answers: the verdict
  and Complete are REST routes (`Api/DiscoveryEndpoints.cs`).
- **No shell on any discovery phase.** On claude every phase runs with `--tools Read,Grep,Glob`,
  and the scout's browser also loses `browser_run_code_unsafe`. Because the scout cannot start the
  product, the worker runs `discovery.prepare` before it and `discovery.teardown` after
  (`data/worker-discovery.ps1`), under the project's `app-stack` lease.
- **Configuration** is a `discovery` block in the project's config: `siteUrl` (required to file a
  discovery), `prepare`, `teardown`, `maxProposals` (6), `areas`.
- **Screenshots** are copied off the run's browser folder into `data/discovery/<id>/` as they are
  logged, so the record does not depend on the run folder lasting.
- **Each hand-off owes its record**, written on this pass: Chart a route with jobs, Scout an
  observation, Sift a proposal or a dropped idea.
- **A Slice proposal** becomes a briefing note on the discovery rather than a filed idea, and a
  **Decide** proposal's answer is kept on the proposal, not written to the steward's register.
  Both are for a later step to carry further.
- **The worktree** is the walk-through's: detached on the base, never a branch.
- **What is measured is recorded from the first verdict.** Every owner action on a proposal is a
  row in `DiscoveryVerdictEvents`, appended and never updated: approve, reject (with its reason
  and note), snooze, undo, restore from the dropped list, the one-tap *new to me*, and Complete's
  filing (with the ticket it became). Each event measures the proposal as acted on against Sift's
  draft, which is kept on the proposal (`Draft*`) and never changed: edited or not, kind changed
  or not, and the edit distance beside the draft's length.
- **Lineage** is the run's lease id (`RunId`) on every route, observation, visit and proposal -
  the same id `CycleRun.LeaseRunId` and `QaRulingEvent.RunId` carry - so a row leads to its run's
  model, provider, effort and skill hash without a column of its own. A proposal names its
  observations; a filed ticket carries `DiscoveryId` and its proposal `FiledTicketId`; a Bug
  proposal files with `IsBug`. `VideoAt` is on the observation, empty until step 3 records video.
  The taste file's version is recorded from step 4 on (`DiscoveryTasteRead`).

## Step 2 as built

What step 2 settled, and where it stopped short of the design above:

- **Phases** add `NeedsRouteApproval`, `AwaitingWork`, `NeedsRecheck` and `Rechecking`. The track
  runs Chart, (route approval), Scout, Sift, Verdict, then Awaiting work, Recheck, and either a
  new verdict or Sign-off. The journey reuses the existing stations: route approval sits on
  Chart's, the long tail on Verdict's. A recheck round is not rework: Awaiting work and Recheck
  rank with Verdict, so going round again is a forward move.
- **The route gate is a redirect, not a second chart hand-off.** A chart run that hands off to
  scouting on a ticket with `RequiresRouteReview` lands in `NeedsRouteApproval` instead, so the
  chart skill does not change. The owner edits the route (`PUT .../discovery/route`, which marks
  it `OwnerEdited`) and approves it (`POST .../discovery/route/approve`, `ApprovedAt`), or sends
  it back to Chart. Both are REST routes with no tool behind them. Off by default; the project's
  `discovery.reviewRoute` sets the filing form's checkbox, and a ticket can change it until
  scouting starts. There is no Settings control for it yet - it is a config key.
- **Complete** moves a discovery that filed tickets to Awaiting work, and one that filed nothing
  to Sign-off as before.
- **Settling is checked, not polled**: after any ticket reaches Done, after a ticket is deleted,
  and when the drawer reads the discovery. Every filed ticket Done or gone moves it to
  `NeedsRecheck`. A Blocked filed ticket holds it, and the drawer names it and its reason. The
  owner can skip the recheck from the drawer (straight to Sign-off). Slice and Decide proposals
  file no ticket in step 1, so they never hold one.
- **Rounds.** A round is an entry into `NeedsRecheck`. `discovery.recheckRounds` (default 2,
  0..10) is how many rounds may go back to a verdict: within it, a round with proposals must hand
  off to `NeedsVerdict`, and past it the run goes to Sign-off, where the owner rules on its
  proposals beside Complete (late proposals are filed by Complete without another round).
  `recheckRounds: 0` means the recheck still runs once, straight to Sign-off.
- **Goals** for a round are the filed Ticket and Bug proposals not already judged met in an
  earlier round. Recheck records one `check_goal` per goal per round (Met / NotMet / Unclear, a
  note and the observations behind it) and cannot hand off until each is checked. A proposal is
  marked `FixUp` (and must name a current goal it fixes) or `New`, and Sift's rules and the
  per-round `maxProposals` cap apply.
- **What Recheck reads of the earlier observations** (the open question below): this round's in
  full, earlier rounds' as one line each - id, kind, title, round - beside the earlier goal checks and
  every proposal with its verdict, enough to know what was seen without re-proposing it.
- **Recorded for the marts**: `DiscoveryGoalChecks` (one row per goal per round, with run id and
  commit - recheck's landed rate), `Round`, `RecheckKind` and `GoalId` on proposals (recheck's
  yield, fix-up against new), `Round` on observations, and `ApprovedAt` / `OwnerEdited` on the
  route. Verdict events need nothing new: a recheck proposal's verdict is an event like any.
- **Same berth as Scout**: the `recheck` skill (`data/skill-templates/recheck/skill.md`) runs with
  the scout's browser, `discovery.prepare` / `teardown` and the `app-stack` lease, and no shell.

## Step 3 as built

What step 3 settled, and where it departed from or fell short of the design above:

- **The board finds the recording; the agent is not trusted to report it.** The scout's browser
  starts with `--caps devtools` (`Get-DiscoveryBrowserArgs` in `data/worker-discovery.ps1`), and
  the scout calls `browser_start_video` with no filename, so the video lands in the run's folder
  as `video-<start>.webm`. The browser writes the file only at `browser_stop_video`. Whenever the
  board reads a discovery, completes a verdict or runs retention, it *adopts* any finished video
  under `discovery/<ticket>/runs/` (`DiscoveryMedia.AdoptAsync`): moves it to `rec<id>.webm` beside
  the shots, records a `DiscoveryRecording`, and stamps what was logged during its span. A scout
  cut short, or one that forgets to say it recorded, still has its video adopted. There is no
  route or tool that reports a video.
- **Second 0 comes from the file's name**, which the browser server stamps as recording starts.
  Measured against frames, video time matched wall time to within the 0.5s sample; the file's
  length, by contrast, runs about a second past wall time. A video with any other name falls back
  to its last write less its length.
- **An observation's moment is its screenshot's write time** (`DiscoveryObservation.ObservedAt`),
  not when it was logged: a model logs a few seconds after it sees, and the clip should show the
  thing, not the scout thinking. With no shot, or a shot older than ten minutes, the time it was
  logged stands in. `VideoAt` and `RecordingId` are set at adoption.
- **Chapters are the board's, through a tool** (`mark_chapter`, Scouting and Rechecking only, a
  `DiscoveryChapter` row) beside the browser's own `browser_video_chapter` card. The browser does
  not say when a chapter fell, so the skills tell the agent to make both calls in the same
  message. Called one after the other, the board's stamp falls about two seconds after the card
  starts, because the card call blocks while it shows.
- **A clip is cut on demand, the first time it is played** (the plan's open question), and again,
  if it never was, before the recording it comes from is pruned. One clip per proposal
  (`p<id>.mp4`), from the recording holding most of what it cites. Around each cited moment: from
  the chapter it falls in, but at most 20s and at least 5s before it, to 4s after it or the next
  chapter, whichever comes first. Spans that overlap are joined, and the clip is capped at 90s
  (`DiscoveryMedia.ClipSpans`). H.264 in MP4, at most 1280 wide, falling back to VP8 WebM for an
  ffmpeg built without libx264. A clip of about ten seconds cut in under a second on the QA copy.
  Two cuts run at a time, one per proposal.
- **Two ffmpegs.** The browser records with Playwright's own (`playwright install ffmpeg` in
  `data/playwright-mcp`, into the user's cache). Without it, `browser_start_video` fails, and the
  skill says to carry on with screenshots and say so. The board cuts clips with the machine's own
  (PATH, or `ffmpegPath` in the global config). Without it there are recordings but no clips, and
  the drawer says so. The Setup check's *Discovery video (ffmpeg)* row covers both, and shows only
  once a project can be scouted.
- **Retention is part of `CaptureRetention`**, not a second service. The same pass and the same
  ledger, with the store `discovery`, pruned file by file rather than by folder
  (`DiscoveryMedia.PruneAsync`):
  - **At Complete** (`PruneAtCompleteAsync`): a rejected or snoozed proposal's clip goes, cut or
    not (`ClipPrunedAt`, so none is cut after). Its first cited screenshot stays as its still. Any
    shot no approved, filed, undecided or pinned proposal, still or goal check uses goes
    (`ShotPrunedAt`). Recordings get `VerdictAt`, which starts their seven days. The run folders
    go too: the browser's scratch, all of it already copied out.
  - **Each pass**: a recording seven days after `VerdictAt`, or after Done for one that fed no
    verdict, open discovery or not, after first cutting the clips that outlive it. A kept clip
    90 days after Done, and every shot but each proposal's still with it. Run folders a day old
    once nothing drives the ticket. Orphans whole. Days are `retention.discoveryRecordingDays` /
    `discoveryClipDays` in the global config.
  - **The budget** (`discovery.storageBudgetMb`, default 2048, per project): past it, a whole Done
    discovery's media at a time, the one Done longest first, stills included. Never an open
    discovery's, never a pinned proposal's.
  - **The pin** (`PUT /api/discovery/proposals/{id}/pin`, REST with no tool behind it) keeps a
    proposal's clip and shots through every rule, the budget included.
  - **Pruned is said**: rows stay with when. The drawer shows "Clip removed on", "Recording removed
    on" and "Shot removed on" (`RemovedShot`, which now names what went), a removed recording or
    clip answers 410, and a removed shot's URL answers with the placeholder picture.
- **The stills keep beyond the 90 days**, which is a reading of the plan's table: "one still kept
  per proposal" is said of rejected ones at Complete, and step 3 applies it to approved ones at 90
  days too. Every proposal keeps a picture until the budget takes it. Goal checks' shots are kept
  while the discovery is open and go at 90 days.
- **The drawer** plays each recording with its chapters as jump points, and each observation seen
  on camera has a "▶ m:ss" that plays the recording from just before it. Each proposal plays its
  clip, and a *keep* toggle pins it. Checked at 390px wide with no sideways scroll.
- **Worker hosts**: `Remove-HostTicketCopies` in `runner.ps1` sweeps `discovery/<ticket>` like the
  other ticket copies. Scout still runs on the board host only. `log_observation` copies a
  screenshot from a path on the board's own disk, and that would need the capture upload QA uses
  before a worker host could scout.
- **Fell short / not done**: recording size is the browser's default (it scales a 1280x800
  viewport down to 800x500), because the skill does not pass `size`. A run that stops and restarts
  its video gets a clip from only one of the recordings. Nothing re-adopts a video moved by hand.
  The verdict-time numbers ("Time to verdict", active minutes on the page) record no clip views.
- **Proven on the QA copy**: a scout played through the board's own `/mcp` and the pinned
  Playwright MCP, with the worker's browser arguments, against the copy's own board. The 18s
  recording was adopted with both observations and both chapters stamped. Two clips of about 9s
  played in the drawer. Complete removed the rejected clip (410 after) and kept its still and the
  approved clip. The Setup check row read ok.

## Step 4 as built

What step 4 settled, and where it departed from or fell short of the design above:

- **The file** is `data/projects/<key>/discovery/taste.md`, in the project folder and its own repo
  (`DashboardPaths.DiscoveryTasteFile`). It has two sections under a short preamble: **The owner's**,
  which no fold changes, and **Learned**, which each fold rewrites. A file the owner wrote without the
  headings is all theirs; a fold adds its section after it. That split is the answer to "which changes
  need the owner": a fold may only *suggest* a change to the owner's section (after a
  `SUGGESTIONS FOR THE OWNER:` line in its answer), and the panel shows the suggestion beside the
  file for the owner to make or leave.
- **What counts toward a fold** is wider than the plan's "every N rejections": the brief asked for
  verdicts, so the count is *proposals whose verdict teaches* - approved (edited or not), rejected as
  generic or wrong, or restored from the dropped list - among those whose verdict is closed (decided,
  and the discovery no longer at `NeedsVerdict`, where a change of mind is a click away).
  `discovery.tasteFoldEvery` (default 10, 1..100) is that count. Not now, a snooze and already decided
  ride along in the fold's material marked "teaches nothing", so they are never folded twice, and are
  not counted. *New to me*, edits (with their distance and a changed kind or title) and changes of
  mind are told to the fold beside the verdict they belong to. Already decided is listed on the panel
  as "a decision the register may be missing"; the verdict does not yet offer to record it in the
  steward's register (below).
- **Who runs the fold: a worker run, after it is done**, beside reflection - the notes store's
  maintenance is the precedent, for the reasons it gives: the reflection command (provider, cheap
  model, effort) and the worktree are already set up, it is outside the run's measured window, and it
  is board-host only (`-not $onHost`) and skipped after a usage limit. Any run of the project carries
  it, not only a discovery's. `Invoke-DiscoveryTasteFold` in `data/worker-discovery.ps1` asks the board
  (`POST /api/discovery/projects/<key>/taste/fold/begin`), and only when one is due runs the model on
  the prompt the board wrote, then hands the answer back (`.../fold/finish`). The board starts no model
  for it, so a QA copy needs nothing in `QaIsolation`'s table: the copy's heartbeat is refused, and
  with it every fold.
- **The board owns the file.** Begin takes a claim (15 minutes, kept in `DiscoveryTasteState`, so two
  runs finishing together fold once) and remembers the file's hash and the events it handed out.
  Finish checks the answer (`ParseReply`: not empty, at least one `- ` rule, no headings, at most 40
  non-blank lines and 6000 characters, fences and a stray `## Learned` stripped), refuses it if the file
  changed since begin, writes the Learned section with the owner's section as it is on disk, commits
  **that one file** in the project folder's repo with a message naming the verdicts and discoveries
  (`ProjectRepo.CommitFile`; a stand-in identity when git has none, as the baseline commit does), and
  appends a `DiscoveryTasteVersion`. A refused answer or a failed run folds nothing, lets the claim go,
  and says so on the panel ("Not folded: ..."); the verdicts wait for the next run. A folder that is
  not a repo still gets the file, and the version says it is not committed.
- **Versions are by content.** A version is the file's SHA-256 with line ends made LF. Whenever the
  board reads the file and finds it changed outside - an edit in the folder, a `git revert` of a fold -
  it records an `Outside` version first, with the commit when the file on disk is a commit's, so what
  a run read always has a number. Sources: `Fold`, `Owner`, `Revert`, `Outside`; a fold keeps the
  verdict events it took in (`EventIds`), how many verdicts taught, its suggestions, and the ticket
  whose run it rode on. Versions are appended, never rewritten, and keep their whole text.
- **The version each run read** is a `DiscoveryTasteRead` row (run id, round, phase, version), written
  when a Sifting or Rechecking run calls `get_discovery`, which now hands it `taste` (text, version, and
  a note on how to apply it, or a plain "no taste file yet" with no verdicts folded). One row per run,
  round and version, with no version when there was no file. A proposal leads to its taste through its
  `RunId`, the same join as everything else; the mart that compares approval before and after a fold
  is step 5's.
- **The owner's routes**, none with a tool behind it: `GET /api/discovery/projects/<key>/taste` (the
  panel's whole state), `GET .../taste/versions/<id>`, `PUT .../taste` (an edit of the whole file,
  committed as "edited by the owner"), `POST .../taste/versions/<id>/restore` (put back, as a new
  version), and `PUT .../taste/fold-request` (fold at the next run, below the count; never with nothing
  to fold, and cleared by the fold).
- **The panel** is a *Taste* section in every discovery's drawer (`DiscoveryTaste.tsx`), not a page of
  its own: which version this discovery's sift and rechecks read, the file, how many verdicts toward
  the next fold (or "No verdicts yet, so there is nothing to fold"), the last fold's note and
  suggestions, the already-decided list, and the versions with show and put back. Checked at 375px
  wide with no sideways scroll.
- **Skills**: the sift and recheck skills read `taste` and apply it - what it says is unwanted is
  dropped against it unless the case is different and `why` says how; it never replaces the specs and
  decisions - in the templates and every project's copy, word for word. The sift and recheck prompts
  say the same in a line (`Format-DiscoveryNote`). The copies' Skills-page baselines were left as they
  were, as step 3 left them.
- **Proven on the QA copy**: two discoveries on alpha, scouted and sifted through the board's own
  `/mcp`, with seven verdicts (an edited approval new to the owner, generic and wrong rejections with
  notes, a not now). The first sift read no file; the count reached `tasteFoldEvery: 3` only after
  Complete; begin refused a second claim; the first fold's answer was written by hand, the second's by
  a real cheap model run on the board's prompt (outside the copy), which merged the new verdicts into
  the existing lines as asked. Both landed as versions 1 and 2, each one commit of `discovery/taste.md`
  in the project folder; a third discovery's sift recorded reading version 2. An edit from the drawer
  and a put back of version 1 became versions 3 and 4, with their commits.
- **Fell short / not done**: the fold itself was not run by a worker on the copy, which refuses the
  heartbeat; the script half is covered by `worker-discovery.tests.ps1` and the board half by the
  `DiscoveryTaste` tests. Already decided is shown, not offered as a decision to record. A fold reads
  verdicts only, not the owner's later edits to the filed tickets. The owner's edit is of the whole
  file in a textarea, with no diff view between versions beyond showing each one.

## Step 5 as built

What step 5 settled, and where it departed from or fell short of the design above. It was built
before there were real verdicts to measure, so every choice below is one to revisit once there are.

- **Where it is.** The loaders in `analytics/refresh.py` (eight Discovery tables, without their
  long prose: bodies, notes and the taste text stay on the board); one staging model per table,
  `stg_board__discovery_*`, plus `stg_board__discovery_tickets` for the tickets on the track; five
  intermediate models, `int_discovery_*`; and four published marts - `fct_discovery_proposals`,
  `mart_discovery_funnel`, `mart_discovery_rejections`, `mart_discovery_versions`. The board reads
  them at `GET /api/analytics/discovery?days&project&by` (`AnalyticsService.Discovery`), which only
  reads parquet and so needs no `QaIsolation` entry, and the Metrics page shows them as a
  **Discovery** section after Escapes. The section always reads the Discovery track, whatever track
  the page is filtered to; the window and the project still apply.
- **Built for Recon, in the names its plan gives.** The `stg_board__discovery_*` models and
  `fct_discovery_proposals` are the ones `docs/recon-tab-plan.md` lists as shared, and Recon's R2
  builds on them rather than beside them. Two things are deliberately half-done for it:
  `fct_discovery_proposals.area` is Sift's own `Area` on the proposal for now - the plan's rule,
  the majority of its observations' URLs matched against `discovery.areas` page patterns, needs the
  pattern syntax R2 is to settle, so R2 replaces that one column in place; and the direction lineage
  (`direction`, `direction_lineage_id`, `direction_lineage_title`) follows `ReconFrom`'s `rerun:<id>`, which is
  loaded and staged now. Recon's own marts, routes and tab are not built.
- **A window is a cohort.** Every row is counted in the window by when its *discovery* was filed,
  not when each verdict came, so a discovery's funnel is never split across two windows and a rate
  never has a numerator from one week and a denominator from another.
- **Rates are over closed verdicts, with a floor.** A verdict counts once it is closed - decided,
  and the discovery past `NeedsVerdict` (Done, or a later Filed event or a later exit from the gate)
  - the same rule step 4's fold counts by. Below `discovery_min_verdicts` (a dbt var, 10) closed
  verdicts in a row, every rate on it is null and the mart says `enough_verdicts = false`; the page
  says "Not enough verdicts yet" or "too few" rather than printing 0% or 100% from three verdicts.
  Counts are always shown. Sift yield, landed and recheck yield are not gated: they are ratios of
  counts the page shows beside them.
- **Approved is the latest verdict, and filing counts.** The verdict is the latest Approved,
  Rejected, Snoozed or Undone event; a Filed proposal is approved whatever came before. A second
  decision or an undo marks `changed_mind`. *New to me* is the latest non-null answer. The edit is
  read from the Filed event (else the last approval): edited, kind changed, and the share of the
  draft changed (`edit_distance / draft_length`, at most 1). The median edit is of the edited
  drafts only - over every approval it reads 0% whenever fewer than half were touched.
- **Owner minutes are estimated from the verdict log.** There is no page timer. A sitting is a run of
  verdict events at most five minutes apart; each event costs a minute for the first of a sitting
  and the gap since the last otherwise (`int_discovery_verdict_rounds`). Time to verdict is from
  entering `NeedsVerdict` to leaving it, per round. Approved per owner-minute divides by that.
- **Approved per dollar** uses `fct_runs.list_cost_usd`, so it is shown only where *every* run of a
  discovery has a list price (`cost_complete`); a run that shared its session has none. The QA copy
  refreshes without transcripts and so has no prices, and the page says so.
- **Repeats are by title.** Two proposals on the same project repeat when their titles' words of
  three letters or more share at least two and a Jaccard similarity of 0.5 or more, the later one
  counted (`int_discovery_repeats`), split into same-discovery and another-discovery. Crude, cheap
  and explainable; a text embedding would be better and was not worth a dependency before there is
  data to tune it on.
- **Coverage** is pages (distinct URL paths, query and fragment dropped), areas, roles and widths per
  discovery, and the share of visits that were off the route.
- **Found by discovery.** A bug ticket filed from a discovery (`DiscoveryId` set, `IsBug`) now
  counts as found by `Discovery` in `fct_bug_links` and the Bugs section, beside QA and the owner,
  with how many were traced to a cause. The other half - bugs later traced to an area a run scouted
  without seeing them - needs areas' code paths, which is Recon's (above); the page says it comes
  with Recon.
- **Which cut a row is.** Every measure is cut by week filed, model, effort, provider, the sift or
  recheck skill version, the scout model and skill version, the chart skill version, the taste-file
  version, the A/B arm, the direction (labelled by its lineage's first title) and, for proposal rows
  only, the area. A proposal is cut by the run that *wrote* it (Sift or a Recheck round, matched by
  lease, else by time); a discovery-wide number by its Sift run. The route takes one cut at a time
  from an allowlist; anything else is the week.
- **Before and after each version.** `mart_discovery_versions` lines up each version of the sift,
  recheck, scout and chart skills and of the taste file in the order they came, with closed
  verdicts, approval rate and the share rejected for a reason the taste file can teach. A taste
  version is the one the writing run read (step 4's `DiscoveryTasteReads`, by lease, else the latest
  read in the same round); 'no file' sorts first, and a run that recorded nothing is 'not recorded'.
  The page shows each against the one before in points, only where both clear the floor.
- **Rejections say what they teach.** Each reason is shown with its share of rejections and of
  closed verdicts, and the fix the table above names for it.
- **Proven** by dbt unit tests on seeded rows (five on the intermediate models, four on the marts,
  besides schema tests), by `AnalyticsDiscoveryTests` for the route (scope, the floor passed through
  as null, cut order and the allowlist, and that `refresh.py`'s `PUBLISHED` and
  `AnalyticsService.Marts` name the same marts), and on the QA copy: five seeded discoveries on two
  projects, refreshed through the board, with the funnel, rejections, quality, cuts and taste
  versions checked by hand against the seeded rows, a project below the floor reading "too few",
  and the section at 390px wide with the page's scroll width equal to the viewport's.
- **Fell short / not done**: nothing has been measured on real verdicts yet - the live board had no
  discoveries when this was built, and its refresh still builds clean on none. No experiment has
  used approval rate as a guardrail; `mart_experiment_arms` is unchanged. Owner minutes are an
  estimate from clicks, not time on the page. Area is Sift's, not Recon's. Bugs walked past wait
  for Recon.

## Open for whoever builds it

- ~~How a proposal's clip is cut~~ - settled in "Step 3 as built": on demand, and before its recording is pruned.
- ~~What Recheck reads of the original observations~~ - settled in "Step 2 as built".
- ~~Who runs the taste fold, and what it counts~~ - settled in "Step 4 as built".
- Already decided as a decision to record: the verdict could prefill the steward's register (on a
  project that has one) from the rejection's note, rather than only listing it on the taste panel.
