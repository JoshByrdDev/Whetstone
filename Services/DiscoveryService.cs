using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// The Discovery track's record and the owner's verdict on it (docs/discovery-track-plan.md).
/// <para>
/// Two kinds of writer, kept apart by which methods they can reach. A run writes through the MCP
/// tools - a route while Charting, observations and visits while Scouting, proposals and the dropped
/// list while Sifting - and each of those is refused outside its own phase. The owner writes the
/// verdict and Complete through REST routes with no tool behind them, so no run can approve, file
/// or answer anything.
/// </para>
/// </summary>
public class DiscoveryService
{
    private readonly DashboardDbContext _db;
    private readonly TicketService _tickets;
    private readonly ProjectRegistry _registry;
    private readonly DashboardPaths _paths;

    public DiscoveryService(DashboardDbContext db, TicketService tickets, ProjectRegistry registry, DashboardPaths paths)
        => (_db, _tickets, _registry, _paths) = (db, tickets, registry, paths);

    /// <summary>A refusal a caller hands straight back: the run or the owner reads it.</summary>
    public sealed class Refused(string message) : Exception(message);

    public sealed record View(
        DiscoveryRoute? Route,
        IReadOnlyList<DiscoveryObservation> Observations,
        IReadOnlyList<DiscoveryVisit> Visits,
        IReadOnlyList<DiscoveryProposal> Proposals,
        IReadOnlyList<FiledTicket> Filed);

    public sealed record FiledTicket(int Id, string? Title, string Phase, int? ProposalId);

    private const long MaxShotBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> ShotTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    // ---- reading ----------------------------------------------------------------------------

    public async Task<View?> GetAsync(int ticketId)
    {
        var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId);
        if (t is null || t.Track != TicketTrack.Discovery) return null;
        var route = await _db.DiscoveryRoutes.AsNoTracking().FirstOrDefaultAsync(r => r.TicketId == ticketId);
        var obs = await _db.DiscoveryObservations.AsNoTracking().Where(o => o.TicketId == ticketId).OrderBy(o => o.Id).ToListAsync();
        var visits = await _db.DiscoveryVisits.AsNoTracking().Where(v => v.TicketId == ticketId).OrderBy(v => v.Id).ToListAsync();
        var props = await _db.DiscoveryProposals.AsNoTracking().Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.Status == ProposalStatus.Dropped).ThenBy(p => p.Rank).ThenBy(p => p.Id).ToListAsync();
        var filed = await _db.Tickets.AsNoTracking().Where(x => x.DiscoveryId == ticketId).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.Phase }).ToListAsync();
        return new View(route, obs, visits, props,
            filed.Select(f => new FiledTicket(f.Id, f.Title, f.Phase.ToString(),
                props.FirstOrDefault(p => p.FiledTicketId == f.Id)?.Id)).ToList());
    }

    public Task<TicketPhase?> PhaseAsync(int ticketId) =>
        _db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => (TicketPhase?)t.Phase).FirstOrDefaultAsync();

    public async Task<string> ProjectOfAsync(int ticketId) =>
        await _db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.ProjectKey).FirstAsync();

    /// <summary>
    /// What Sift checks its proposals against, beyond the repo's own specs: the project's open
    /// tickets, and earlier discoveries' proposals with what the owner said about them.
    /// </summary>
    public async Task<object> KnownAsync(string projectKey, int exceptTicketId)
    {
        var open = await _db.Tickets.AsNoTracking()
            .Where(t => t.ProjectKey == projectKey && t.Phase != TicketPhase.Done && t.Track == TicketTrack.Delivery)
            .OrderByDescending(t => t.Id).Take(200)
            .Select(t => new { t.Id, t.Title, phase = t.Phase.ToString(), t.IssueKey }).ToListAsync();
        var earlier = await _db.DiscoveryProposals.AsNoTracking()
            .Where(p => p.TicketId != exceptTicketId && p.Ticket!.ProjectKey == projectKey && p.Status != ProposalStatus.Proposed)
            .OrderByDescending(p => p.Id).Take(200).ToListAsync();
        var now = DateTime.UtcNow;
        return new
        {
            openTickets = open,
            earlierProposals = earlier.Select(p => new
            {
                p.Id, discovery = p.TicketId, kind = p.Kind.ToString(), status = p.Status.ToString(), p.Title,
                rejected = p.RejectReason?.ToString(), p.RejectNote, p.DropReason, p.FiledTicketId,
                snoozedUntil = p.SnoozeUntil,
                snoozeDue = p.Status == ProposalStatus.Snoozed && (p.SnoozeUntil is null || p.SnoozeUntil <= now),
            }),
        };
    }

    // ---- what a run writes ------------------------------------------------------------------

    private async Task<Ticket> InPhaseAsync(int ticketId, TicketPhase phase, string tool)
    {
        var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId)
            ?? throw new Refused($"Ticket {ticketId} not found.");
        if (t.Track != TicketTrack.Discovery)
            throw new Refused($"Ticket {ticketId} is a {t.Track} ticket, not a discovery: {tool} is the Discovery track's.");
        if (t.Phase != phase)
            throw new Refused($"{tool} is for the {phase} phase, and ticket {ticketId} is in {t.Phase}. "
                + "Each phase of a discovery writes only its own part of the record.");
        return t;
    }

    public async Task<DiscoveryRoute> SetRouteAsync(int ticketId, string persona, IReadOnlyList<string> jobs,
        IReadOnlyList<string>? startCases, IReadOnlyList<string>? widths, string? signedInAs, string? notes, IReadOnlyList<string>? areas)
    {
        await InPhaseAsync(ticketId, TicketPhase.Charting, "set_discovery_route");
        if (string.IsNullOrWhiteSpace(persona)) throw new Refused("Say who the person is: the persona is what the scout plays.");
        var j = Clean(jobs);
        if (j.Count == 0) throw new Refused("A route needs at least one job: what the person came to get done.");
        if (j.Count > 8) throw new Refused("Three to five jobs. A route of more than eight is a plan, not a visit.");

        var route = await _db.DiscoveryRoutes.FirstOrDefaultAsync(r => r.TicketId == ticketId);
        if (route is null) { route = new DiscoveryRoute { TicketId = ticketId }; _db.DiscoveryRoutes.Add(route); }
        route.Persona = persona.Trim();
        route.Jobs = j;
        route.StartCases = Clean(startCases);
        route.Widths = Clean(widths);
        route.SignedInAs = Blank(signedInAs);
        route.Notes = Blank(notes);
        route.Areas = Clean(areas);
        route.WrittenAt = DateTime.UtcNow;
        route.RunId = await _tickets.LiveRunIdAsync(ticketId);
        await _db.SaveChangesAsync();
        return route;
    }

    public sealed record ObservationInput(string Kind, string Title, string Saw, string? Tried, int? Job,
        string? Url, string? Width, string? SignedInAs, string? Area, string? ShotPath,
        int? FocusX, int? FocusY, int? FocusW, int? FocusH, string? Commit);

    public async Task<DiscoveryObservation> LogObservationAsync(int ticketId, ObservationInput i)
    {
        await InPhaseAsync(ticketId, TicketPhase.Scouting, "log_observation");
        if (!Enum.TryParse<ObservationKind>(i.Kind?.Replace("-", "").Replace(" ", ""), true, out var kind))
            throw new Refused($"Unknown kind '{i.Kind}'. Use Wall, Judgement, Idea, RoughEdge or Bug.");
        if (string.IsNullOrWhiteSpace(i.Title)) throw new Refused("Give it a title: what you noticed, in a line.");
        if (string.IsNullOrWhiteSpace(i.Saw)) throw new Refused("Say what you saw and why it matters to the person on the route.");

        var o = new DiscoveryObservation
        {
            TicketId = ticketId, Kind = kind, Title = Cap(i.Title, 300), Saw = i.Saw.Trim(), Tried = Blank(i.Tried),
            Job = i.Job, Url = Blank(i.Url), Width = Blank(i.Width), SignedInAs = Blank(i.SignedInAs), Area = Blank(i.Area),
            Commit = Blank(i.Commit), RunId = await _tickets.LiveRunIdAsync(ticketId),
        };
        if (i.FocusW is > 0 && i.FocusH is > 0 && i.FocusX is >= 0 && i.FocusY is >= 0)
            (o.FocusX, o.FocusY, o.FocusW, o.FocusH) = (i.FocusX, i.FocusY, i.FocusW, i.FocusH);
        _db.DiscoveryObservations.Add(o);
        await _db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(i.ShotPath))
        {
            o.ShotPath = await SaveShotAsync(ticketId, $"o{o.Id}", i.ShotPath);
            await _db.SaveChangesAsync();
        }
        return o;
    }

    public async Task<DiscoveryVisit> LogVisitAsync(int ticketId, string url, string? routePattern, string? width,
        string? signedInAs, int? seconds, string? area, string? showcaseCase, string? commit)
    {
        await InPhaseAsync(ticketId, TicketPhase.Scouting, "log_visit");
        if (string.IsNullOrWhiteSpace(url)) throw new Refused("Which page? Pass its URL.");
        var v = new DiscoveryVisit
        {
            TicketId = ticketId, Url = Cap(url, 2048), RoutePattern = Blank(routePattern), Width = Blank(width),
            SignedInAs = Blank(signedInAs), Seconds = seconds is >= 0 ? seconds : null, Area = Blank(area),
            Case = Blank(showcaseCase), Commit = Blank(commit), RunId = await _tickets.LiveRunIdAsync(ticketId),
        };
        _db.DiscoveryVisits.Add(v);
        await _db.SaveChangesAsync();
        return v;
    }

    public sealed record ProposalInput(string Kind, string Title, string? Why, string Body,
        IReadOnlyList<string>? WorkItems, string? Size, string? Confidence, string? Area, IReadOnlyList<int>? ObservationIds);

    public async Task<DiscoveryProposal> ProposeAsync(int ticketId, ProposalInput i)
    {
        var t = await InPhaseAsync(ticketId, TicketPhase.Sifting, "propose");
        if (!Enum.TryParse<ProposalKind>(i.Kind, true, out var kind))
            throw new Refused($"Unknown kind '{i.Kind}'. Use Ticket, Bug, Slice or Decide.");
        if (string.IsNullOrWhiteSpace(i.Title)) throw new Refused("Give it a title: what would be true for the person, in a sentence.");
        if (string.IsNullOrWhiteSpace(i.Body)) throw new Refused("Say what it would look like - enough that someone could build it.");
        var ids = await CheckObservationsAsync(ticketId, i.ObservationIds);
        if (ids.Count == 0) throw new Refused("A proposal stands on what the scout saw: name at least one observation id.");

        var max = _registry.GetProjectConfig(t.ProjectKey).Discovery.MaxProposals;
        var since = await _tickets.LastBackwardAtAsync(ticketId) ?? DateTime.MinValue;
        var count = await _db.DiscoveryProposals.CountAsync(p => p.TicketId == ticketId
            && p.Status == ProposalStatus.Proposed && p.CreatedAt >= since);
        if (count >= max)
            throw new Refused($"This sift already has {count} proposals, the project's cap. Keep the best {max}: "
                + "withdraw_proposal one you would bet on less, or drop_idea this one with its reason.");

        var p = new DiscoveryProposal
        {
            TicketId = ticketId, Kind = kind, Rank = count + 1, Title = Cap(i.Title, 300), Why = Blank(i.Why), Body = i.Body.Trim(),
            WorkItems = Clean(i.WorkItems), Size = Blank(i.Size), Confidence = Blank(i.Confidence), Area = Blank(i.Area),
            ObservationIds = ids, RunId = await _tickets.LiveRunIdAsync(ticketId),
        };
        KeepDraft(p);
        _db.DiscoveryProposals.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    public async Task<DiscoveryProposal> DropIdeaAsync(int ticketId, string idea, string reason, IReadOnlyList<int>? observationIds)
    {
        await InPhaseAsync(ticketId, TicketPhase.Sifting, "drop_idea");
        if (string.IsNullOrWhiteSpace(idea)) throw new Refused("Name the idea you are dropping, in a line.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new Refused("Say why: generic, already decided (which decision), already planned (which slice), wrong, or not worth it.");
        var p = new DiscoveryProposal
        {
            TicketId = ticketId, Kind = ProposalKind.Ticket, Status = ProposalStatus.Dropped, Title = Cap(idea, 300),
            DropReason = reason.Trim(), ObservationIds = await CheckObservationsAsync(ticketId, observationIds),
            Rank = await _db.DiscoveryProposals.CountAsync(x => x.TicketId == ticketId && x.Status == ProposalStatus.Dropped) + 1,
            RunId = await _tickets.LiveRunIdAsync(ticketId),
        };
        KeepDraft(p);
        _db.DiscoveryProposals.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    /// <summary>A sift taking back one of its own undecided proposals - its own or an earlier pass's.</summary>
    public async Task<DiscoveryProposal> WithdrawAsync(int ticketId, int proposalId, string reason)
    {
        await InPhaseAsync(ticketId, TicketPhase.Sifting, "withdraw_proposal");
        var p = await _db.DiscoveryProposals.FirstOrDefaultAsync(x => x.Id == proposalId && x.TicketId == ticketId)
            ?? throw new Refused($"Discovery {ticketId} has no proposal {proposalId}.");
        if (p.Status != ProposalStatus.Proposed)
            throw new Refused($"Proposal {proposalId} is {p.Status}: the owner has already decided it, and only they can change that.");
        if (string.IsNullOrWhiteSpace(reason)) throw new Refused("Say in a line why it no longer makes the cut.");
        p.Status = ProposalStatus.Dropped;
        p.DropReason = reason.Trim();
        // A withdrawn proposal is on this pass's dropped list now, so it is stamped as this pass's.
        p.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return p;
    }

    private async Task<List<int>> CheckObservationsAsync(int ticketId, IReadOnlyList<int>? ids)
    {
        var wanted = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (wanted.Count == 0) return wanted;
        var have = await _db.DiscoveryObservations.Where(o => o.TicketId == ticketId && wanted.Contains(o.Id)).Select(o => o.Id).ToListAsync();
        var missing = wanted.Except(have).ToList();
        if (missing.Count > 0)
            throw new Refused($"Discovery {ticketId} has no observation {string.Join(", ", missing)}. Use the ids get_discovery lists.");
        return wanted;
    }

    private async Task<string> SaveShotAsync(int ticketId, string stem, string sourcePath)
    {
        var src = sourcePath.Trim().Trim('"');
        if (!File.Exists(src)) throw new Refused($"The observation is saved, but there is no screenshot at '{src}'. Pass the absolute path browser_take_screenshot wrote.");
        var ext = Path.GetExtension(src);
        if (!ShotTypes.Contains(ext)) throw new Refused($"The observation is saved, but '{ext}' is not a screenshot type (png, jpg, webp).");
        var len = new FileInfo(src).Length;
        if (len == 0 || len > MaxShotBytes) throw new Refused("The observation is saved, but its screenshot is empty or over 10MB.");
        var dir = _paths.DiscoveryTicketDir(ticketId);
        Directory.CreateDirectory(dir);
        var name = stem + ext.ToLowerInvariant();
        await using (var input = File.OpenRead(src))
        await using (var output = File.Create(Path.Combine(dir, name)))
            await input.CopyToAsync(output);
        return name;
    }

    /// <summary>A stored shot's path on disk, or null when the name is unsafe or missing.</summary>
    public string? ShotDiskPath(int ticketId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..") || name.Contains('/') || name.Contains('\\')) return null;
        var path = Path.Combine(_paths.DiscoveryTicketDir(ticketId), name);
        return File.Exists(path) ? path : null;
    }

    // ---- the owner's verdict ----------------------------------------------------------------

    public sealed record VerdictInput(
        string? Status = null, string? Title = null, string? Why = null, string? Body = null, IReadOnlyList<string>? WorkItems = null,
        string? Kind = null, string? RejectReason = null, string? RejectNote = null, DateTime? SnoozeUntil = null, string? Answer = null,
        bool? NewToMe = null);

    /// <summary>The owner edits a proposal or decides it. Only while the discovery waits for a verdict.</summary>
    public async Task<DiscoveryProposal> DecideAsync(int proposalId, VerdictInput v)
    {
        var p = await _db.DiscoveryProposals.Include(x => x.Ticket).FirstOrDefaultAsync(x => x.Id == proposalId)
            ?? throw new Refused($"There is no proposal {proposalId}.");
        if (p.Ticket!.Phase != TicketPhase.NeedsVerdict)
            throw new Refused($"Discovery {p.TicketId} is in {p.Ticket.Phase}: a verdict is given while it waits for one.");
        if (p.Status is ProposalStatus.Filed or ProposalStatus.Dropped)
            throw new Refused($"Proposal {proposalId} is {p.Status} and cannot be changed.");

        if (v.Title is not null && v.Title.Trim() != p.Title) { p.Title = Cap(v.Title, 300); p.Edited = true; }
        if (v.Why is not null && Blank(v.Why) != p.Why) { p.Why = Blank(v.Why); p.Edited = true; }
        if (v.Body is not null && v.Body.Trim() != p.Body) { p.Body = v.Body.Trim(); p.Edited = true; }
        if (v.WorkItems is not null) { var w = Clean(v.WorkItems); if (!w.SequenceEqual(p.WorkItems)) { p.WorkItems = w; p.Edited = true; } }
        if (v.Kind is not null)
        {
            if (!Enum.TryParse<ProposalKind>(v.Kind, true, out var k)) throw new Refused($"Unknown kind '{v.Kind}'.");
            if (k != p.Kind) { p.Kind = k; p.Edited = true; }
        }
        if (v.Answer is not null) p.Answer = Blank(v.Answer);
        var newToMeChanged = v.NewToMe is not null && v.NewToMe != p.NewToMe;
        if (newToMeChanged) p.NewToMe = v.NewToMe;

        VerdictEventKind? happened = null;
        if (v.Status is not null)
        {
            if (!Enum.TryParse<ProposalStatus>(v.Status, true, out var s) || s is ProposalStatus.Filed or ProposalStatus.Dropped)
                throw new Refused($"A verdict is Approved, Rejected, Snoozed or Proposed (undecided), not '{v.Status}'.");
            p.Status = s;
            p.DecidedAt = s == ProposalStatus.Proposed ? null : DateTime.UtcNow;
            p.RejectReason = null; p.RejectNote = null; p.SnoozeUntil = null;
            if (s == ProposalStatus.Rejected)
            {
                if (!Enum.TryParse<RejectReason>(v.RejectReason, true, out var r))
                    throw new Refused("Say why it is rejected: Generic, AlreadyDecided, NotNow or Wrong.");
                p.RejectReason = r;
                p.RejectNote = Blank(v.RejectNote);
            }
            if (s == ProposalStatus.Snoozed) p.SnoozeUntil = v.SnoozeUntil;
            happened = s switch
            {
                ProposalStatus.Approved => VerdictEventKind.Approved,
                ProposalStatus.Rejected => VerdictEventKind.Rejected,
                ProposalStatus.Snoozed => VerdictEventKind.Snoozed,
                _ => VerdictEventKind.Undone,
            };
        }
        else if (newToMeChanged) happened = VerdictEventKind.NewToMe;
        if (p.Status == ProposalStatus.Approved && p.Kind == ProposalKind.Decide && string.IsNullOrWhiteSpace(p.Answer))
            throw new Refused("A Decide proposal is approved with your answer: say what you decide.");
        if (string.IsNullOrWhiteSpace(p.Title) || string.IsNullOrWhiteSpace(p.Body))
            throw new Refused("A proposal needs a title and a body.");
        if (happened is { } kind) Record(p, kind);
        await _db.SaveChangesAsync();
        return p;
    }

    /// <summary>
    /// The owner promotes a line Sift dropped to a proposal, undecided, at the end of the list. It
    /// is recorded: restores are how "Sift cut too much" is counted.
    /// </summary>
    public async Task<DiscoveryProposal> RestoreAsync(int proposalId)
    {
        var p = await _db.DiscoveryProposals.Include(x => x.Ticket).FirstOrDefaultAsync(x => x.Id == proposalId)
            ?? throw new Refused($"There is no proposal {proposalId}.");
        if (p.Ticket!.Phase != TicketPhase.NeedsVerdict)
            throw new Refused($"Discovery {p.TicketId} is in {p.Ticket.Phase}: a dropped line is restored while it waits for a verdict.");
        if (p.Status != ProposalStatus.Dropped)
            throw new Refused($"Proposal {proposalId} is {p.Status}, not on the dropped list.");
        p.Status = ProposalStatus.Proposed;
        // A dropped line is a title and a reason; it needs a body to be decided, and the title is
        // the honest start of one. Whatever the owner writes over it counts as their edit.
        if (string.IsNullOrWhiteSpace(p.Body)) p.Body = p.Title;
        p.Rank = (await _db.DiscoveryProposals.Where(x => x.TicketId == p.TicketId && x.Status != ProposalStatus.Dropped)
            .MaxAsync(x => (int?)x.Rank) ?? 0) + 1;
        Record(p, VerdictEventKind.Restored);
        await _db.SaveChangesAsync();
        return p;
    }

    public sealed record CompleteResult(int Filed, int Answered, int Briefed, IReadOnlyList<int> TicketIds, string Phase);

    /// <summary>
    /// The owner's Complete: every approved proposal acted on at once, and the discovery moved to
    /// its sign-off. Refused while any proposal is undecided, so nothing is filed from half a verdict.
    /// </summary>
    public async Task<CompleteResult> CompleteAsync(int ticketId)
    {
        var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId)
            ?? throw new Refused($"Ticket {ticketId} not found.");
        if (t.Track != TicketTrack.Discovery) throw new Refused($"Ticket {ticketId} is not a discovery.");
        if (t.Phase != TicketPhase.NeedsVerdict) throw new Refused($"Discovery {ticketId} is in {t.Phase}, not waiting for a verdict.");

        var props = await _db.DiscoveryProposals.Where(p => p.TicketId == ticketId).OrderBy(p => p.Rank).ToListAsync();
        var undecided = props.Where(p => p.Status == ProposalStatus.Proposed).ToList();
        if (undecided.Count > 0)
            throw new Refused($"{undecided.Count} proposal{(undecided.Count == 1 ? " is" : "s are")} still undecided "
                + $"({string.Join(", ", undecided.Select(p => $"#{p.Id}"))}). Approve, reject or snooze each one first.");

        var cfg = _registry.GetProjectConfig(t.ProjectKey);
        var filedIds = new List<int>();
        int answered = 0, briefed = 0;
        foreach (var p in props.Where(p => p.Status == ProposalStatus.Approved))
        {
            switch (p.Kind)
            {
                case ProposalKind.Ticket:
                case ProposalKind.Bug:
                    var body = p.Body + (string.IsNullOrWhiteSpace(p.Why) ? "" : $"\n\n**Why:** {p.Why}")
                        + $"\n\n_Filed from discovery #{ticketId} ({(t.Title ?? "").Trim()}), proposal #{p.Id}._";
                    // A bug goes straight into the queue, as every bug filed is: it was seen, and the
                    // owner approved it. A ticket waits in Backlog for the owner to put it in order.
                    var nt = await _tickets.CreateAsync(t.ProjectKey, p.Title, body, null, null,
                        p.Kind == ProposalKind.Bug ? TicketPhase.NeedsSpeculation : TicketPhase.Backlog,
                        priority: t.Priority, skipClassroomAnalysis: cfg.SkipClassroomAnalysisByDefault);
                    if (p.Kind == ProposalKind.Bug) await _tickets.SetBugAsync(nt.Id, true);
                    foreach (var w in p.WorkItems) await _tickets.AddWorkItemAsync(nt.Id, w);
                    await _db.Tickets.Where(x => x.Id == nt.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DiscoveryId, ticketId));
                    p.FiledTicketId = nt.Id;
                    p.FiledNote = $"Filed as #{nt.Id}.";
                    filedIds.Add(nt.Id);
                    break;
                case ProposalKind.Slice:
                    // A briefing: the owner takes it to a session, or to the steward's ideas. Writing
                    // a slice idea from here is a later step of the plan.
                    p.FiledNote = "A briefing to take to a session or the steward's ideas.";
                    briefed++;
                    break;
                case ProposalKind.Decide:
                    p.FiledNote = $"Answered: {p.Answer}";
                    answered++;
                    break;
            }
            p.Status = ProposalStatus.Filed;
            Record(p, VerdictEventKind.Filed, t.ProjectKey);
        }
        await _db.SaveChangesAsync();

        var summary = $"Verdict complete: {filedIds.Count} ticket{(filedIds.Count == 1 ? "" : "s")} filed"
            + (filedIds.Count > 0 ? $" ({string.Join(", ", filedIds.Select(i => $"#{i}"))})" : "")
            + (answered > 0 ? $", {answered} question{(answered == 1 ? "" : "s")} answered" : "")
            + (briefed > 0 ? $", {briefed} slice briefing{(briefed == 1 ? "" : "s")}" : "")
            + $", {props.Count(p => p.Status == ProposalStatus.Rejected)} rejected, {props.Count(p => p.Status == ProposalStatus.Snoozed)} snoozed.";
        await _tickets.AddCommentAsync(ticketId, summary, AuthorKind.System);
        var moved = await _tickets.TransitionAsync(ticketId, TicketPhase.NeedsReview, summary, TransitionActor.Human);
        return new CompleteResult(filedIds.Count, answered, briefed, filedIds, (moved?.Phase ?? TicketPhase.NeedsReview).ToString());
    }

    // ---- the verdict log ----------------------------------------------------------------------

    /// <summary>Sift's words as written, kept apart from whatever the owner edits them into.</summary>
    private static void KeepDraft(DiscoveryProposal p)
    {
        p.DraftKind = p.Kind;
        p.DraftTitle = p.Title;
        p.DraftWhy = p.Why;
        p.DraftBody = p.Body;
        p.DraftWorkItems = p.WorkItems.ToList();
    }

    /// <summary>Appends one event for what the owner just did, measured against Sift's draft. Never updates one.</summary>
    private void Record(DiscoveryProposal p, VerdictEventKind kind, string? projectKey = null)
    {
        var draft = EditText(p.DraftTitle, p.DraftWhy, p.DraftBody, p.DraftWorkItems);
        var now = EditText(p.Title, p.Why, p.Body, p.WorkItems);
        _db.DiscoveryVerdictEvents.Add(new DiscoveryVerdictEvent
        {
            TicketId = p.TicketId, ProposalId = p.Id, ProjectKey = projectKey ?? p.Ticket?.ProjectKey ?? "",
            Kind = kind,
            RejectReason = kind == VerdictEventKind.Rejected ? p.RejectReason : null,
            Note = kind == VerdictEventKind.Rejected ? p.RejectNote : null,
            NewToMe = p.NewToMe,
            Edited = draft != now || p.Kind != p.DraftKind,
            KindChanged = p.Kind != p.DraftKind,
            EditDistance = EditDistance(draft, now),
            DraftLength = draft.Length,
            FiledTicketId = kind == VerdictEventKind.Filed ? p.FiledTicketId : null,
            At = DateTime.UtcNow,
        });
    }

    private static string EditText(string title, string? why, string body, IEnumerable<string> items) =>
        string.Join("\n", new[] { title, why ?? "", body }.Concat(items));

    /// <summary>
    /// Levenshtein distance in characters, on texts capped at 8000 each so a pathological body costs
    /// a bounded amount; past the cap the length difference stands in for the rest.
    /// </summary>
    public static int EditDistance(string a, string b)
    {
        const int cap = 8000;
        var extra = Math.Abs(Math.Max(a.Length - cap, 0) - Math.Max(b.Length - cap, 0));
        if (a.Length > cap) a = a[..cap];
        if (b.Length > cap) b = b[..cap];
        if (a == b) return extra;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length] + extra;
    }

    // ---- small helpers ----------------------------------------------------------------------

    private static List<string> Clean(IEnumerable<string>? xs) =>
        (xs ?? Array.Empty<string>()).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).ToList();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Cap(string s, int max) { s = s.Trim(); return s.Length <= max ? s : s[..max]; }
}
