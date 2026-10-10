using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Tracker;

namespace Whetstone.Api;

// ---- Request bodies ----
// Title is optional: required only when IssueKey is absent (a linked ticket's title comes from the tracker).
// MasterNotes is settable here (the REST API is the human's UI) but has no MCP tool — see Ticket.MasterNotes.
// Priority is a TicketPriority name ("High"); absent means Normal on create and unchanged on update.
public record CreateTicketRequest(string ProjectKey, string? Title, string? Description, string? IssueKey, string? IssueUrl, string? Phase,
    bool? RequiresSpeculationReview = null, bool? RequiresImplementationReview = null, string? MasterNotes = null,
    bool? SkipQaTesting = null, string? Priority = null,
    // Review track only. Track is fixed here and nowhere else: there is no endpoint that moves a
    // ticket between tracks, because one that did would leave the ticket in a phase its new track
    // does not have and every rule reading the pair would have to cope.
    string? Track = null, string? SourceBranch = null, string? TargetBranch = null,
    string? PrUrl = null, string? PrId = null, string? PrAuthor = null,
    // Absent = the project's default (ProjectConfig.SkipClassroomAnalysisByDefault), so every way a
    // ticket gets created honours it, not only the form that knows to ask.
    bool? SkipClassroomAnalysis = null);

/// <summary>Staging one piece of feedback. Everything but the body has a sensible default.</summary>
public record AddReviewCommentRequest(string Body, string? FilePath = null, int? Line = null,
    string? Side = null, string? Severity = null);

/// <summary>Editing one. Every field is optional — an absent one is left alone.</summary>
public record UpdateReviewCommentRequest(string? Body = null, string? Severity = null, string? Status = null,
    int? QaShotOrder = null, bool ClearQaShot = false);

/// <summary>What the reviewer concluded, plus an optional note posted to the ticket's timeline.</summary>
public record SetVerdictRequest(string Verdict, string? Note = null);
public record UpdateTicketRequest(string? Title, string? Description, string? IssueKey, string? IssueUrl, string? Branch, string? ProjectKey,
    bool? RequiresSpeculationReview = null, bool? RequiresImplementationReview = null, string? MasterNotes = null,
    bool? SkipQaTesting = null, string? Priority = null, bool? ShortLoopReview = null,
    bool? SkipClassroomAnalysis = null);
public record TransitionRequest(string ToPhase, string? Reason);
public record SetBranchRequest(string? Branch, string? WorktreePath);
public record OrderRequest(double BoardOrder);
public record AddWorkItemRequest(string Text);
public record UpdateWorkItemRequest(string? Text, bool? Done, int? Order);
public record AddQaItemRequest(string Text);
public record UpdateQaItemRequest(string? Text, string? Status, string? Note, int? Order);
public record AddCommentRequest(string Body, string? Author);
public record ClaimNextRequest(string[]? Projects, string[]? Priorities = null);
public record ReleaseLeasesRequest(int TicketId, string? Reason);
public record ResetLeasesRequest(int[]? KeepTicketIds);
public record AddDependencyRequest(string? BlockerIssueKey, int? BlockerTicketId, string? Note);
public record SetDependencyOverrideRequest(bool? Override, string? Note);

/// <summary>
/// One finished worker run, as the heartbeat reports it. Everything from
/// <c>ApiDurationSeconds</c> on is optional because a run that timed out or crashed never produced
/// the result event those numbers come from — and recording that run is the whole point.
/// </summary>
public record CycleReportRequest(
    int TicketId, string? Project, string Phase, string? Skill, bool Resumed, string? Model,
    DateTime? StartedAt, double DurationSeconds, int Mutations, string Outcome,
    double? ApiDurationSeconds = null, int? NumTurns = null,
    int? InputTokens = null, int? OutputTokens = null,
    int? CacheReadTokens = null, int? CacheCreationTokens = null);

public static class TicketEndpoints
{
    /// <summary>
    /// Parse a priority name off a request body. Returns false only for a value that was supplied
    /// and is not one of the five — a missing one is legal and yields null (create: Normal;
    /// update: leave it alone). Enum.TryParse alone would not do: it happily accepts any number in
    /// range of the underlying int, so "9" would become a priority that outranks Critical.
    /// </summary>
    private static bool TryParsePriority(string? name, out TicketPriority? priority)
    {
        priority = null;
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (!Enum.TryParse<TicketPriority>(name.Trim(), true, out var p) || !Enum.IsDefined(p)) return false;
        priority = p;
        return true;
    }

    /// <summary>
    /// Tidy a key a human typed, using the rules of the tracker that has to resolve it.
    /// <para>
    /// Returns false when the tracker will not recognise the value at all - which on Azure DevOps
    /// is any key that is not a bare work item id. Refusing beats storing: a ticket carrying a key
    /// its own tracker cannot look up is one that shows a title of "PROJ-123" on an Azure Boards
    /// project and resolves to nothing for the rest of its life.
    /// </para>
    /// </summary>
    private static bool TryNormalizeIssueKey(IIssueTracker tracker, string? raw, out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;   // absent is legal; it means "unlinked"
        key = tracker.NormalizeKey(raw);
        return key is not null;
    }

    /// <summary>Build the canonical browse URL for an issue, the way its own tracker addresses one.</summary>
    private static string? DeriveIssueUrl(IIssueTracker tracker, string issueKey) => tracker.IssueUrl(issueKey);

    public static void MapTicketEndpoints(this WebApplication app)
    {
        // ---- Board metadata -------------------------------------------------

        app.MapGet("/api/phases", () =>
        {
            var phases = Enum.GetValues<TicketPhase>().Select(p => new
            {
                name = p.ToString(),
                kind = p == TicketPhase.Backlog ? "backlog"
                     : p == TicketPhase.Blocked ? "blocked"
                     : PhaseMachine.IsTerminal(p) ? "terminal"
                     : PhaseMachine.IsInProgress(p) ? "active"
                     : PhaseMachine.IsActionable(p) ? "queue"
                     : "gate",
                // Board metadata is track-agnostic: it describes the machine, not one ticket. The
                // union is what lets the client draw both tracks' columns from a single request;
                // per-ticket legality is answered by the ticket's own nextPhases, which IS filtered.
                nextPhases = PhaseMachine.AllNextPhases(p).Select(n => n.ToString()).ToArray(),
                track = PhaseMachine.TrackOf(p)?.ToString(),
            });
            return Results.Ok(phases);
        });

        // ---- Heartbeat --------------------------------------------------------

        // Atomically claim the next ticket for the one global heartbeat: most-progressed
        // workable ticket across all projects (or the given subset). Locks it into its "-ing"
        // phase and returns everything the orchestrator needs to launch the worker.
        app.MapPost("/api/heartbeat/claim-next", async (ClaimNextRequest? req, TicketService svc, DashboardPaths paths, ProjectRegistry registry, LinkedIssueResolver resolver, IssueSnapshotService snapshots, TicketDependencyService deps, TicketFileStore ticketFiles, IssueTrackerFactory trackers) =>
        {
            bool SkillExists(string projectKey, string skill) => File.Exists(paths.SkillFile(projectKey, skill));

            var global = registry.GetGlobalConfig();

            // Reliability guard: block a ticket after this many no-progress resume attempts.
            var maxAttempts = global.TryGetValue("heartbeatMaxAttempts", out var ma) && ma.ValueKind == JsonValueKind.Number
                ? (int)ma.GetDouble() : 3;

            // Eligible-project filter: the request wins; otherwise fall back to the configured
            // set (chosen on the Heartbeat page). Empty/absent means all projects. Reading it
            // here per-call means changing the selection takes effect on the next cycle — no restart.
            var filter = req?.Projects;
            if ((filter is null || filter.Length == 0)
                && global.TryGetValue("heartbeatProjects", out var hp) && hp.ValueKind == JsonValueKind.Array)
            {
                filter = hp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }

            // Eligible-band filter, read exactly like the project one and meaning the same thing:
            // the request wins, the stored set is the fallback, empty/absent is everything. An
            // unrecognised name is dropped rather than failing the claim — the alternative is a
            // typo in a hand-edited config stopping the fleet with a 400 nobody is watching for.
            // If every name is junk the set is empty, which reads as "all bands": a filter that
            // parsed to nothing should stand the fleet down no more than an unwritten one does.
            var priorityNames = req?.Priorities;
            if ((priorityNames is null || priorityNames.Length == 0)
                && global.TryGetValue("heartbeatPriorities", out var hpr) && hpr.ValueKind == JsonValueKind.Array)
            {
                priorityNames = hpr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }
            var priorityFilter = priorityNames is null ? null : priorityNames
                .Select(n => Enum.TryParse<TicketPriority>(n, ignoreCase: true, out var p) ? (TicketPriority?)p : null)
                .Where(p => p is not null)
                .Select(p => p!.Value)
                .Distinct()
                .ToArray();

            // Re-read the blocking links before choosing, so a block cleared upstream a minute ago
            // lets its ticket through on this cycle rather than the next one. Best-effort by design:
            // an unreachable tracker leaves the last-known dependencies in place and the claim carries on.
            foreach (var depProject in (filter is { Length: > 0 } ? filter : registry.Projects.ToArray()))
            {
                try { await deps.RefreshFromTrackerAsync(depProject); }
                catch { /* dependencies are advisory; never fail a claim over them */ }
            }

            // How long a lease outlives its worker before anyone else may take the resource: the
            // configured worker timeout plus a margin for the cycle's own tidying (leftover commit,
            // squash, reflection). Short enough that a dead worker frees the QA stack on its own,
            // long enough that a slow but living one is never robbed of it mid-run.
            var timeoutMinutes = global.TryGetValue("heartbeatWorkerTimeoutMinutes", out var wt) && wt.ValueKind == JsonValueKind.Number
                ? wt.GetDouble() : 120;
            var leaseTtl = TimeSpan.FromMinutes(timeoutMinutes + 30);

            string[] ResourcesFor(string projectKey, string skill) =>
                registry.GetProjectConfig(projectKey).ResourcesFor(skill);

            var result = await svc.ClaimNextAsync(filter, SkillExists, maxAttempts, ResourcesFor, leaseTtl, priorityFilter);
            if (result is null)
                return Results.Ok(new { claimed = (object?)null });

            var key = result.Ticket.ProjectKey;
            var cfg = registry.GetProjectConfig(key);
            var tracker = trackers.For(key);
            var r = await resolver.ResolveAsync(result.Ticket);

            // What moved in the linked issue since the plan was made, rendered ready to paste into
            // the prompt. Computed here rather than left to the worker to go and check: a phase that
            // has to remember to look for a change is a phase that will miss one.
            var issueChanges = IssueSnapshotService.Render(await snapshots.DiffAsync(result.Ticket), tracker.DisplayName);

            // The other half of the block loop: a worker asked a question, a human answered it by
            // unblocking, and this is the run that has to hear the answer. Injected into the prompt
            // like the master notes rather than left in the comment timeline for the worker to go and
            // find - a starting context that has to remember to look for an answer is one that will
            // ask the same question again.
            var blockNote = TicketService.RenderBlockEpisode(await svc.LastBlockEpisodeAsync(result.Ticket.Id));

            // What the ticket's owner has said that nobody has answered. Injected here, beside the
            // block answer, for exactly the same reason it is: a note left in the comment timeline is
            // a note the run has to remember to go and look for, and the runs that most need one are
            // the runs least likely to look. Delivering it also stamps it, so the drawer can tell the
            // owner their note reached a run rather than leaving them to guess.
            // Rendered first, stamped second. The block distinguishes a note a previous run already
            // saw from one arriving fresh, so stamping before rendering would tell every first run
            // that its note had been ignored once already.
            var outstanding = await svc.UnansweredOwnerNotesAsync(result.Ticket.Id);
            var ownerNotes = TicketService.RenderOwnerNotes(outstanding, ticketFiles);
            await svc.MarkOwnerNotesDeliveredAsync(outstanding);

            return Results.Ok(new
            {
                claimed = new
                {
                    ticket = TicketMapper.ToDetail(result.Ticket, r.Title, r.Description, r.Unavailable,
                        files: ticketFiles, tracker: tracker),
                    skill = result.Skill,
                    resumed = result.Resumed,
                    leases = result.Leases,
                    issueChanges,
                    // How this project's tracker wants its issue named in a branch and in a commit
                    // subject. Resolved here rather than in the worker script because the difference
                    // is the tracker's, not the shell's: Jira reads "PROJ-123: ..." and Azure
                    // DevOps links a commit to a work item only on the literal form "AB#1234".
                    // Empty for an unlinked ticket, which is what the script already branches on.
                    issueBranchToken = result.Ticket.IssueKey is { Length: > 0 } bk ? tracker.BranchToken(bk) : "",
                    issueCommitRef = result.Ticket.IssueKey is { Length: > 0 } ck ? tracker.CommitRef(ck) : "",
                    blockNote,
                    ownerNotes,
                    project = new
                    {
                        key,
                        displayName = cfg.DisplayName,
                        projectPath = cfg.ProjectPath,
                        baseBranch = cfg.BaseBranch,
                        branchPrefix = cfg.BranchPrefix,
                        // Decides which ref the worker treats as the base: origin's when work
                        // leaves by pull request, the local one when it is merged here instead.
                        mergeInsteadOfPr = cfg.MergeInsteadOfPr && !cfg.IsTfvc,
                        // Where the worker actually runs: its own worktree under this root, never
                        // projectPath itself (which is the human's checkout of the same repo).
                        worktreeRoot = cfg.ResolvedWorktreeRoot,
                        worktreeSetup = cfg.WorktreeSetup,
                        worktreeCopyFiles = cfg.WorktreeCopyFiles,
                        skillsDir = paths.SkillsDir(key),
                        dataDir = paths.ProjectDir(key),
                        skillFile = Path.Combine(paths.SkillsDir(key), result.Skill, "skill.md"),
                        // The repo-side notes store, in the HUB repo. The worker reads the copy that
                        // rode into its worktree; the orchestrator writes accepted facts back here.
                        memoryDir = cfg.ResolvedMemoryDir,
                        memoryIndex = cfg.ResolvedMemoryIndex,
                    },
                },
            });
        });

        // What a worker run actually spent, posted by the heartbeat once the run is over. Separate
        // from /api/events on purpose: events are a feed a human reads and are trimmed as they age,
        // while these rows are the arithmetic behind every metric and have to survive.
        //
        // Fire-and-forget from the heartbeat's side, so this never fails a cycle: an unparseable
        // phase or a ticket that has since been deleted is dropped with a 200-shaped answer rather
        // than reported as an error nobody is listening for.
        app.MapPost("/api/heartbeat/cycles", async (CycleReportRequest req, MetricsService metrics) =>
        {
            if (!Enum.TryParse<TicketPhase>(req.Phase, true, out var phase))
                return Results.BadRequest(new { error = $"Unknown phase '{req.Phase}'" });

            var outcome = Enum.TryParse<CycleOutcome>(req.Outcome, true, out var o) ? o : CycleOutcome.Completed;

            var run = await metrics.RecordCycleAsync(new MetricsService.CycleReport(
                req.TicketId, req.Project ?? "", phase, req.Skill ?? "", req.Resumed, req.Model,
                req.StartedAt ?? DateTime.UtcNow, req.DurationSeconds, req.Mutations, outcome,
                req.ApiDurationSeconds, req.NumTurns, req.InputTokens, req.OutputTokens,
                req.CacheReadTokens, req.CacheCreationTokens));

            return run is null
                ? Results.Ok(new { recorded = false, reason = "ticket not found" })
                : Results.Ok(new { recorded = true, run.Id, phaseAfter = run.PhaseAfter?.ToString() });
        });

        app.MapGet("/api/projects", (ProjectRegistry registry) =>
        {
            var projects = registry.Projects.Select(key =>
            {
                var cfg = registry.GetProjectConfig(key);
                // Which lanes this project's board draws. Presentation only - a ticket carries its own
                // track, and this says which of them the reader is shown; see ProjectConfig.Tracks.
                return new
                {
                    key,
                    displayName = string.IsNullOrEmpty(cfg.DisplayName) ? key : cfg.DisplayName,
                    tracks = cfg.Tracks.Select(t => t.ToString()).ToArray(),
                    // "git" or "tfvc": whether this project's tickets are delivered by pull request or
                    // by TFVC check-in, which decides the board's git buttons and the dialog they open.
                    repoType = cfg.RepoType,
                    // What a new ticket's "skip classroom analysis" box starts as for this project.
                    skipClassroomAnalysisByDefault = cfg.SkipClassroomAnalysisByDefault,
                    // A git project that delivers by merging to its base rather than by PR.
                    mergeInsteadOfPr = cfg.MergeInsteadOfPr && !cfg.IsTfvc,
                };
            });
            return Results.Ok(new { projects });
        });

        // ---- Tickets --------------------------------------------------------

        app.MapGet("/api/tickets", async (TicketService svc, LinkedIssueResolver resolver,
            TicketDependencyService deps, AnalysisQueue analyses, string? project, string? phase) =>
        {
            TicketPhase? ph = Enum.TryParse<TicketPhase>(phase, true, out var p) ? p : null;
            var tickets = await svc.ListAsync(project, ph);
            var resolved = await resolver.ResolveManyAsync(tickets);
            // One query for the whole board's blockers, not one per card. Same for the rework
            // bounces, which is what dates each ticket's QA evidence.
            var ids = tickets.Select(t => t.Id).ToList();
            var blockers = await deps.ResolveAsync(ids);
            var bounces = await svc.LastBackwardAtAsync(ids);
            // Which refinement tickets the claim is holding back for their analysis. Read off the
            // queue rather than worked out afresh, which would mean a git call per card: a ticket the
            // gate is waiting on is by construction one with an analysis queued or running.
            var (running, pending, _) = analyses.Snapshot();
            var analysing = pending.Where(q => q.WalkthroughOf is null).Select(q => q.TicketId)
                .Concat(running is { WalkthroughOf: null } c ? new[] { c.TicketId } : Array.Empty<int>())
                .ToHashSet();
            return Results.Ok(tickets.Select(t =>
            {
                var r = resolved[t.Id];
                var b = blockers.TryGetValue(t.Id, out var list) ? TicketMapper.ToBlockers(list) : null;
                return TicketMapper.ToSummary(t, r.Title, r.Unavailable, b,
                    bounces.TryGetValue(t.Id, out var at) ? at : null,
                    awaitingAnalysis: ClassroomGate.Applies(t) && analysing.Contains(t.Id));
            }));
        });

        // ---- Metrics --------------------------------------------------------

        app.MapGet("/api/metrics", async (MetricsService metrics, string? project, int? days) =>
            Results.Ok(await metrics.SummaryAsync(project, days ?? 30)));

        app.MapGet("/api/metrics/cycles", async (MetricsService metrics, string? project, int? limit) =>
            Results.Ok((await metrics.RecentCyclesAsync(project, limit ?? 25)).Select(c => new
            {
                c.Id, c.TicketId, c.ProjectKey, Phase = c.Phase.ToString(), c.Skill, c.Resumed, c.Model,
                c.StartedAt, c.DurationSeconds, c.ApiDurationSeconds, c.NumTurns,
                c.InputTokens, c.OutputTokens, c.CacheReadTokens, c.CacheCreationTokens,
                c.Mutations, Outcome = c.Outcome.ToString(), PhaseAfter = c.PhaseAfter?.ToString(),
            })));

        // ---- Resource leases ------------------------------------------------

        // Give back whatever this ticket's run was holding. Keyed by ticket rather than by lease so
        // it is safe to call twice, and so a worker that died without reporting anything is still
        // cleaned up by whatever mentions the ticket next. The expiry is the backstop under both.
        app.MapPost("/api/heartbeat/leases/release", async (ReleaseLeasesRequest req, ResourceLeaseService leases) =>
            Results.Ok(new { released = await leases.ReleaseForTicketAsync(req.TicketId, req.Reason) }));

        // A run the provider refused before it could start (the session limit) gives its attempt
        // back, so a limit cannot walk a ticket into the attempt cap. See RefundAttemptAsync.
        app.MapPost("/api/heartbeat/refund-attempt", async (ReleaseLeasesRequest req, TicketService svc) =>
            await svc.RefundAttemptAsync(req.TicketId, req.Reason) is { } attempts
                ? Results.Ok(new { attempts })
                : Results.NotFound());

        // Wipe the board clean, minus whatever the caller says is still genuinely in use. The
        // orchestrator calls this as it starts, when almost nothing can legitimately be held: it has
        // just taken the singleton lock and has not launched an agent yet, so a live lease is
        // normally the residue of a run that was killed rather than finished. Without it, a hard stop
        // leaves each in-flight ticket behind its own lease for the length of the TTL, and the
        // restarted fleet looks like it is ignoring the front of its own queue.
        //
        // "Almost" nothing, because workers are separate processes: an orchestrator that died without
        // taking them with it leaves them running, and wiping their leases would hand a live worktree
        // to a second agent. The orchestrator scans for those survivors first and sends them here to
        // be spared - see KeepTicketIds and the adoption pass in heartbeat.ps1.
        app.MapPost("/api/heartbeat/leases/reset", async (ResetLeasesRequest? req, ResourceLeaseService leases) =>
            Results.Ok(new
            {
                released = await leases.ReleaseAllAsync(
                    "orchestrator restarted - no agent could still be holding this", req?.KeepTicketIds),
            }));

        // Who holds what right now - the answer to "why has nothing picked up that QA ticket".
        app.MapGet("/api/leases", async (ResourceLeaseService leases) =>
            Results.Ok((await leases.ActiveAsync()).Select(l => new
            {
                l.Id, l.ResourceKey, l.Slot, l.TicketId, l.Skill, l.AcquiredAt, l.ExpiresAt,
            })));

        // ---- Dependencies ---------------------------------------------------

        app.MapGet("/api/tickets/{id:int}/dependencies", async (int id, TicketDependencyService deps) =>
        {
            var resolved = await deps.ResolveAsync(new[] { id });
            return Results.Ok(resolved.TryGetValue(id, out var list)
                ? TicketMapper.ToBlockers(list) : new List<BlockerDto>());
        });

        // Record a block someone found while working. The tracker never has to know about it.
        app.MapPost("/api/tickets/{id:int}/dependencies", async (int id, AddDependencyRequest req, TicketDependencyService deps) =>
        {
            var dep = await deps.AddLocalAsync(id, req.BlockerIssueKey, req.BlockerTicketId, req.Note);
            return dep is null
                ? Results.BadRequest(new { error = "Need a blocker (issue key or #ticket id) on a ticket that exists, and a ticket cannot wait on itself." })
                : Results.Ok(new { dep.Id });
        });

        // The human verdict that outranks the tracker. null = follow the tracker again.
        app.MapPut("/api/dependencies/{depId:int}", async (int depId, SetDependencyOverrideRequest req, TicketDependencyService deps) =>
            await deps.SetOverrideAsync(depId, req.Override, req.Note)
                ? Results.Ok(new { updated = true }) : Results.NotFound());

        app.MapDelete("/api/dependencies/{depId:int}", async (int depId, TicketDependencyService deps) =>
            await deps.DeleteAsync(depId) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        // Re-read one project's "is blocked by" links now, rather than waiting for the next claim.
        app.MapPost("/api/projects/{key}/dependencies/refresh", async (string key, TicketDependencyService deps) =>
            Results.Ok(new { changed = await deps.RefreshFromTrackerAsync(key) }));

        app.MapGet("/api/tickets/{id:int}", async (int id, TicketService svc, LinkedIssueResolver resolver,
            TicketDependencyService deps, ProjectRegistry registry, IssueTrackerFactory trackers) =>
        {
            var t = await svc.GetAsync(id);
            if (t is null) return Results.NotFound();
            var r = await resolver.ResolveAsync(t);
            var resolved = await deps.ResolveAsync(new[] { id });
            var blockers = resolved.TryGetValue(id, out var list) ? TicketMapper.ToBlockers(list) : null;
            // The drawer is the only place the learning brief is read, and it always loads from
            // here, so this is the one call site that needs the project's heading for it.
            var label = registry.GetProjectConfig(t.ProjectKey).ResolvedLearningLabel;
            return Results.Ok(TicketMapper.ToDetail(t, r.Title, r.Description, r.Unavailable, blockers, label,
                tracker: trackers.For(t.ProjectKey), lastBackwardAt: await svc.LastBackwardAtAsync(id)));
        });

        app.MapPost("/api/tickets", async (CreateTicketRequest req, TicketService svc, ProjectRegistry registry,
            LinkedIssueResolver resolver, IssueTrackerFactory trackers) =>
        {
            // Named, never inferred. A ticket carries its project for the rest of its life - it
            // decides the repo, the branch, the tracker and the skills - so a create that did not
            // say which one is a question, not a default.
            var projectKey = req.ProjectKey?.Trim() ?? "";
            if (!registry.IsEnabled(projectKey))
                return Results.BadRequest(new { error = $"Unknown or missing project '{projectKey}'" });

            var phase = TicketPhase.Backlog;
            if (!string.IsNullOrEmpty(req.Phase) && !Enum.TryParse(req.Phase, true, out phase))
                return Results.BadRequest(new { error = $"Unknown phase '{req.Phase}'" });

            if (!TryParsePriority(req.Priority, out var priority))
                return Results.BadRequest(new { error = $"Unknown priority '{req.Priority}'" });

            var tracker = trackers.For(projectKey);
            if (!TryNormalizeIssueKey(tracker, req.IssueKey, out var issueKey))
                return Results.BadRequest(new { error = $"'{req.IssueKey}' is not a valid {tracker.DisplayName} {tracker.IssueNoun} id." });

            // Which pipeline. Fixed here and never afterwards — see CreateTicketRequest.
            var track = TicketTrack.Delivery;
            if (!string.IsNullOrWhiteSpace(req.Track)
                && !Enum.TryParse(req.Track, ignoreCase: true, out track))
                return Results.BadRequest(new { error = $"Unknown track '{req.Track}'" });

            // A ticket created straight into a phase its own track does not have would be unmovable
            // from the moment it existed - every edge out of it is filtered by the same rule.
            if (!PhaseMachine.IsOnTrack(phase, track))
                return Results.BadRequest(new { error = $"A {track} ticket cannot start in {phase}." });

            ReviewTarget? reviewTarget = null;
            var title = req.Title;
            if (track == TicketTrack.Review)
            {
                if (string.IsNullOrWhiteSpace(req.SourceBranch))
                    return Results.BadRequest(new { error = "A peer review ticket needs the branch it is reviewing." });

                reviewTarget = new ReviewTarget
                {
                    SourceBranch = req.SourceBranch.Trim(),
                    TargetBranch = string.IsNullOrWhiteSpace(req.TargetBranch) ? null : req.TargetBranch.Trim(),
                    PrUrl = string.IsNullOrWhiteSpace(req.PrUrl) ? null : req.PrUrl.Trim(),
                    PrId = string.IsNullOrWhiteSpace(req.PrId) ? null : req.PrId.Trim(),
                    PrAuthor = string.IsNullOrWhiteSpace(req.PrAuthor) ? null : req.PrAuthor.Trim(),
                };

                // A review ticket always has SOMETHING to be called - the branch, at worst - so it
                // never needs to refuse over a missing title the way a standalone delivery ticket
                // does. Only applied when nothing better was given, and never over a linked issue,
                // whose own title stays the source of truth.
                if (issueKey is null && string.IsNullOrWhiteSpace(title))
                    title = $"Review: {reviewTarget.SourceBranch}";
            }

            // Title required only for standalone tickets; a linked one pulls it from the tracker.
            if (issueKey is null && string.IsNullOrWhiteSpace(title))
                return Results.BadRequest(new { error = "Title is required when no issue is linked." });

            // Derive the browse URL from the project's own tracker when none was supplied.
            var issueUrl = req.IssueUrl;
            if (issueKey != null && string.IsNullOrWhiteSpace(issueUrl))
                issueUrl = DeriveIssueUrl(tracker, issueKey);

            var t = await svc.CreateAsync(projectKey, title, req.Description, issueKey, issueUrl, phase,
                req.RequiresSpeculationReview ?? false, req.RequiresImplementationReview ?? false, req.MasterNotes,
                req.SkipQaTesting ?? false, priority ?? TicketPriority.Normal, track, reviewTarget,
                req.SkipClassroomAnalysis ?? registry.GetProjectConfig(projectKey).SkipClassroomAnalysisByDefault);
            var r = await resolver.ResolveAsync(t);
            return Results.Created($"/api/tickets/{t.Id}",
                TicketMapper.ToDetail(t, r.Title, r.Description, r.Unavailable, tracker: tracker));
        });

        app.MapPut("/api/tickets/{id:int}", async (int id, UpdateTicketRequest req, TicketService svc,
            ProjectRegistry registry, LinkedIssueResolver resolver, IssueTrackerFactory trackers) =>
        {
            if (req.ProjectKey is not null && !registry.Projects.Contains(req.ProjectKey))
                return Results.BadRequest(new { error = $"Unknown project '{req.ProjectKey}'" });
            if (!TryParsePriority(req.Priority, out var priority))
                return Results.BadRequest(new { error = $"Unknown priority '{req.Priority}'" });

            var cur = await svc.GetAsync(id);
            if (cur is null) return Results.NotFound();

            // The tracker of the project the ticket will END UP on, since a move is legal here and
            // the two projects need not share one.
            var tracker = trackers.For(req.ProjectKey ?? cur.ProjectKey);

            // Resulting link: IssueKey null = unchanged, "" = unlink, value = (re)link.
            if (!TryNormalizeIssueKey(tracker, req.IssueKey, out var normalized))
                return Results.BadRequest(new { error = $"'{req.IssueKey}' is not a valid {tracker.DisplayName} {tracker.IssueNoun} id." });
            var resulting = req.IssueKey is null ? cur.IssueKey : normalized;

            // A standalone (unlinked) ticket must have a title.
            if (resulting is null && string.IsNullOrWhiteSpace(req.Title ?? cur.Title))
                return Results.BadRequest(new { error = "A title is required when the ticket is not linked to an issue." });

            // Keep the URL in sync with the link: derive on (re)link, clear on unlink.
            var issueUrl = req.IssueUrl;
            if (req.IssueKey is not null)
                issueUrl = resulting is null
                    ? ""  // unlink -> clear the stale URL (UpdateAsync maps "" to null)
                    : (string.IsNullOrWhiteSpace(issueUrl) ? DeriveIssueUrl(tracker, resulting) : issueUrl);

            // Pass the NORMALISED key on, not the raw one - "" still means unlink, and anything else
            // has to reach the database in the form the resolver will later look it up by.
            var t = await svc.UpdateAsync(id, req.Title, req.Description,
                req.IssueKey is null ? null : (normalized ?? ""),
                issueUrl, req.Branch, req.ProjectKey,
                req.RequiresSpeculationReview, req.RequiresImplementationReview, req.MasterNotes, req.SkipQaTesting,
                priority, req.ShortLoopReview, req.SkipClassroomAnalysis);
            if (t is null) return Results.NotFound();
            var r = await resolver.ResolveAsync(t);
            return Results.Ok(TicketMapper.ToDetail(t, r.Title, r.Description, r.Unavailable, tracker: tracker));
        });

        app.MapDelete("/api/tickets/{id:int}", async (int id, TicketService svc) =>
            await svc.DeleteAsync(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        app.MapPost("/api/tickets/{id:int}/transition", async (int id, TransitionRequest req, TicketService svc) =>
        {
            if (!Enum.TryParse<TicketPhase>(req.ToPhase, true, out var to))
                return Results.BadRequest(new { error = $"Unknown phase '{req.ToPhase}'" });
            try
            {
                // Everything that reaches the REST API is a person on the board - the heartbeat's own
                // moves come through the MCP server or through TicketService directly.
                var t = await svc.TransitionAsync(id, to, req.Reason, TransitionActor.Human);
                return t is null ? Results.NotFound() : Results.Ok(TicketMapper.ToDetail(t));
            }
            catch (InvalidTransitionException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    allowed = ex.Allowed.Select(p => new NextPhaseDto(p.ToString(), PhaseMachine.Meaning(ex.From, p))),
                });
            }
        });

        app.MapPut("/api/tickets/{id:int}/order", async (int id, OrderRequest req, TicketService svc) =>
            await svc.SetOrderAsync(id, req.BoardOrder) ? Results.Ok(new { updated = true }) : Results.NotFound());

        // Record the ticket's feature branch and, optionally, the worktree it is checked out in.
        // Deliberately narrow (the branch half mirrors the set_branch MCP tool) so the heartbeat's
        // deterministic workspace setup can persist both without touching title/description/issue link.
        app.MapPost("/api/tickets/{id:int}/branch", async (int id, SetBranchRequest req, TicketService svc) =>
        {
            var t = req.Branch is null ? await svc.GetAsync(id)
                                       : await svc.UpdateAsync(id, null, null, null, null, req.Branch, null);
            if (t is null) return Results.NotFound();
            if (req.WorktreePath is not null) t = await svc.SetWorktreePathAsync(id, req.WorktreePath);
            return t is null ? Results.NotFound() : Results.Ok(new { t.Id, t.Branch, t.WorktreePath });
        });

        // ---- QA shots (screenshots streamed from disk) ----------------------

        // GET the stored screenshot bytes for a ticket's shot slot. Referenced by QaShotDto.ImageUrl,
        // which stamps a ?v= on it - see there for why the slot route alone is not enough.
        app.MapGet("/api/tickets/{id:int}/qa-shots/{order:int}", async (int id, int order, string? v,
            HttpContext ctx, TicketService svc, QaShotStore store) =>
        {
            var t = await svc.GetAsync(id);
            var shot = t?.QaShots.FirstOrDefault(s => s.Order == order);
            if (shot is null) return Results.NotFound();
            var disk = store.DiskPath(id, shot.ImageFile);
            if (disk is null) return Results.NotFound();

            // A stamped URL names one immutable set of bytes - replacing a ticket's shots mints new
            // stamps - so a cached copy under it can never go stale and is worth keeping for good.
            // An unstamped one carries no such promise (a page opened before this change, or the
            // route typed by hand), and must go back to the server every time rather than inherit
            // the browser's guess at a lifetime.
            ctx.Response.Headers.CacheControl = string.IsNullOrEmpty(v)
                ? "no-cache"
                : "public, max-age=31536000, immutable";
            return Results.File(disk, QaShotStore.ContentType(shot.ImageFile));
        });

        // ---- Work items -----------------------------------------------------

        app.MapPost("/api/tickets/{id:int}/workitems", async (int id, AddWorkItemRequest req, TicketService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Text is required" });
            var wi = await svc.AddWorkItemAsync(id, req.Text);
            return wi is null ? Results.NotFound() : Results.Ok(new WorkItemDto(wi.Id, wi.Text, wi.Done, wi.Order, wi.StartedAt, wi.Scope.ToString()));
        });

        app.MapPut("/api/workitems/{id:int}", async (int id, UpdateWorkItemRequest req, TicketService svc) =>
        {
            var wi = await svc.UpdateWorkItemAsync(id, req.Text, req.Done, req.Order);
            return wi is null ? Results.NotFound() : Results.Ok(new WorkItemDto(wi.Id, wi.Text, wi.Done, wi.Order, wi.StartedAt, wi.Scope.ToString()));
        });

        app.MapDelete("/api/workitems/{id:int}", async (int id, TicketService svc) =>
            await svc.DeleteWorkItemAsync(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        // ---- QA items -------------------------------------------------------

        app.MapPost("/api/tickets/{id:int}/qaitems", async (int id, AddQaItemRequest req, TicketService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Text is required" });
            var qi = await svc.AddQaItemAsync(id, req.Text);
            return qi is null ? Results.NotFound() : Results.Ok(new QaItemDto(qi.Id, qi.Text, qi.Status.ToString(), qi.Note, qi.Order, qi.StartedAt));
        });

        app.MapPut("/api/qaitems/{id:int}", async (int id, UpdateQaItemRequest req, TicketService svc) =>
        {
            QaStatus? status = null;
            if (req.Status is not null)
            {
                if (!Enum.TryParse<QaStatus>(req.Status, true, out var st))
                    return Results.BadRequest(new { error = $"Unknown QA status '{req.Status}'" });
                status = st;
            }
            var qi = await svc.UpdateQaItemAsync(id, req.Text, status, req.Note, req.Order);
            return qi is null ? Results.NotFound() : Results.Ok(new QaItemDto(qi.Id, qi.Text, qi.Status.ToString(), qi.Note, qi.Order, qi.StartedAt));
        });

        app.MapDelete("/api/qaitems/{id:int}", async (int id, TicketService svc) =>
            await svc.DeleteQaItemAsync(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        // ---- Comments -------------------------------------------------------

        app.MapPost("/api/tickets/{id:int}/comments", async (int id, AddCommentRequest req, TicketService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body)) return Results.BadRequest(new { error = "Body is required" });
            var author = AuthorKind.Human;
            if (!string.IsNullOrEmpty(req.Author) && !Enum.TryParse(req.Author, true, out author))
                return Results.BadRequest(new { error = $"Unknown author '{req.Author}'" });

            var c = await svc.AddCommentAsync(id, req.Body, author);
            return c is null ? Results.NotFound() : Results.Ok(new CommentDto(c.Id, c.Author.ToString(), c.Body, c.Phase?.ToString(), c.CreatedAt));
        });

        // ---- Owner notes: the human → worker channel -------------------------
        //
        // Separate from the JSON /comments route above rather than replacing it, because they are
        // different things wearing the same word. That one posts a note; this one posts a note WITH
        // FILES, which has to be multipart and therefore cannot share a shape with it.

        // Post a note as the ticket's owner, with up to MaxPerNote files attached. Every note posted
        // through here is a directive: it is delivered to the next run that claims the ticket and
        // holds that run's hand-off until it is answered.
        app.MapPost("/api/tickets/{id:int}/notes", async (int id, HttpRequest http,
            TicketService svc, TicketFileStore files) =>
        {
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Expected a multipart form" });
            var form = await http.ReadFormAsync();

            var body = form["body"].ToString();
            if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest(new { error = "Body is required" });

            var (saved, error) = await SaveUploads(form.Files, id, files);
            if (error is not null) return Results.BadRequest(new { error });

            var c = await svc.AddCommentAsync(id, body, AuthorKind.Human, saved);
            if (c is null)
            {
                // The ticket vanished between the upload and the write. Nothing references these
                // bytes now, and an orphaned file in a per-ticket folder is invisible forever.
                foreach (var a in saved) files.Delete(a);
                return Results.NotFound();
            }
            return Results.Ok(ToCommentDto(id, c));
        });

        // The stored bytes of one note attachment. Referenced by NoteAttachmentDto.Url.
        app.MapGet("/api/tickets/{id:int}/files/{fileId:int}", async (int id, int fileId,
            DashboardDbContext db, TicketFileStore files) =>
        {
            // Matched on the ticket as well as the id, so a URL cannot walk from one ticket's file
            // to another's by guessing a number.
            var a = await db.NoteAttachments.FirstOrDefaultAsync(x => x.Id == fileId && x.TicketId == id);
            if (a is null) return Results.NotFound();
            var path = files.PathFor(a);
            if (!File.Exists(path)) return Results.NotFound();
            // Inline so a screenshot renders in the drawer instead of downloading; the stored
            // content type is never the uploader's claim (see TicketFileStore.AllowedTypes).
            return Results.File(path, a.ContentType, enableRangeProcessing: true);
        });

        // The review gate's change request: the note, its files, the list of what to change and the
        // route home, in one call. See TicketService.RequestChangesAsync for why it is one call.
        app.MapPost("/api/tickets/{id:int}/request-changes", async (int id, HttpRequest http,
            TicketService svc, TicketFileStore files, LinkedIssueResolver resolver) =>
        {
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Expected a multipart form" });
            var form = await http.ReadFormAsync();

            var body = form["body"].ToString();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Say what you want changed — the note is what the next run works from." });

            var items = form["changeItems"].Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
            var shortLoop = form["shortLoop"].ToString() is "true" or "on" or "1";
            var replan = form["replan"].ToString() is "true" or "on" or "1";
            // Absent means "leave the band where it is" — the same shape as the update endpoint, so
            // a reviewer who says nothing about priority does not silently reset it to Normal.
            if (!TryParsePriority(form["priority"].ToString(), out var priority))
                return Results.BadRequest(new { error = $"Unknown priority '{form["priority"]}'" });

            var (saved, error) = await SaveUploads(form.Files, id, files);
            if (error is not null) return Results.BadRequest(new { error });

            try
            {
                var t = await svc.RequestChangesAsync(id, body, items, shortLoop, saved, priority, replan);
                if (t is null)
                {
                    foreach (var a in saved) files.Delete(a);
                    return Results.NotFound();
                }
                var r = await resolver.ResolveAsync(t);
                return Results.Ok(TicketMapper.ToDetail(t, r.Title, r.Description, r.Unavailable));
            }
            catch (InvalidTransitionException ex)
            {
                foreach (var a in saved) files.Delete(a);
                return Results.BadRequest(new { error = ex.Message, allowed = ex.Allowed.Select(p => p.ToString()) });
            }
            catch (InvalidOperationException ex)
            {
                foreach (var a in saved) files.Delete(a);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- Peer review: staging, curating, and sending the feedback ---------
        //
        // The MCP server can reach the first two of these and NOT the third. That asymmetry is the
        // whole safety story of this track: a worker stages drafts, and only a person sends them.

        app.MapPost("/api/tickets/{id:int}/review/comments", async (
            int id, AddReviewCommentRequest req, TicketService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { error = "A review comment needs a body." });
            if (!TryParseEnum<ReviewSeverity>(req.Severity, ReviewSeverity.Question, out var severity))
                return Results.BadRequest(new { error = $"Unknown severity '{req.Severity}'" });
            if (!TryParseEnum<DiffSide>(req.Side, DiffSide.Head, out var side))
                return Results.BadRequest(new { error = $"Unknown side '{req.Side}'" });

            var c = await svc.AddReviewCommentAsync(id, req.Body, severity, AuthorKind.Human,
                req.FilePath, req.Line, side);
            return c is null ? Results.NotFound() : Results.Ok(new { c.Id });
        });

        app.MapPut("/api/tickets/{id:int}/review/comments/{commentId:int}", async (
            int id, int commentId, UpdateReviewCommentRequest req, TicketService svc) =>
        {
            ReviewSeverity? severity = null;
            if (req.Severity is not null)
            {
                if (!TryParseEnum<ReviewSeverity>(req.Severity, ReviewSeverity.Question, out var s))
                    return Results.BadRequest(new { error = $"Unknown severity '{req.Severity}'" });
                severity = s;
            }

            ReviewCommentStatus? status = null;
            if (req.Status is not null)
            {
                if (!TryParseEnum<ReviewCommentStatus>(req.Status, ReviewCommentStatus.Draft, out var st))
                    return Results.BadRequest(new { error = $"Unknown status '{req.Status}'" });
                // Submitted is recorded by the send, never chosen: setting it by hand would say
                // feedback reached the pull request when nothing had left this machine.
                if (st == ReviewCommentStatus.Submitted)
                    return Results.BadRequest(new { error = "Submitted is recorded by sending the review, not set directly." });
                status = st;
            }

            // Validated against the set that is actually attached: a citation naming a slot that is
            // not there draws as "the picture moved" in the drawer, which is a true thing to say
            // about a set QA later replaced and a misleading one to say about a typo.
            if (req.QaShotOrder is int slot && !req.ClearQaShot)
            {
                var ticket = await svc.GetAsync(id);
                if (ticket is null) return Results.NotFound();
                if (ticket.QaShots.All(s => s.Order != slot))
                    return Results.BadRequest(new
                    {
                        error = $"This ticket has no QA shot in slot {slot}.",
                        attached = ticket.QaShots.Select(s => s.Order).OrderBy(o => o).ToArray(),
                    });
            }

            var c = await svc.UpdateReviewCommentAsync(commentId, req.Body, severity, status,
                req.QaShotOrder, req.ClearQaShot);
            return c is null ? Results.NotFound() : Results.Ok(new { c.Id, status = c.Status.ToString(), c.QaShotOrder });
        });

        app.MapDelete("/api/tickets/{id:int}/review/comments/{commentId:int}", async (
            int id, int commentId, TicketService svc) =>
            await svc.DeleteReviewCommentAsync(commentId) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/tickets/{id:int}/review/verdict", async (
            int id, SetVerdictRequest req, TicketService svc) =>
        {
            if (!TryParseEnum<ReviewVerdict>(req.Verdict, ReviewVerdict.ApproveWithComments, out var verdict)
                || string.IsNullOrWhiteSpace(req.Verdict))
                return Results.BadRequest(new { error = $"Unknown verdict '{req.Verdict}'" });

            var r = await svc.SetReviewVerdictAsync(id, verdict);
            if (r is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(req.Note))
                await svc.AddCommentAsync(id, req.Note.Trim(), AuthorKind.Human);
            return Results.Ok(new { verdict = verdict.ToString(), r.VerdictAt });
        });

        // Recording that the owner sent the feedback. In v1 the sending itself happens in their
        // browser - they copy the markdown below and paste it into the pull request - so this marks
        // what they did rather than doing it. When an Azure DevOps client lands, the posting goes
        // HERE, behind this same endpoint, and no worker gains a route to it.
        app.MapPost("/api/tickets/{id:int}/review/submit", async (int id, TicketService svc) =>
        {
            var t = await svc.GetAsync(id);
            if (t is null) return Results.NotFound();
            if (t.Track != TicketTrack.Review)
                return Results.BadRequest(new { error = "This is not a peer review ticket." });

            var r = await svc.MarkReviewSubmittedAsync(id);
            if (r is null) return Results.BadRequest(new { error = "This ticket has no review target." });
            return Results.Ok(new { r.SubmittedAt });
        });

        // The v1 send: the whole review as markdown, ready to paste into the pull request. Worth
        // having even once posting is automated - a reviewer who wants to reword something in the
        // host's own editor should not have to fight the tool for the text.
        app.MapGet("/api/tickets/{id:int}/review/markdown", async (
            int id, TicketService svc, LinkedIssueResolver resolver) =>
        {
            var t = await svc.GetAsync(id);
            if (t is null) return Results.NotFound();
            if (t.Track != TicketTrack.Review)
                return Results.BadRequest(new { error = "This is not a peer review ticket." });

            var r = await resolver.ResolveAsync(t);
            return Results.Ok(new { markdown = ReviewMarkdown.Compose(t, r.Title) });
        });
    }

    /// <summary>
    /// Parse an enum name, treating null/blank as <paramref name="fallback"/> rather than as an
    /// error — every one of these fields is optional and has a sensible default.
    /// </summary>
    private static bool TryParseEnum<T>(string? raw, T fallback, out T value) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) { value = fallback; return true; }
        return Enum.TryParse(raw, ignoreCase: true, out value);
    }

    /// <summary>
    /// Copy an upload set into the file store, or say why not. Nothing is written unless every file
    /// is acceptable: a note that half-arrived is worse than one that was refused, because the owner
    /// believes the pipeline has the screenshot it does not have.
    /// </summary>
    private static async Task<(List<NoteAttachment> Saved, string? Error)> SaveUploads(
        IFormFileCollection uploads, int ticketId, TicketFileStore files)
    {
        if (uploads.Count == 0) return (new List<NoteAttachment>(), null);
        if (uploads.Count > TicketFileStore.MaxPerNote)
            return (new List<NoteAttachment>(), $"{uploads.Count} files is more than one note can carry "
                                              + $"(limit {TicketFileStore.MaxPerNote}).");

        var saved = new List<NoteAttachment>();
        foreach (var f in uploads)
        {
            try
            {
                await using var stream = f.OpenReadStream();
                saved.Add(await files.SaveAsync(ticketId, f.FileName, stream, f.Length));
            }
            catch (Exception ex)
            {
                foreach (var a in saved) files.Delete(a);
                return (new List<NoteAttachment>(), ex.Message);
            }
        }
        return (saved, null);
    }

    internal static CommentDto ToCommentDto(int ticketId, Comment c) => new(
        c.Id, c.Author.ToString(), c.Body, c.Phase?.ToString(), c.CreatedAt,
        c.Attachments.OrderBy(a => a.Id).Select(a => new NoteAttachmentDto(
            a.Id, a.FileName, $"/api/tickets/{ticketId}/files/{a.Id}", "", a.Bytes, a.IsImage)).ToList(),
        c.AcknowledgedAt is not null, c.Acknowledgement, c.DeliveredAt, c.AcknowledgedAt);
}
