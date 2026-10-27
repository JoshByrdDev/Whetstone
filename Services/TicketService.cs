using System.Security.Cryptography;
using System.Text;
using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>Thrown when a phase transition violates the state machine.</summary>
public class InvalidTransitionException : Exception
{
    public TicketPhase From { get; }
    public IReadOnlyList<TicketPhase> Allowed { get; }
    public InvalidTransitionException(string message, TicketPhase from, IEnumerable<TicketPhase> allowed) : base(message)
        => (From, Allowed) = (from, allowed.ToList());
}

/// <summary>
/// Thrown when the move itself is legal but the ticket is not carrying what that hand-off requires
/// (see <see cref="HandoffRequirements"/>) — a refine pass with no learning brief, a QA pass with
/// neither screenshots nor a reason for having none.
/// <para>
/// Deliberately a SUBCLASS of <see cref="InvalidTransitionException"/> so every existing caller
/// still handles it and reports it as a refused move; callers that want to say something more
/// precise catch this first. <see cref="InvalidTransitionException.Allowed"/> still lists the real
/// alternatives, because "go back for rework instead" is a genuine answer to this refusal.
/// </para>
/// </summary>
public class HandoffRequirementException : InvalidTransitionException
{
    /// <summary>The phase that was refused — still reachable, once the requirement is met.</summary>
    public TicketPhase To { get; }

    public HandoffRequirementException(string message, TicketPhase from, TicketPhase to, IEnumerable<TicketPhase> allowed)
        : base(message, from, allowed) => To = to;
}

/// <summary>
/// The single implementation of every ticket operation, shared by the REST API
/// and the MCP tools so their behaviour (and the state-machine rules) can never
/// drift apart. Scoped — wraps the scoped DbContext.
/// </summary>
public class TicketService
{
    private readonly DashboardDbContext _db;

    // Optional so the state-machine tests can construct a service with no tracker at all. When it is
    // absent the ticket simply never gets a baseline, and later phases report "nothing to compare".
    private readonly IssueSnapshotService? _snapshots;

    // Also optional: without it the claim simply does not gate on dependencies, which is how the
    // loop behaved before they existed.
    private readonly TicketDependencyService? _dependencies;

    // Same story: absent, the claim takes no leases and nothing is serialised, which is exactly how
    // the loop behaved when only one worker could ever run.
    private readonly ResourceLeaseService? _leases;

    // And again: absent, a ticket in NeedsRefinement is claimed at once, with no analysis to rule on.
    private readonly IClassroomGate? _classroomGate;

    // And once more: absent, work that lands in a queue waits for the heartbeat's own timer, which
    // is how it always was.
    private readonly HeartbeatManager? _heartbeat;

    public TicketService(DashboardDbContext db, IssueSnapshotService? snapshots = null,
        TicketDependencyService? dependencies = null, ResourceLeaseService? leases = null,
        IClassroomGate? classroomGate = null, HeartbeatManager? heartbeat = null)
        => (_db, _snapshots, _dependencies, _leases, _classroomGate, _heartbeat)
         = (db, snapshots, dependencies, leases, classroomGate, heartbeat);

    /// <summary>
    /// A ticket has just become something a worker could pick up, has finished and may have freed
    /// the tickets that were waiting on it, or has arrived where a steward takes over (review, a
    /// block). A worker's own hand-off needs no help - the orchestrator is awake and polling while
    /// anything runs - so this matters for the moves made while it sleeps: a person's, the
    /// dashboard's, a steward's.
    /// </summary>
    private void WakeFor(TicketPhase phase)
    {
        if (PhaseMachine.IsActionable(phase)
            || phase is TicketPhase.Done or TicketPhase.NeedsReview or TicketPhase.Blocked)
            _heartbeat?.Wake();
    }

    // Four collections off one root, so this MUST be a split query. As a single query EF joins them
    // all together and SQLite returns the cartesian product - one row per (comment x work item x qa
    // item x shot) combination, each carrying a full copy of the comment body. A ticket with 50
    // comments, 20 work items and 11 QA items is 11,000 rows on its own, and the board asks for
    // every ticket at once: ~40,000 fat rows, ~10 seconds, to render counts. Split, each collection
    // is its own small SELECT and the same board costs a few hundred rows. The OrderBy on every
    // caller below is what makes splitting safe (EF needs a stable order to stitch the parts back).
    // Note attachments ride along as a fifth split SELECT rather than being loaded only where they
    // are rendered. They are needed on the two calls that matter most (the detail drawer and the
    // claim), an unattached note is the common case so the extra SELECT usually returns nothing, and
    // the alternative - a second loading path for tickets-with-attachments - is how a renderer ends
    // up silently dropping files nobody remembered to Include.
    // The two Review collections ride along on the same reasoning as the note attachments above:
    // they are needed on the calls that matter (the drawer, the claim, the board's gate label), the
    // empty case is the common one and costs a SELECT that returns nothing, and the alternative is a
    // second loading path that some future caller forgets to use.
    private IQueryable<Ticket> WithChildren() =>
        _db.Tickets.Include(t => t.WorkItems).Include(t => t.QaItems).Include(t => t.QaShots)
            .Include(t => t.Comments).ThenInclude(c => c.Attachments)
            .Include(t => t.ReviewTarget)
            .Include(t => t.ReviewComments)
            .Include(t => t.GateFindings)
            .AsSplitQuery();

    // ---- Queries ----

    public async Task<List<Ticket>> ListAsync(string? project, TicketPhase? phase)
    {
        var q = WithChildren();
        if (!string.IsNullOrEmpty(project)) q = q.Where(t => t.ProjectKey == project);
        if (phase is not null) q = q.Where(t => t.Phase == phase);
        return await q.OrderBy(t => t.BoardOrder).ThenByDescending(t => t.UpdatedAt).ToListAsync();
    }

    /// <summary>Tickets sitting in a "Needs*" queue phase — what the heartbeat pulls from.</summary>
    public async Task<List<Ticket>> ListActionableAsync(string? project)
    {
        var actionable = new[] { TicketPhase.NeedsSpeculation, TicketPhase.NeedsImplementation, TicketPhase.NeedsQa };
        var q = WithChildren().Where(t => actionable.Contains(t.Phase));
        if (!string.IsNullOrEmpty(project)) q = q.Where(t => t.ProjectKey == project);
        return await q.OrderBy(t => t.BoardOrder).ToListAsync();
    }

    public Task<Ticket?> GetAsync(int id) => WithChildren().FirstOrDefaultAsync(t => t.Id == id);

    // ---- Heartbeat claim (the one global, project-agnostic pickup) ----

    /// <summary>
    /// Outcome of a successful claim: the now-locked ticket, its skill, whether it was a resume, and
    /// any shared resources the run holds (which the worker must give back when it finishes).
    /// </summary>
    /// <para>
    /// <c>RunId</c> is the id of the ticket's own lease - "this run", for as long as the lease lives.
    /// A worker host names it when it hands a branch back, so a run the orchestrator has since given
    /// up on cannot overwrite what the next run did. 0 when the claim took no lease (no lease service).
    /// </para>
    /// <para>
    /// <c>StackSlot</c> is which of a resource's capacity slots the run got (0-based) - the first
    /// resource it holds beside the ticket itself. 0 for a run that holds none, and for every run on
    /// a resource whose capacity is one, which is every run unless capacity was raised for it.
    /// </para>
    public record ClaimResult(Ticket Ticket, string Skill, bool Resumed, string[] Leases, int RunId = 0, int StackSlot = 0);

    /// <summary>
    /// Claim the single most urgent workable ticket across all projects (or a filtered
    /// subset) for the heartbeat: highest <see cref="Ticket.Priority"/> band first, then within that
    /// band most-progressed first (<see cref="PhaseMachine.ProgressRank"/>), then oldest-updated.
    /// <paramref name="projectFilter"/> and <paramref name="priorityFilter"/> narrow what is
    /// eligible at all; null or empty means that axis restricts nothing.
    /// A "Needs*" ticket is locked by transitioning it to its "-ing" phase;
    /// an "-ing" ticket is resumed in place, unless another agent is already on it — every claim
    /// takes an exclusive lease on the ticket, so calling this repeatedly (which is exactly what
    /// filling several agent slots does) hands back a different ticket each time.
    /// Guards that let the loop skip a bad candidate and try the next one instead of wedging:
    /// <list type="bullet">
    /// <item>If the chosen ticket's project has no matching skill file, it is Blocked
    /// (via <paramref name="skillExists"/>).</item>
    /// <item>If an "-ing" ticket has run <paramref name="maxAttempts"/> times without advancing, it
    /// is Blocked (the wedged-ticket circuit breaker) rather than resumed forever. Runs that DID
    /// advance it reset the count — see <see cref="Ticket.HeartbeatProgressMark"/>.</item>
    /// </list>
    /// Returns null when nothing is workable.
    /// </summary>
    public async Task<ClaimResult?> ClaimNextAsync(
        IReadOnlyCollection<string>? projectFilter,
        Func<string, string, bool> skillExists,
        int maxAttempts = 3,
        Func<string, string, string[]>? resourcesFor = null,
        TimeSpan? leaseTtl = null,
        IReadOnlyCollection<TicketPriority>? priorityFilter = null,
        IReadOnlyCollection<string>? skillFilter = null)
    {
        if (maxAttempts < 1) maxAttempts = 1;

        var q = _db.Tickets.Where(t => PhaseMachine.WorkablePhases.Contains(t.Phase));
        if (projectFilter is { Count: > 0 })
            q = q.Where(t => projectFilter.Contains(t.ProjectKey));

        // A narrower thing than the ordering just below, and deliberately so. Priority already
        // decides what goes FIRST; this decides what is workable AT ALL, so a ticket in an off-duty
        // band is passed over exactly the way one in a stood-down project is — not claimed, not
        // blocked, not touched, and eligible again the moment the band is switched back on.
        // Empty/absent means every band, so a config nobody has written works the whole board.
        if (priorityFilter is { Count: > 0 })
            q = q.Where(t => priorityFilter.Contains(t.Priority));

        // ProgressRank isn't SQL-translatable and the workable set is small — order in memory.
        //
        // Priority sorts strictly ABOVE progress, and that ordering is the whole feature: the bands
        // are meant to be read as separate queues that happen to share a heartbeat, so anything
        // workable at High is taken before anything at Normal however nearly finished the Normal one
        // is. Progress then decides within the band exactly as it always did, which keeps the older
        // rule ("finish what is most nearly finished") true of every band rather than repealing it.
        //
        // Track sorts BELOW progress and above age: at the same priority and the same rank, our own
        // delivery work goes first. It is a tiebreak rather than a band of its own because the bands
        // are what priority is for - a review the owner marked High is meant to beat a Normal
        // delivery ticket, and a rule that put every delivery ticket above every review would make
        // that impossible to express. The tie it breaks is a real one and not an accident: the Review
        // track's working phases are deliberately ranked alongside implementation's, so this line is
        // reached constantly rather than never (see PhaseMachine.ProgressRank).
        var candidates = (await q.ToListAsync())
            .OrderByDescending(t => t.Priority)
            .ThenByDescending(t => PhaseMachine.ProgressRank(t.Phase))
            .ThenBy(t => t.Track)
            .ThenBy(t => t.UpdatedAt)
            .ToList();

        // Tickets waiting on something else are passed over, not blocked: the wait is a fact about
        // the world, not a fault in the ticket, and it clears on its own when the blocker finishes.
        // Blocking them would need a human to unblock every one of them by hand afterwards.
        var waiting = _dependencies is null
            ? new HashSet<int>()
            : await _dependencies.BlockedTicketIdsAsync(candidates.Select(t => t.Id).ToList());

        foreach (var t in candidates)
        {
            if (waiting.Contains(t.Id)) continue;

            var target = PhaseMachine.ClaimTarget(t.Phase);
            var skill = PhaseMachine.SkillFor(target);

            // A worker host only runs some phases (docs/worker-hosts-plan.md). Above the skill check
            // on purpose: a claim that is not going to run this ticket must not block it either -
            // that is for a claim that would, and the board host's next one will.
            if (skillFilter is { Count: > 0 } && !skillFilter.Contains(skill)) continue;

            if (!skillExists(t.ProjectKey, skill))
            {
                await TransitionAsync(t.Id, TicketPhase.Blocked,
                    $"No '{skill}' skill configured for project '{t.ProjectKey}'.");
                continue;
            }

            var resumed = target == t.Phase;

            // Refinement waits for its independent analysis the way a ticket waits on a dependency:
            // passed over, not blocked, and eligible the moment the analysis lands. Asked here -
            // below the skill check, so a ticket that is about to be Blocked never costs an analysis
            // run - and never on a resume, which already has whatever this pass was handed.
            GateReadiness? gate = null;
            if (!resumed && _classroomGate is not null && t.Phase == TicketPhase.NeedsRefinement)
            {
                gate = await _classroomGate.CheckAsync(t);
                if (!gate.Ready) continue;
            }

            // Being back here only means the last run did not finish the phase — not that it
            // failed. A ticket bigger than one run ends every run still in its "-ing" phase, so
            // the breaker asks what actually changed instead. A run that ticked off work items
            // or rewrote the summary is not wedged, and counting it would block the ticket for
            // the crime of making progress. A run that changed nothing (a crash, an instant
            // exit, a worker going in circles) is what the cap is for.
            //
            // Read here, applied further down: nothing may be written to the ticket until we know
            // this run can actually go ahead — see the lease below.
            var mark = resumed ? await ProgressMarkAsync(t) : null;
            var advanced = resumed && t.HeartbeatProgressMark is not null && t.HeartbeatProgressMark != mark;

            // A ticket somebody is HOLDING is busy, not wedged. Skipped here, above the attempt cap,
            // because the cap asks "has anything moved since the last claim" and the honest answer
            // in the first minutes of a live run is "not yet" — a QA worker that is still bringing
            // the stack up has no QA item to tick off, and will never get one if the cap blocks the
            // ticket out from under it. That is not hypothetical: it is what happened to #39, which
            // was blocked for making no progress while the run working it was ten minutes from
            // finishing, and it takes a human unblock to undo.
            //
            // The lease acquisition further down would refuse this ticket anyway, so skipping now
            // costs nothing and keeps the cap's own reason for sitting above it intact: a ticket
            // about to be blocked still never briefly holds the QA stack.
            if (_leases is not null && await _leases.HolderOfAsync(ResourceLeaseService.TicketKey(t.Id)) is not null)
                continue;

            if (resumed && !advanced && t.HeartbeatAttempts >= maxAttempts)
            {
                // Nothing moved since the last claim, repeatedly. Give up so a wedged ticket can't
                // be re-run every cycle forever.
                await TransitionAsync(t.Id, TicketPhase.Blocked,
                    $"No progress after {maxAttempts} heartbeat attempts in {t.Phase}. Blocked for review; unblock to retry.");
                continue;
            }

            // Everything this run needs exclusive use of, taken all-or-nothing BEFORE the ticket is
            // touched. Taken after the attempt check so a ticket about to be blocked never briefly
            // holds the QA stack, and before any write so that losing the race for a busy resource
            // costs the ticket nothing — no consumed attempt, no phase move, nothing to undo. It is
            // simply not this ticket's turn.
            //
            // The ticket itself is always first on that list. With one worker the phase move was
            // lock enough; with several, the very next claim sees this ticket sitting in its "-ing"
            // phase, reads that as a resume, and hands the same branch and worktree to a second
            // worker. The ticket lease is what makes the pickup exclusive rather than merely ordered.
            List<ResourceLease>? leases = null;
            if (_leases is not null)
            {
                var needed = new List<string> { ResourceLeaseService.TicketKey(t.Id) };

                // A code-review-only ticket needs nothing but its own worktree: QA reads the diff and
                // never starts the app, so taking the project's stack would park it in a queue for a
                // resource it will not touch — and hold the stack away from a ticket that would.
                // Decided here rather than in the resourcesFor delegate because only the ticket knows.
                // The ticket's own lease is unaffected: that one is the pickup lock, not a resource.
                // Both testing skills honour it. On a Review ticket the flag means exactly what it
                // means on a Delivery one - read the diff, do not stand the app up - so the same
                // reasoning applies unchanged: a run that will not touch the stack must not queue
                // for it, nor hold it away from a run that would.
                var codeReviewOnly = skill is "qa-test" or "review-pr" && t.SkipQaTesting;
                if (resourcesFor is not null && !codeReviewOnly) needed.AddRange(resourcesFor(t.ProjectKey, skill));

                leases = await _leases.TryAcquireAllAsync(needed, t.Id, skill,
                    leaseTtl ?? TimeSpan.FromMinutes(150));
                if (leases is null) continue;   // someone else holds it — try the next ticket
            }

            if (resumed)
            {
                t.HeartbeatProgressMark = mark;

                if (advanced)
                {
                    t.HeartbeatAttempts = 1;
                    await AddCommentAsync(t.Id,
                        $"Resuming in-progress ticket. The last run advanced it, so this is attempt 1/{maxAttempts}.",
                        AuthorKind.System);
                }
                else
                {
                    t.HeartbeatAttempts++;
                    await AddCommentAsync(t.Id,
                        $"Resuming in-progress ticket (attempt {t.HeartbeatAttempts}/{maxAttempts}) — the last run left no progress behind.",
                        AuthorKind.System);
                }
            }
            else
            {
                // Fresh claim into a new "-ing" phase — this is attempt 1 for that phase, and the
                // baseline every later resume is measured against.
                t.HeartbeatAttempts = 1;
                t.HeartbeatProgressMark = await ProgressMarkAsync(t);
                if (target == TicketPhase.Refining) await HandOverAnalysisAsync(t, gate);
                await TransitionAsync(t.Id, target, "claimed by heartbeat");
            }

            return new ClaimResult((await GetAsync(t.Id))!, skill, resumed,
                leases?.Select(l => l.ResourceKey).ToArray() ?? Array.Empty<string>(),
                leases?.FirstOrDefault(l => l.ResourceKey == ResourceLeaseService.TicketKey(t.Id))?.Id ?? 0,
                leases?.Where(l => l.ResourceKey != ResourceLeaseService.TicketKey(t.Id))
                    .OrderBy(l => l.ResourceKey, StringComparer.Ordinal).Select(l => l.Slot).FirstOrDefault() ?? 0);
        }

        return null;
    }

    /// <summary>
    /// Give the refinement pass about to start the analysis the gate found for it: record which one,
    /// copy its findings out as rows to rule on (once per analysis - a ticket sent back to
    /// refinement on the same commit keeps the rulings it already has), and say on the ticket what
    /// the pass was handed. Saved by the transition that follows.
    /// </summary>
    private async Task HandOverAnalysisAsync(Ticket t, GateReadiness? gate)
    {
        t.GateAnalysisId = gate?.AnalysisId;

        if (gate?.AnalysisId is { } analysisId)
        {
            var findings = await _db.GateFindings.CountAsync(f => f.TicketId == t.Id && f.AnalysisId == analysisId);
            if (findings == 0)
            {
                var json = await _db.TicketAnalyses.AsNoTracking()
                    .Where(a => a.Id == analysisId).Select(a => a.BlocksJson).FirstOrDefaultAsync() ?? "";
                var extracted = ClassroomGate.ExtractFindings(t.Id, analysisId, json);
                _db.GateFindings.AddRange(extracted);
                findings = extracted.Count;
            }
            _db.Comments.Add(new Comment
            {
                TicketId = t.Id,
                Author = AuthorKind.System,
                Phase = TicketPhase.Refining,
                Body = findings == 0
                    ? $"Independent Classroom analysis #{analysisId} read this commit and raised no risks or coverage gaps."
                    : $"Independent Classroom analysis #{analysisId} raised {findings} finding(s) - risks and coverage gaps. "
                    + "Refinement rules on each with rule_on_finding before this ticket can go to NeedsReview.",
            });
        }
        else if (gate?.Note is { } note)
        {
            _db.Comments.Add(new Comment
            {
                TicketId = t.Id,
                Author = AuthorKind.System,
                Phase = TicketPhase.Refining,
                Body = $"Refining without an independent Classroom analysis: {note}.",
            });
        }
    }

    /// <summary>
    /// Hand back the attempt the last claim charged, for a run that never got to work at all
    /// because the provider refused it - the account's session limit. The cap is for runs that
    /// were given a chance and did nothing with it; a refused launch was given no chance. Charging
    /// it is how #71 went from claimed to Blocked in sixty seconds: three launches straight into
    /// the same limit, three attempts gone, and a ticket that needed nothing but the reset parked
    /// for a human to unblock. Never below zero, and nothing else about the ticket moves.
    /// Returns the attempt count after the refund, or null for an unknown ticket.
    /// </summary>
    public async Task<int?> RefundAttemptAsync(int ticketId, string? reason)
    {
        var t = await _db.Tickets.FindAsync(ticketId);
        if (t is null) return null;
        if (t.HeartbeatAttempts > 0)
        {
            t.HeartbeatAttempts--;
            await _db.SaveChangesAsync();
            await AddCommentAsync(ticketId,
                $"This run did not count toward the attempt limit: {(string.IsNullOrWhiteSpace(reason) ? "the provider refused it" : reason.Trim())}",
                AuthorKind.System);
        }
        return t.HeartbeatAttempts;
    }

    /// <summary>
    /// Fingerprint the ticket state that a working run is expected to move: work items added and
    /// ticked off, QA items added and resolved, the recorded branch, the implementation summary, the
    /// learning brief. The brief earns its place because it is the one durable mark a refine run
    /// leaves on the ticket itself — without it, a refinement cut short after writing it looks like
    /// a run that achieved nothing and counts against the ticket's attempt limit.
    /// Deliberately NOT comment count — a run can narrate without achieving anything, and the System
    /// comments the claim itself writes would make every ticket look busy.
    /// Hashed rather than stored raw so the mark stays small and comparable, and SHA256 rather than
    /// string.GetHashCode() because this is persisted and compared across process restarts.
    /// </summary>
    private async Task<string> ProgressMarkAsync(Ticket t)
    {
        var workTotal  = await _db.WorkItems.CountAsync(w => w.TicketId == t.Id);
        var workDone   = await _db.WorkItems.CountAsync(w => w.TicketId == t.Id && w.Done);
        var qaTotal    = await _db.QaItems.CountAsync(q => q.TicketId == t.Id);
        var qaResolved = await _db.QaItems.CountAsync(q => q.TicketId == t.Id && q.Status != QaStatus.Todo);

        var raw = $"w{workDone}/{workTotal}|q{qaResolved}/{qaTotal}|{t.Branch}|{t.ImplementationSummary}|{t.LearningBrief}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    // ---- Ticket lifecycle ----

    public async Task<Ticket> CreateAsync(string projectKey, string? title, string? description, string? issueKey, string? issueUrl, TicketPhase phase,
        bool requiresSpeculationReview = false, bool requiresImplementationReview = false, string? masterNotes = null,
        bool skipQaTesting = false, TicketPriority priority = TicketPriority.Normal,
        TicketTrack track = TicketTrack.Delivery, ReviewTarget? reviewTarget = null,
        bool skipClassroomAnalysis = false)
    {
        var maxOrder = await _db.Tickets.Where(t => t.ProjectKey == projectKey)
            .Select(t => (double?)t.BoardOrder).MaxAsync() ?? 0;

        var key = string.IsNullOrWhiteSpace(issueKey) ? null : issueKey.Trim();
        var ticket = new Ticket
        {
            ProjectKey = projectKey,
            // Linked tickets carry no local title/description (the tracker is the source of truth).
            Title = key != null ? null : title?.Trim(),
            Description = key != null ? null : description,
            IssueKey = key,
            IssueUrl = string.IsNullOrWhiteSpace(issueUrl) ? null : issueUrl.Trim(),
            Phase = phase,
            BoardOrder = maxOrder + 1,
            RequiresSpeculationReview = requiresSpeculationReview,
            RequiresImplementationReview = requiresImplementationReview,
            SkipQaTesting = skipQaTesting,
            SkipClassroomAnalysis = skipClassroomAnalysis,
            Priority = priority,
            Track = track,
            MasterNotes = string.IsNullOrWhiteSpace(masterNotes) ? null : masterNotes.Trim(),
        };

        // A Review ticket without a branch to review has nothing to work on, and the phase that
        // would claim it has no way to say so - it would fail deep in the worker, on a worktree
        // error that looks nothing like its cause. Refused here instead, at the one door that
        // creates one.
        if (track == TicketTrack.Review)
        {
            if (reviewTarget is null || string.IsNullOrWhiteSpace(reviewTarget.SourceBranch))
                throw new ArgumentException("A review ticket needs the branch it is reviewing.", nameof(reviewTarget));
            ticket.ReviewTarget = reviewTarget;
            reviewTarget.SourceBranch = reviewTarget.SourceBranch.Trim();
        }

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync();
        WakeFor(phase);
        return (await GetAsync(ticket.Id))!;
    }

    // masterNotes is last and optional on purpose: the MCP tools call this positionally and stop
    // before it, so no Claude-driven path can rewrite the human's standing instructions. skipQaTesting
    // sits after it for the same reason — it decides how much QA a ticket gets, which is the human's
    // call, not something a worker talks itself into partway through a run. priority is last on that
    // same list and for the sharpest version of the reason: it decides what gets worked BEFORE this
    // ticket, so a worker able to set it could promote itself past every other project.
    public async Task<Ticket?> UpdateAsync(int id, string? title, string? description, string? issueKey, string? issueUrl, string? branch, string? projectKey,
        bool? requiresSpeculationReview = null, bool? requiresImplementationReview = null, string? masterNotes = null,
        bool? skipQaTesting = null, TicketPriority? priority = null, bool? shortLoopReview = null,
        bool? skipClassroomAnalysis = null)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null) return null;

        if (requiresSpeculationReview is not null) t.RequiresSpeculationReview = requiresSpeculationReview.Value;
        if (requiresImplementationReview is not null) t.RequiresImplementationReview = requiresImplementationReview.Value;
        if (skipQaTesting is not null) t.SkipQaTesting = skipQaTesting.Value;
        if (skipClassroomAnalysis is not null) t.SkipClassroomAnalysis = skipClassroomAnalysis.Value;
        // The short loop is granted at the review gate, but it is spent much later - by the run that
        // takes the ticket back through implementation. Between those two moments the reviewer can
        // change their mind (the blocking questions get answered, the change turns out to be bigger
        // than it looked), and until this was editable the only way to take it back was to let the
        // work skip QA and refinement and then ask for them again.
        if (shortLoopReview is not null) t.ShortLoopReview = shortLoopReview.Value;
        if (priority is not null) t.Priority = priority.Value;
        if (masterNotes is not null) t.MasterNotes = string.IsNullOrWhiteSpace(masterNotes) ? null : masterNotes.Trim();

        if (title is not null) t.Title = title.Trim();
        if (description is not null) t.Description = description;
        if (issueKey is not null) t.IssueKey = string.IsNullOrWhiteSpace(issueKey) ? null : issueKey.Trim();
        if (issueUrl is not null) t.IssueUrl = string.IsNullOrWhiteSpace(issueUrl) ? null : issueUrl.Trim();
        if (branch is not null) t.Branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        if (projectKey is not null) t.ProjectKey = projectKey;

        // Invariant: a linked ticket never keeps a local title/description (the tracker owns them);
        // when it becomes/stays linked, drop any that slipped in. Unlinking keeps the applied title.
        if (!string.IsNullOrEmpty(t.IssueKey)) { t.Title = null; t.Description = null; }

        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>
    /// Record where this ticket's branch is checked out for the agent. Separate from
    /// <see cref="UpdateAsync"/> because the heartbeat sets it as a fact about the machine, not as
    /// an edit to the ticket's content — and because there is deliberately no MCP tool for it: the
    /// worker does not choose its own workspace.
    /// </summary>
    public async Task<Ticket?> SetWorktreePathAsync(int id, string? worktreePath)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null) return null;
        t.WorktreePath = string.IsNullOrWhiteSpace(worktreePath) ? null : worktreePath.Trim();
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>
    /// Record the commit a local merge landed this ticket at. Called by both mergers, the board's
    /// route and the steward, straight after the merge succeeds and before the move to Done, so a
    /// ticket that is Done by a merge always says which one.
    /// </summary>
    public async Task SetMergedAsync(int id, string? mergeSha)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null || string.IsNullOrWhiteSpace(mergeSha)) return;
        t.MergeSha = mergeSha.Trim();
        t.MergedAt = DateTime.UtcNow;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Rewrite the ticket's learning brief — the human's reading material, written by refinement
    /// once the branch is final (see <see cref="Ticket.LearningBrief"/>). Rewrite semantics, like
    /// the implementation summary: a ticket that bounces and comes back gets a fresh brief for the
    /// work that actually landed, never an accreted one.
    /// </summary>
    public async Task<Ticket?> SetLearningBriefAsync(int id, string? brief)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null) return null;
        var text = string.IsNullOrWhiteSpace(brief) ? null : brief.Trim();
        t.LearningBrief = text;
        // Stamped together so the UI never shows a brief with no date, or a date with no brief.
        t.LearningBriefAt = text is null ? null : DateTime.UtcNow;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>Rewrite the ticket's plain-language implementation summary (set at the end of each implement run).</summary>
    public async Task<Ticket?> SetImplementationSummaryAsync(int id, string? summary)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null) return null;
        t.ImplementationSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>
    /// Replace a ticket's QA shots with the given set (already copied to storage). Each tuple is the
    /// stored filename + caption; order is the list order. Returns the reloaded ticket, or null if missing.
    /// </summary>
    /// <param name="noShotsReason">
    /// Why this ticket has no screenshots, for work that genuinely has none to take. Recorded only
    /// when <paramref name="shots"/> is empty — real shots are the better answer and always win, so
    /// attaching any clears a previously recorded reason rather than sitting alongside it.
    /// </param>
    public Task<Ticket?> SetQaShotsAsync(int ticketId, IReadOnlyList<(string ImageFile, string Caption)> shots,
        string? noShotsReason = null) =>
        SetQaShotsAsync(ticketId, shots.Select(s => new QaShotSave(s.ImageFile, s.Caption)).ToList(), noShotsReason);

    /// <summary>One stored shot to record: filename, caption, and the optional focus rectangle.</summary>
    public record QaShotSave(string ImageFile, string Caption, ImageRect? Focus = null);

    public async Task<Ticket?> SetQaShotsAsync(int ticketId, IReadOnlyList<QaShotSave> shots,
        string? noShotsReason = null)
    {
        var t = await _db.Tickets.Include(x => x.QaShots).FirstOrDefaultAsync(x => x.Id == ticketId);
        if (t is null) return null;

        _db.QaShots.RemoveRange(t.QaShots);
        for (int i = 0; i < shots.Count; i++)
        {
            var shot = new QaShot { TicketId = ticketId, Order = i, ImageFile = shots[i].ImageFile, Caption = shots[i].Caption.Trim() };
            shot.SetFocus(shots[i].Focus);
            t.QaShots.Add(shot);
        }

        var reason = string.IsNullOrWhiteSpace(noShotsReason) ? null : noShotsReason.Trim();
        if (shots.Count > 0)
        {
            t.QaShotsWaiver = null;
            t.QaShotsWaiverAt = null;
        }
        else if (reason is not null)
        {
            t.QaShotsWaiver = reason;
            t.QaShotsWaiverAt = DateTime.UtcNow;
        }

        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(ticketId);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null) return false;
        _db.Tickets.Remove(t);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetOrderAsync(int id, double boardOrder)
    {
        var t = await _db.Tickets.FindAsync(id);
        if (t is null) return false;
        t.BoardOrder = boardOrder;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Filler a worker reaches for when it is not really saying anything.</summary>
    private static readonly HashSet<string> BlockFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "blocked", "stuck", "error", "failed", "failure", "broken", "n/a", "na", "none", "unknown",
        "see comment", "see comments", "see above", "see the comment", "as discussed", "cannot continue",
        "needs help", "need help", "needs input", "blocked by human", "unclear", "ambiguous",
    };

    /// <summary>
    /// A crude floor on the block reason: enough words to have said something, and not one of the
    /// stock non-answers. Deliberately mechanical - the point is not to grade the prose but to make
    /// the empty case impossible, because the reason is the whole interface between a stopped ticket
    /// and the person who can restart it. The refusal states this bar so a retry can clear it.
    /// </summary>
    private static bool IsUsableBlockReason(string? reason)
    {
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r)) return false;
        if (BlockFiller.Contains(r.TrimEnd('.', '!', '…'))) return false;
        return r.Length >= 20 && r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
    }

    /// <summary>
    /// Validated phase transition. Returns null if the ticket is missing; throws on an illegal move.
    /// <para>
    /// <paramref name="actor"/> defaults to <see cref="TransitionActor.System"/> because the
    /// dashboard's own moves — the claim lock, a skipped gate, the wedged-ticket breaker — are the
    /// ones that call this without saying who they are. The two doors a real actor comes through
    /// (the REST API and the MCP server) both name themselves.
    /// </para>
    /// <para>
    /// <paramref name="questions"/> is the structured form of a block (see <see cref="BlockPayload"/>)
    /// and is ignored for every other move. When it is given it REPLACES <paramref name="reason"/> as
    /// the account of the block: the reason becomes its preamble, and everything downstream keeps
    /// reading the one rendered string.
    /// </para>
    /// </summary>
    public async Task<Ticket?> TransitionAsync(int id, TicketPhase to, string? reason,
        TransitionActor actor = TransitionActor.System, BlockPayload? questions = null)
    {
        var t = await GetAsync(id);
        if (t is null) return null;

        if (!PhaseMachine.CanTransitionFor(t, to))
            throw new InvalidTransitionException($"Illegal transition {t.Phase} → {to}", t.Phase, PhaseMachine.NextPhasesFor(t));

        // What the ticket has to be CARRYING for this hand-off, checked only for the workers the
        // requirement is aimed at. A person moving a ticket by hand is making a deliberate choice
        // about their own ticket and is not the failure this guards against; the pipeline moving one
        // unattended, with nobody to notice the omission, is.
        if (actor == TransitionActor.Worker)
        {
            var unmet = HandoffRequirements.Unmet(t, t.Phase, to, await LastBackwardAtAsync(id));
            if (unmet is not null)
                throw new HandoffRequirementException(unmet, t.Phase, to, PhaseMachine.NextPhasesFor(t));

            // A block that says nothing stops the work and withholds the one thing needed to restart
            // it: nobody can answer a question that was never asked, and the board shows the bare
            // word "Blocked". Refused rather than defaulted, and refused the same way a missing
            // learning brief is - "do this, then call again with the same phase" - because the move
            // itself is right. Only workers are held to it: a person blocking their own ticket by
            // hand is already the person who would have read the reason.
            //
            // Questions and prose are alternatives, and the bar is the same either way - a question
            // list is held to it per question, so it cannot pass by being a well-shaped list of
            // nothing.
            if (to == TicketPhase.Blocked)
            {
                var problem = questions is not null
                    ? BlockQuestions.Validate(questions)
                    : string.IsNullOrWhiteSpace(reason)
                        ? "This block carries no reason, so nothing can be done about it. "
                        : IsUsableBlockReason(reason)
                            ? null
                            : $"\"{reason.Trim()}\" does not say what a person has to do about this ticket. ";

                if (problem is not null)
                    throw new HandoffRequirementException(
                        problem + HandoffRequirements.For(t.Phase, to, t)!.HowToSatisfy,
                        t.Phase, to, PhaseMachine.NextPhases(t.Phase, t.Track));
            }
        }

        var from = t.Phase;
        if (to == TicketPhase.Blocked)
        {
            t.PhaseBeforeBlock = from;
            // The rendered questions ARE the reason. Keeping one string as the account of the block
            // is what lets the board, the comment trail and the next run's prompt stay ignorant of
            // whether this block was asked in prose or in choices.
            t.BlockedQuestionsJson = questions is null ? null : BlockQuestions.Serialize(questions);
            t.BlockedReason = questions is not null
                ? BlockQuestions.Render(questions)
                : string.IsNullOrWhiteSpace(reason) ? "Blocked" : reason.Trim();
        }
        else if (from == TicketPhase.Blocked)
        {
            t.BlockedReason = null;
            t.BlockedQuestionsJson = null;
            t.PhaseBeforeBlock = null;

            // "Blocked for review; unblock to retry" is a promise the counter used to break. A ticket
            // blocked by the attempt cap comes back with its attempts still at the cap, so unblocking
            // it into the same "-ing" phase re-blocks it on the very next claim unless something else
            // happened to move the progress mark in the meantime. Whatever the block was about, a
            // person has now looked at it, and the run after a human intervention is attempt one.
            t.HeartbeatAttempts = 0;
        }

        // The short loop is one trip, not a setting: it is spent the moment the work is back in front
        // of the person who asked for it. Left standing, the next change request they make would
        // silently skip QA too - having been granted once, for a different change.
        if (to == TicketPhase.NeedsReview) t.ShortLoopReview = false;

        // Going round again puts the ball back in our court, so the submitted stamp has to go with
        // it - otherwise the gate would still read "awaiting dev" while a fresh pass was queued, and
        // the owner would be told to wait for a reply to feedback that is about to be superseded.
        // Set here rather than in the endpoint because both doors reach this method, and a stamp
        // that survived one route and not the other would be worse than either.
        if (to == TicketPhase.NeedsPeerReview && t.ReviewTarget is { SubmittedAt: not null })
        {
            t.ReviewTarget.SubmittedAt = null;
            t.ReviewTarget.UpdatedAt = DateTime.UtcNow;
        }

        t.Phase = to;
        t.UpdatedAt = DateTime.UtcNow;

        var note = to == TicketPhase.Blocked
            ? $"Blocked: {t.BlockedReason}"
            : $"Phase changed {from} → {to}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason.Trim()}");
        t.Comments.Add(new Comment { Author = AuthorKind.System, Body = note, Phase = to });

        // The same move, recorded as a countable row (see PhaseTransition). Written in the same
        // SaveChanges as the phase itself so the log can never disagree with the board.
        _db.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId    = t.Id,
            ProjectKey  = t.ProjectKey,
            Track       = t.Track,
            From        = from,
            To          = to,
            Actor       = actor,
            // A block logs what the ticket is CARRYING, not the raw argument: the ticket's own
            // BlockedReason is cleared the moment it unblocks, so this row is the only surviving copy
            // and LastBlockEpisodeAsync reads it back to the next run. For a structured block the raw
            // reason is just the preamble, and quoting only that would lose the questions.
            Reason      = to == TicketPhase.Blocked
                ? t.BlockedReason
                : string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Backward    = PhaseMachine.IsBackward(from, to),
            At          = DateTime.UtcNow,
        });

        // The two arrivals a project's steward exists to answer: finished work to sign off, and a
        // question somebody has to settle. Not its own moves - a run does not owe itself another.
        if (actor != TransitionActor.Steward && to is TicketPhase.NeedsReview or TicketPhase.Blocked)
            await StewardDue.MarkAsync(_db, t.ProjectKey,
                to == TicketPhase.NeedsReview ? $"#{t.Id} is ready for review" : $"#{t.Id} is blocked");

        await _db.SaveChangesAsync();
        WakeFor(to);

        // Leaving speculation means a plan now exists, built against the linked issue as it stands
        // right now — so this is the moment to record what it was built against. Every later phase
        // diffs against this. Captured here rather than asked of the worker: a baseline the worker
        // could forget to take is a baseline that silently isn't there.
        if (from == TicketPhase.Speculating && _snapshots is not null && !string.IsNullOrWhiteSpace(t.IssueKey))
        {
            var snapshot = await _snapshots.CaptureAsync(t);
            if (snapshot is not null)
            {
                t.IssueSnapshot = snapshot;
                t.IssueSnapshotAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }

        // A ticket reaching Done keeps its worktree. It used to be removed here, but holding it
        // means the finished work stays inspectable in place while anyone is still likely to look.
        // It is not kept forever: WorktreeReaper takes it back once the ticket has been Done for
        // worktreeRetentionDays, because a worktree with its packages installed is a gigabyte.

        // The short loop home. A reviewer asked for a small change and said to bring it straight back
        // to them, so the implementation hand-off routes past QA and refinement to the sign-off gate.
        // Checked before the gate skip below and not folded into it: this is the opposite decision
        // (where to send the work), and it wins - a reviewer who asked for the work back has already
        // answered the question the implementation-review gate would be asking.
        if (from == TicketPhase.Implementing && to == TicketPhase.NeedsImplementationReview && t.ShortLoopReview)
            return await TransitionAsync(id, TicketPhase.NeedsReview,
                "short loop - the reviewer asked for this change back without a full QA and refinement cycle");

        // A human review gate this ticket doesn't want is passed straight through, so the pipeline
        // never stalls waiting on a review nobody asked for. Only the automated hand-off skips —
        // a human moving a ticket onto a gate by hand keeps it there.
        if (PhaseMachine.TryGetGateSkip(from, to, out var skipTo))
        {
            if (!GateRequired(t, to))
                return await TransitionAsync(id, skipTo, $"{GateName(to)} review not required for this ticket");

            // Held by the plan rather than by the ticket's setting. Said in the trail as well as on
            // the drawer, because a ticket with speculation review switched off sitting at the gate
            // otherwise reads as the skip having broken.
            if (WorkItemScopes.HeldForExpansion(t))
            {
                var n = WorkItemScopes.OpenExpansionCount(t);
                t.Comments.Add(new Comment
                {
                    Author = AuthorKind.System,
                    Body = $"Held for approval: the plan includes {n} expansion item{(n == 1 ? "" : "s")} - work beyond what the ticket asked for. "
                         + "Speculation review is off for this ticket; it stopped here only because of those items. "
                         + "Approve to build them, or delete them first to keep the ticket to its ask.",
                    Phase = to,
                });
                await _db.SaveChangesAsync();
            }
        }

        return t;
    }

    /// <summary>
    /// When this ticket last moved backwards along the pipeline (QA failing, refinement finding a
    /// gap, a reviewer asking for changes), or null if it never has.
    /// <para>
    /// Read off the transition log rather than tracked on the ticket, because the log already
    /// records exactly this and a second copy could disagree with it. Used to age out the hand-off
    /// artefacts: anything written before the last bounce describes work that has since changed.
    /// </para>
    /// </summary>
    /// <summary>
    /// A ticket's most recent trip through Blocked, once it has come back out: what stopped it, and
    /// what the person who unblocked it said. Both sides come off the transition log, so nothing new
    /// is stored - the ticket's own BlockedReason is cleared when it unblocks, by design (it describes
    /// the present, and the present is no longer blocked).
    /// </summary>
    public record BlockEpisode(TicketPhase BlockedIn, string Reason, string? Answer, DateTime UnblockedAt, TicketPhase ResumedInto);

    /// <summary>
    /// The block this ticket has just come back from, or null - including when it has since been
    /// worked past that point.
    /// <para>
    /// "Since" is the whole subtlety. A worker asked a question, a human answered it by unblocking,
    /// and the very next run is the one that needs the answer - so the episode is reported only while
    /// no phase has been completed since. Claims are ignored (they move a queue phase to its "-ing"
    /// phase and nothing else), which is what lets the answer survive a resume of the same run
    /// without following the ticket around for the rest of its life.
    /// </para>
    /// </summary>
    public async Task<BlockEpisode?> LastBlockEpisodeAsync(int ticketId)
    {
        var log = await _db.PhaseTransitions
            .Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.At).ThenBy(p => p.Id)
            .Select(p => new { p.From, p.To, p.Reason, p.At })
            .ToListAsync();

        var blockAt = log.FindLastIndex(p => p.To == TicketPhase.Blocked);
        if (blockAt < 0) return null;

        var unblockAt = log.FindIndex(blockAt + 1, p => p.From == TicketPhase.Blocked);
        if (unblockAt < 0) return null;   // still blocked - nobody is working it, so nobody needs this

        // Anything other than a claim means a phase actually finished after the unblock: the answer
        // has been acted on, and repeating it to later phases would be noise they cannot use.
        for (var i = unblockAt + 1; i < log.Count; i++)
            if (!PhaseMachine.IsInProgress(log[i].To)) return null;

        var block = log[blockAt];
        var unblock = log[unblockAt];
        return new BlockEpisode(block.From, block.Reason ?? "(no reason recorded)",
            string.IsNullOrWhiteSpace(unblock.Reason) ? null : unblock.Reason.Trim(),
            unblock.At, unblock.To);
    }

    /// <summary>
    /// The episode as a block of prose for a worker's prompt, or null when there is nothing to say.
    /// Rendered here beside the query rather than in the orchestrator, for the same reason the issue
    /// diff is: the wording is part of how the pipeline behaves, and PowerShell is a poor place to
    /// keep it.
    /// </summary>
    public static string? RenderBlockEpisode(BlockEpisode? e)
    {
        if (e is null) return null;

        var answer = e.Answer is null
            ? "**They did not leave a note.** The block reason above is all you have. Do not simply "
            + "block it again with the same reason - either the question has been settled somewhere "
            + "you can now read (the ticket description, the master notes, the comments, the linked "
            + "issue), or the answer is that you should proceed on the most reasonable reading and say "
            + "in a comment which one you took. Block again only if you find the same wall, and say in "
            + "the reason that it is the second time and what you already tried."
            : $"**What they said:**\n\n> {e.Answer.Replace("\n", "\n> ")}\n\nThis is a direct answer to "
            + "the question that stopped the last run, from the ticket's owner. Treat it with the "
            + "authority of the master notes for the decision it settles, and re-read it at the point "
            + "in your phase where it applies. If it does not actually settle the question, say so in "
            + "a comment and block again with what is still missing - narrower than last time.";

        return $"This ticket was **blocked during {e.BlockedIn}** and a person has since unblocked it "
             + $"into {e.ResumedInto} ({e.UnblockedAt:yyyy-MM-dd HH:mm} UTC). You are the run that "
             + "picks it up afterwards.\n\n"
             + $"**Why it was blocked:**\n\n> {e.Reason.Replace("\n", "\n> ")}\n\n{answer}";
    }

    public async Task<DateTime?> LastBackwardAtAsync(int ticketId) =>
        await _db.PhaseTransitions
            .Where(p => p.TicketId == ticketId && p.Backward)
            .OrderByDescending(p => p.At)
            .Select(p => (DateTime?)p.At)
            .FirstOrDefaultAsync();

    /// <summary>
    /// The same answer for a whole board in one query — the cards need it to say how much of each
    /// ticket's QA evidence is current, and asking per card would be a round trip per row. Tickets
    /// that have never gone backwards are simply absent from the result.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> LastBackwardAtAsync(IReadOnlyCollection<int> ticketIds) =>
        ticketIds.Count == 0
            ? new Dictionary<int, DateTime>()
            : await _db.PhaseTransitions
                .Where(p => p.Backward && ticketIds.Contains(p.TicketId))
                .GroupBy(p => p.TicketId)
                .Select(g => new { TicketId = g.Key, At = g.Max(p => p.At) })
                .ToDictionaryAsync(x => x.TicketId, x => x.At);


    /// <summary>
    /// Send a ticket back to speculation because the linked issue changed materially under it, and
    /// force the human speculation-review gate on for the replan regardless of the ticket's setting.
    /// <para>
    /// One call, because the alternative is a worker doing three things correctly in a row — set the
    /// flag, move the phase, explain why — and leaving it half-done when it doesn't.
    /// </para>
    /// </summary>
    public async Task<Ticket?> RequestRespeculationAsync(int id, string reason)
    {
        var t = await GetAsync(id);
        if (t is null) return null;

        // The worker's door only, and only from a phase a worker is standing in. NeedsReview can
        // reach speculation too, but that edge is the reviewer's replan (RequestChangesAsync), and a
        // stray call must not be able to pull a ticket back from under the owner's sign-off.
        if (!PhaseMachine.IsInProgress(t.Phase))
            throw new InvalidTransitionException(
                $"Respeculation is requested by the run working a ticket; a ticket in {t.Phase} is not being worked",
                t.Phase, PhaseMachine.NextPhases(t.Phase, t.Track));

        t.RequiresSpeculationReview = true;
        t.Comments.Add(new Comment
        {
            Author = AuthorKind.Claude,
            Body = $"Re-speculation requested — the linked issue changed materially since the plan was made: {reason.Trim()}\n\n"
                 + "Speculation review has been switched on for this ticket so the new plan gets a human look.",
            Phase = t.Phase,
        });
        await _db.SaveChangesAsync();

        // The stale baseline goes with the stale plan; a fresh one is taken on the way back out.
        t.IssueSnapshot = null;
        t.IssueSnapshotAt = null;

        return await TransitionAsync(id, TicketPhase.NeedsSpeculation, $"The linked issue changed materially: {reason.Trim()}",
            TransitionActor.Worker);
    }

    /// <summary>
    /// A reviewer asking for changes: the note, the files, the list of what to change and the route
    /// home, applied as one operation and one trip back to implementation.
    /// <para>
    /// One call rather than four, for the reason <see cref="RequestRespeculationAsync"/> is one call:
    /// the alternative is a person doing four things correctly in a row from a drawer - write the
    /// note, upload the screenshot, add the work items, move the phase - and the pipeline behaving
    /// wrongly, silently, when they stop after the third. The move is what the worker reacts to, so a
    /// move that lands without the note attached is the one ordering that must be impossible.
    /// </para>
    /// <para>
    /// The change list becomes WORK ITEMS, not just prose in the note. The implement phase is built
    /// entirely around that checklist - it starts items, ticks them, resumes from them, and the
    /// wedged-ticket breaker measures progress by them - so a change request that arrives as prose is
    /// a change request none of that machinery can see. The note carries the why; the items carry
    /// the what.
    /// </para>
    /// <para>
    /// <paramref name="priority"/> rides along for the same reason the rest of it does. "Fix this and
    /// bring it back quickly" is one thought, and a reviewer who has to close the drawer and go and
    /// find the priority control to finish saying it is a reviewer who will often not bother — which
    /// leaves the urgent change sitting in the same band as everything else. Null leaves the band
    /// alone, so a change request that is not urgent says nothing about priority at all.
    /// </para>
    /// <para>
    /// <paramref name="replan"/> sends it to speculation instead, with the speculation review gate
    /// switched on. Implementation works from a checklist and has no scope gate, so when the request
    /// is new scope rather than a correction — or leaves a product decision open — it gets built as
    /// the worker reads it, and the open question comes back as a note for the reviewer after the
    /// work is done. Speculation is the phase that labels scope, blocks on an open decision, and
    /// stops for the owner's yes before anything is built. The note still arrives as an owner note,
    /// and the speculate skill's replan section tells that run to plan the note, not the whole
    /// story again.
    /// </para>
    /// </summary>
    public async Task<Ticket?> RequestChangesAsync(int id, string body, IReadOnlyList<string>? changeItems,
        bool shortLoop, IReadOnlyList<NoteAttachment>? attachments = null, TicketPriority? priority = null,
        bool replan = false, TransitionActor actor = TransitionActor.Human)
    {
        // A steward sends work back in the owner's place: the same note, the same move, under its
        // own name. Anything else through this door is a person.
        var author = actor == TransitionActor.Steward ? AuthorKind.Steward : AuthorKind.Human;

        var t = await GetAsync(id);
        if (t is null) return null;

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Say what you want changed — the note is what the next run works from.");

        // The two answer opposite questions: the short loop says the change is small enough to skip
        // the checks, a replan says it is big enough to need planning first.
        if (replan && shortLoop)
            throw new InvalidOperationException("A replan goes the full way round - it cannot also skip QA and refinement.");

        var target = replan ? TicketPhase.NeedsSpeculation : TicketPhase.NeedsImplementation;
        if (!PhaseMachine.CanTransition(t.Phase, target, t.Track))
            throw new InvalidTransitionException(
                $"A ticket in {t.Phase} cannot be sent back {(replan ? "for a replan" : "for changes")}", t.Phase, PhaseMachine.NextPhases(t.Phase, t.Track));

        await AddCommentAsync(id, body, author, attachments);

        foreach (var item in (changeItems ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)))
            await AddWorkItemAsync(id, item);

        // Reloaded because the writes above went through their own contexts of this same ticket, and
        // the flag has to be set on the instance the transition below is about to save.
        t = await GetAsync(id);
        if (t is null) return null;
        t.ShortLoopReview = shortLoop;
        // Set before the move, not after: the transition is what puts this ticket back in front of
        // the heartbeat, and a band applied a moment later is a band applied to a ticket that may
        // already have been claimed out of the old one.
        if (priority is not null) t.Priority = priority.Value;
        // A replan exists so the owner sees the plan before it is built, so the gate goes on whatever
        // the ticket was set to — the same reason RequestRespeculationAsync forces it.
        if (replan) t.RequiresSpeculationReview = true;
        await _db.SaveChangesAsync();

        // The move's own reason is the note's first line, so the transition log and the board read
        // usefully without the whole note being duplicated into them.
        var firstLine = body.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (firstLine.Length > 200) firstLine = firstLine[..197] + "...";
        return await TransitionAsync(id, target,
            $"{(replan ? "Replan requested" : "Changes requested")}: {firstLine}", actor);
    }

    /// <summary>Whether this ticket wants the human review gate it just landed on.</summary>
    private static bool GateRequired(Ticket t, TicketPhase gate) => gate switch
    {
        // A plan that goes beyond the ask gets a human look however the ticket is set: that yes is
        // the one decision no phase downstream is allowed to make on the owner's behalf.
        TicketPhase.NeedsApproval => t.RequiresSpeculationReview || WorkItemScopes.OpenExpansionCount(t) > 0,
        TicketPhase.NeedsImplementationReview => t.RequiresImplementationReview,
        _ => true,  // notably NeedsReview: the final sign-off is the owner's - a person's, or their steward's - and never skipped.
    };

    private static string GateName(TicketPhase gate) => gate switch
    {
        TicketPhase.NeedsApproval => "speculation",
        TicketPhase.NeedsImplementationReview => "implementation",
        _ => gate.ToString(),
    };

    // ---- Independent analysis findings ----

    /// <summary>
    /// Record refinement's ruling on one finding of the analysis its current pass was handed.
    /// Returns the finding, or an error in words the caller can act on. Only the current pass's
    /// findings are open to ruling: an older analysis's findings describe a commit that has moved.
    /// </summary>
    public async Task<(GateFinding? finding, string? error)> RuleOnFindingAsync(
        int ticketId, int findingId, GateRuling ruling, string? note)
    {
        var t = await _db.Tickets.FindAsync(ticketId);
        if (t is null) return (null, $"Ticket {ticketId} not found");
        if (t.GateAnalysisId is null)
            return (null, "This refinement pass was not handed an independent analysis, so there is nothing to rule on.");

        var f = await _db.GateFindings.FirstOrDefaultAsync(x => x.Id == findingId && x.TicketId == ticketId);
        if (f is null)
            return (null, $"Ticket {ticketId} has no finding {findingId}. Use an id from get_ticket's independentReview.");
        if (f.AnalysisId != t.GateAnalysisId)
            return (null, $"Finding {findingId} belongs to an earlier analysis, not the one this pass was handed. "
                        + "Rule on the findings listed in get_ticket's independentReview.");
        if (string.IsNullOrWhiteSpace(note))
            return (null, "Say why. A ruling with no reason is a vote, and the reviewer cannot weigh a vote.");

        f.Ruling = ruling;
        f.RulingNote = note.Trim();
        f.RuledAt = DateTime.UtcNow;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (f, null);
    }

    // ---- Work items ----

    public async Task<WorkItem?> AddWorkItemAsync(int ticketId, string text, WorkItemScope scope = WorkItemScope.Required)
    {
        var t = await _db.Tickets.Include(x => x.WorkItems).FirstOrDefaultAsync(x => x.Id == ticketId);
        if (t is null) return null;
        var order = t.WorkItems.Count == 0 ? 0 : t.WorkItems.Max(w => w.Order) + 1;
        var wi = new WorkItem { TicketId = ticketId, Text = text.Trim(), Order = order, Scope = scope };
        _db.WorkItems.Add(wi);
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return wi;
    }

    public async Task<WorkItem?> UpdateWorkItemAsync(int id, string? text, bool? done, int? order)
    {
        var wi = await _db.WorkItems.FindAsync(id);
        if (wi is null) return null;
        if (text is not null) wi.Text = text.Trim();
        if (done is not null)
        {
            wi.Done = done.Value;
            // Finishing an item ends its turn as the in-flight one. Clearing it here rather than
            // only in StartWorkItemAsync is what keeps "started but not done" from ever being true
            // of a finished item — otherwise re-opening one later would resurrect it as the item
            // being worked, with nobody working it.
            if (wi.Done) wi.StartedAt = null;
        }
        if (order is not null) wi.Order = order.Value;
        await _db.SaveChangesAsync();
        return wi;
    }

    /// <summary>
    /// Mark one work item as the one being worked right now, and every sibling as not.
    /// <para>
    /// Single-slot by construction: the implement phase does one item at a time, and letting two be
    /// "in progress" at once would only ever mean an earlier one was never closed out. Moving the
    /// marker is therefore the same operation as clearing the old one, and a run that walks back up
    /// the list to redo an item needs no separate call to release the one it left.
    /// </para>
    /// </summary>
    public async Task<WorkItem?> StartWorkItemAsync(int id)
    {
        var wi = await _db.WorkItems.FindAsync(id);
        if (wi is null) return null;

        var siblings = await _db.WorkItems
            .Where(w => w.TicketId == wi.TicketId && w.StartedAt != null && w.Id != id)
            .ToListAsync();
        foreach (var s in siblings) s.StartedAt = null;

        // Re-opening is implied: the phase says "I am working this now", and an item it is working
        // is by definition not finished. Saves the caller a second call to undo a premature tick.
        wi.Done = false;
        wi.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return wi;
    }

    public async Task<bool> DeleteWorkItemAsync(int id)
    {
        var wi = await _db.WorkItems.FindAsync(id);
        if (wi is null) return false;
        _db.WorkItems.Remove(wi);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- QA items ----

    public async Task<QaItem?> AddQaItemAsync(int ticketId, string text)
    {
        var t = await _db.Tickets.Include(x => x.QaItems).FirstOrDefaultAsync(x => x.Id == ticketId);
        if (t is null) return null;
        var order = t.QaItems.Count == 0 ? 0 : t.QaItems.Max(q => q.Order) + 1;
        var qi = new QaItem { TicketId = ticketId, Text = text.Trim(), Order = order };
        _db.QaItems.Add(qi);
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return qi;
    }

    /// <param name="passedByInspection">Whether a Passed outcome was argued from the code rather
    /// than driven through the app (see <see cref="QaItem.PassedByInspection"/>). Null leaves the
    /// flag alone, which is what a caller that only means to retitle or reorder an item wants; any
    /// outcome other than Passed clears it regardless, because the distinction only exists between
    /// two kinds of pass.</param>
    public async Task<QaItem?> UpdateQaItemAsync(int id, string? text, QaStatus? status, string? note, int? order,
        bool? passedByInspection = null)
    {
        var qi = await _db.QaItems.FindAsync(id);
        if (qi is null) return null;
        if (text is not null) qi.Text = text.Trim();
        if (status is not null)
        {
            qi.Status = status.Value;
            // An outcome ends this item's turn as the one being tested. Back to Todo does not: that
            // is the state an item sits in *while* it is under test, so clearing there would drop
            // the marker off the item the phase is standing on.
            if (qi.Status != QaStatus.Todo) qi.StartedAt = null;

            // Stamp the evidence with its own age. A verdict recorded now is a verdict about the
            // code as it stands now, and that is the whole claim this column carries; going back to
            // Todo withdraws the verdict, so it withdraws the timestamp with it rather than leaving
            // a date behind that nothing is true of any more.
            qi.VerifiedAt = qi.Status == QaStatus.Todo ? null : DateTime.UtcNow;
            if (qi.Status != QaStatus.Passed) qi.PassedByInspection = false;
        }
        if (passedByInspection is not null && qi.Status == QaStatus.Passed)
            qi.PassedByInspection = passedByInspection.Value;
        if (note is not null) qi.Note = string.IsNullOrWhiteSpace(note) ? null : note;
        if (order is not null) qi.Order = order.Value;
        await _db.SaveChangesAsync();
        return qi;
    }

    /// <summary>
    /// Mark one QA item as the one being tested right now, and every sibling as not. The QA mirror
    /// of <see cref="StartWorkItemAsync"/>, single-slot for the same reason.
    /// <para>
    /// Retesting is the common case here — QA comes back to an item after a fix, or after a flake —
    /// so starting one drops it back to <see cref="QaStatus.Todo"/>: an item under test does not
    /// have a current verdict, and leaving the old Passed in place would count a re-run that never
    /// finished as still passing. The note is deliberately kept. It explains the outcome being
    /// re-examined, and a run that dies mid-retest must not have erased why the item failed.
    /// </para>
    /// </summary>
    public async Task<QaItem?> StartQaItemAsync(int id)
    {
        var qi = await _db.QaItems.FindAsync(id);
        if (qi is null) return null;

        var siblings = await _db.QaItems
            .Where(q => q.TicketId == qi.TicketId && q.StartedAt != null && q.Id != id)
            .ToListAsync();
        foreach (var s in siblings) s.StartedAt = null;

        qi.Status = QaStatus.Todo;
        qi.VerifiedAt = null;
        qi.PassedByInspection = false;
        qi.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return qi;
    }

    public async Task<bool> DeleteQaItemAsync(int id)
    {
        var qi = await _db.QaItems.FindAsync(id);
        if (qi is null) return false;
        _db.QaItems.Remove(qi);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- Peer review: the branch under review, and the feedback staged against it ----
    //
    // Note what is absent from this whole section: anything that SENDS. Staging, editing, approving
    // and dismissing all live here and are all reachable from both doors; submitting is a separate
    // method reached only from the REST endpoint, and there is no MCP tool that calls it. That is
    // the same shape MasterNotes and Priority already use, and it is what makes "a worker can never
    // post to someone else's pull request" a fact about the tool list rather than a promise in prose.

    /// <summary>
    /// Which track a ticket runs on, or null when there is no such ticket. One column, no includes —
    /// the guards that use it only need to know whether a tool applies, and loading the whole
    /// aggregate to answer that would make every refusal cost more than the call it refuses.
    /// </summary>
    public Task<TicketTrack?> TrackOfAsync(int ticketId) =>
        _db.Tickets.Where(t => t.Id == ticketId).Select(t => (TicketTrack?)t.Track).FirstOrDefaultAsync();

    /// <summary>The branch this ticket is reviewing, or null when it is not a review ticket.</summary>
    public Task<ReviewTarget?> GetReviewTargetAsync(int ticketId) =>
        _db.ReviewTargets.FirstOrDefaultAsync(r => r.TicketId == ticketId);

    /// <summary>
    /// Record what a run read: the commit it reviewed, and what origin was carrying at the time.
    /// This is what every staged comment's line numbers are dated against.
    /// </summary>
    public async Task<ReviewTarget?> SetReviewShasAsync(int ticketId, string? reviewedAtSha, string? headSha)
    {
        var r = await GetReviewTargetAsync(ticketId);
        if (r is null) return null;
        if (!string.IsNullOrWhiteSpace(reviewedAtSha)) r.ReviewedAtSha = reviewedAtSha.Trim();
        if (!string.IsNullOrWhiteSpace(headSha)) r.HeadSha = headSha.Trim();
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return r;
    }

    /// <summary>
    /// What this pass concluded about the branch as a whole. Stamped with its own time so a verdict
    /// left over from before the author's last push stops counting at the hand-off.
    /// </summary>
    public async Task<ReviewTarget?> SetReviewVerdictAsync(int ticketId, ReviewVerdict verdict)
    {
        var r = await GetReviewTargetAsync(ticketId);
        if (r is null) return null;
        r.Verdict = verdict;
        r.VerdictAt = DateTime.UtcNow;
        r.UpdatedAt = DateTime.UtcNow;
        await Touch(ticketId);
        await _db.SaveChangesAsync();
        return r;
    }

    public async Task<ReviewComment?> AddReviewCommentAsync(int ticketId, string body,
        ReviewSeverity severity, AuthorKind author, string? filePath = null, int? line = null,
        DiffSide side = DiffSide.Head, int? qaItemId = null, int? qaShotOrder = null, string? anchorSha = null)
    {
        var t = await _db.Tickets.FindAsync(ticketId);
        if (t is null) return null;

        // Anchored to what the run says it read, falling back to what the target records. A comment
        // with no anchor at all reads as "never stale", which is the wrong way to be wrong - so the
        // fallback matters more than it looks.
        var target = await GetReviewTargetAsync(ticketId);
        var anchor = string.IsNullOrWhiteSpace(anchorSha) ? target?.ReviewedAtSha : anchorSha.Trim();

        var maxOrder = await _db.ReviewComments.Where(c => c.TicketId == ticketId)
            .Select(c => (int?)c.Order).MaxAsync() ?? -1;

        var rc = new ReviewComment
        {
            TicketId = ticketId,
            Body = body.Trim(),
            Severity = severity,
            Author = author,
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim().Replace('\\', '/'),
            Line = line,
            Side = side,
            QaItemId = qaItemId,
            QaShotOrder = qaShotOrder,
            AnchorSha = anchor,
            Order = maxOrder + 1,
        };
        _db.ReviewComments.Add(rc);
        await Touch(ticketId);
        await _db.SaveChangesAsync();
        return rc;
    }

    public async Task<ReviewComment?> UpdateReviewCommentAsync(int id, string? body,
        ReviewSeverity? severity, ReviewCommentStatus? status,
        int? qaShotOrder = null, bool clearQaShot = false)
    {
        var rc = await _db.ReviewComments.FindAsync(id);
        if (rc is null) return null;

        // A comment already sent is a matter of record - editing it here would leave the dashboard
        // disagreeing with what the author can actually read on the pull request.
        if (rc.Status == ReviewCommentStatus.Submitted) return rc;

        if (body is not null) rc.Body = body.Trim();
        if (severity is not null) rc.Severity = severity.Value;
        if (status is not null) rc.Status = status.Value;

        // The citation is settable AFTER the fact, and it has to be: shots are attached in one call
        // with the whole set, so the slot a finding belongs to does not exist yet at the moment the
        // finding is worth writing down. Without this, a run could only cite a shot by holding every
        // failure back until the end - which is the opposite of what the phase asks of it, and is
        // why ticket 57's six drive-test comments all carry a qaItemId and no shot.
        if (clearQaShot) rc.QaShotOrder = null;
        else if (qaShotOrder is not null) rc.QaShotOrder = qaShotOrder;
        rc.UpdatedAt = DateTime.UtcNow;
        await Touch(rc.TicketId);
        await _db.SaveChangesAsync();
        return rc;
    }

    public async Task<bool> DeleteReviewCommentAsync(int id)
    {
        var rc = await _db.ReviewComments.FindAsync(id);
        if (rc is null) return false;
        _db.ReviewComments.Remove(rc);
        await Touch(rc.TicketId);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Record that the owner has sent this ticket's feedback to the pull request.
    /// <para>
    /// Deliberately unreachable from the MCP server — see the note at the top of this section. It
    /// records a thing a human did; it does not do it.
    /// </para>
    /// <para>
    /// Dismissed comments are left alone: they were not sent, and marking them Submitted would erase
    /// the owner's decision not to send them.
    /// </para>
    /// </summary>
    public async Task<ReviewTarget?> MarkReviewSubmittedAsync(int ticketId)
    {
        var r = await GetReviewTargetAsync(ticketId);
        if (r is null) return null;

        var now = DateTime.UtcNow;
        var pending = await _db.ReviewComments
            .Where(c => c.TicketId == ticketId
                     && c.Status != ReviewCommentStatus.Dismissed
                     && c.Status != ReviewCommentStatus.Submitted)
            .ToListAsync();
        foreach (var c in pending)
        {
            c.Status = ReviewCommentStatus.Submitted;
            c.SubmittedAt = now;
            c.UpdatedAt = now;
        }

        r.SubmittedAt = now;
        r.UpdatedAt = now;
        await Touch(ticketId);
        await _db.SaveChangesAsync();
        return r;
    }

    private async Task Touch(int ticketId)
    {
        var t = await _db.Tickets.FindAsync(ticketId);
        if (t is not null) t.UpdatedAt = DateTime.UtcNow;
    }

    // ---- Comments ----

    public async Task<Comment?> AddCommentAsync(int ticketId, string body, AuthorKind author,
        IReadOnlyList<NoteAttachment>? attachments = null)
    {
        var t = await _db.Tickets.FindAsync(ticketId);
        if (t is null) return null;
        var c = new Comment { TicketId = ticketId, Body = body.Trim(), Author = author, Phase = t.Phase };
        if (attachments is { Count: > 0 })
            foreach (var a in attachments) { a.TicketId = ticketId; c.Attachments.Add(a); }
        _db.Comments.Add(c);
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return c;
    }

    // ---- Owner notes: the human → worker channel -------------------------------

    /// <summary>
    /// The ticket's unanswered owner notes, oldest first. Empty for a ticket whose owner has said
    /// nothing since the last run answered them.
    /// </summary>
    public async Task<IReadOnlyList<Comment>> UnansweredOwnerNotesAsync(int ticketId)
    {
        var t = await GetAsync(ticketId);
        return t is null ? Array.Empty<Comment>() : HandoffRequirements.UnansweredOwnerNotes(t);
    }

    /// <summary>
    /// Stamp notes that have just been put in front of a worker as delivered.
    /// <para>
    /// Called AFTER <see cref="RenderOwnerNotes"/>, never before, and the ordering is load-bearing:
    /// the rendered block tells a note apart by whether it has been delivered already ("a previous
    /// run saw this and did not answer it"), so stamping first makes every note look like a repeat
    /// on the very first run that sees it.
    /// </para>
    /// <para>
    /// Only the FIRST delivery is recorded — a note re-delivered because the last run died keeps its
    /// original stamp. The stamp answers the human's question, and there the useful reading is "how
    /// long has this been sitting unanswered"; overwriting it every claim would make a note that has
    /// been ignored for four runs look like it arrived a minute ago.
    /// </para>
    /// </summary>
    public async Task MarkOwnerNotesDeliveredAsync(IReadOnlyList<Comment> notes)
    {
        var now = DateTime.UtcNow;
        var any = false;
        foreach (var n in notes.Where(n => n.DeliveredAt is null)) { n.DeliveredAt = now; any = true; }
        if (any) await _db.SaveChangesAsync();
    }

    /// <summary>Filler that does not answer a note any more than "blocked" is a block reason.</summary>
    private static readonly HashSet<string> AckFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "done", "ok", "okay", "yes", "no", "fixed", "noted", "acknowledged", "ack", "sure", "got it",
        "understood", "will do", "agreed", "makes sense", "thanks", "n/a", "na", "none", "see comment",
        "see above", "as requested", "completed", "complete", "handled", "addressed",
    };

    /// <summary>
    /// Record what a worker did about one owner note, releasing the hand-off it was holding.
    /// <para>
    /// Held to a floor for the same reason a block reason is: the acknowledgement is the entire proof
    /// that the note was acted on rather than read past, and "done" is not that proof. The bar is
    /// deliberately mechanical — length and a stock-phrase list — because the alternative is grading
    /// prose, which this cannot do and should not pretend to.
    /// </para>
    /// </summary>
    public async Task<(Comment? Note, string? Problem)> AcknowledgeNoteAsync(int noteId, string response)
    {
        var note = await _db.Comments.FirstOrDefaultAsync(c => c.Id == noteId);
        if (note is null) return (null, null);

        if (!AuthorKinds.IsDirective(note.Author))
            return (note, $"Note {noteId} was written by {note.Author}, not by the ticket's owner. Only "
                        + "the owner's notes are addressed to you, and only they need answering — your "
                        + "own notes and the phase log do not.");

        var r = response?.Trim() ?? "";
        if (r.Length == 0 || AckFiller.Contains(r.TrimEnd('.', '!', '…')) ||
            r.Length < 20 || r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
            return (note, $"\"{r}\" does not say what you did about this note. Write one or two full "
                        + "sentences naming what you actually changed (or why you did not), specific "
                        + "enough that the owner can tell it apart from every other note on this "
                        + "ticket. This is what they read instead of re-reviewing the branch.");

        // Re-answering is allowed and overwrites: a run that acknowledged a note, then found more to
        // say about it before handing off, should not have to leave the first answer standing as the
        // record. It stays one answer per note, which is what makes it render under the note.
        note.AcknowledgedAt = DateTime.UtcNow;
        note.Acknowledgement = r;

        var t = await _db.Tickets.FindAsync(note.TicketId);
        if (t is not null) t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (note, null);
    }

    /// <summary>
    /// The outstanding owner notes as a block of prose for a worker's prompt, or null when there are
    /// none. Rendered here beside the issue diff and the block episode, for the reason given there:
    /// the wording is part of how the pipeline behaves, and PowerShell is a poor place to keep it.
    /// </summary>
    /// <summary>Size a reader can use. Integer KB rounds a 130-byte log dump to "0KB", which reads as empty.</summary>
    private static string SizeLabel(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#}MB",
        >= 1024 => $"{bytes / 1024.0:0.#}KB",
        _ => $"{bytes} bytes",
    };

    public static string? RenderOwnerNotes(IReadOnlyList<Comment> notes, TicketFileStore? files = null)
    {
        if (notes.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine(notes.Count == 1
            ? "The ticket's owner has left **one note** that no run has answered yet."
            : $"The ticket's owner has left **{notes.Count} notes** that no run has answered yet.");
        sb.AppendLine();
        sb.AppendLine("These are instructions from the person who owns this ticket, written after the "
                    + "work you are looking at. **They carry the authority of the master notes**: where "
                    + "one contradicts the ticket description, the work items, the linked issue or "
                    + "your skill's defaults, the note wins. A note is normally about the phase it was "
                    + "written in, but it is addressed to whoever picks the ticket up next - which is you.");
        sb.AppendLine();

        foreach (var n in notes)
        {
            var whereWhen = $"written {n.CreatedAt:yyyy-MM-dd HH:mm} UTC"
                          + (n.Phase is null ? "" : $", while the ticket was in {n.Phase}");
            sb.AppendLine($"### Note {n.Id} ({whereWhen})");
            sb.AppendLine();
            sb.AppendLine($"> {n.Body.Replace("\n", "\n> ")}");
            sb.AppendLine();

            if (n.Attachments.Count > 0)
            {
                // Named with their paths and an instruction to open them, not merely listed. A bare
                // list of attachments is the exact shape that got ignored on the tracker side until
                // get_ticket started saying what to do with it (see TicketTools.AttachmentsNote).
                sb.AppendLine(n.Attachments.Count == 1
                    ? "**They attached a file. Open it with the Read tool before you act on the note** - "
                    + "it is almost always the specific thing the note is talking about:"
                    : "**They attached these files. Open each one with the Read tool before you act on "
                    + "the note** - they are almost always the specific thing the note is talking about:");
                sb.AppendLine();
                foreach (var a in n.Attachments)
                {
                    var path = files is null ? a.FileName : files.PathFor(a).Replace('\\', '/');
                    sb.AppendLine($"- `{path}` ({a.FileName}, {SizeLabel(a.Bytes)})");
                }
                sb.AppendLine();
            }

            if (n.DeliveredAt is not null)
                sb.AppendLine($"*This note was already put in front of a run on {n.DeliveredAt:yyyy-MM-dd HH:mm} UTC "
                            + "and is still unanswered - either that run did not finish, or it worked past the "
                            + "note. Do not repeat whatever it did; deal with the note.*")
                  .AppendLine();
        }

        sb.AppendLine("**You cannot hand this ticket off until every one of these is answered.** For each "
                    + "note: do what it asks, then call `acknowledge_note` with the note's id and one or "
                    + "two sentences saying what you actually changed. The hand-off out of your phase is "
                    + "refused while any note is unanswered - that refusal is not a bug, it is this rule.");
        sb.AppendLine();
        sb.AppendLine("If a note asks for something you cannot do, or that contradicts something else on "
                    + "the ticket, still `acknowledge_note` saying so, then `set_phase` to Blocked with the "
                    + "question. Blocking is never gated on this.");
        return sb.ToString().TrimEnd();
    }
}
