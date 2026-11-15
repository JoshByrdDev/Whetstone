using System.ComponentModel;
using ModelContextProtocol.Server;
using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Mcp;

/// <summary>
/// The Discovery track's tools, on the workers' own <c>/mcp</c> (docs/discovery-track-plan.md).
/// <para>
/// Each one is refused outside its phase - a route only while Charting, observations, visits and
/// chapters only while Scouting or Rechecking, proposals only while Sifting or Rechecking, a goal's outcome only
/// while Rechecking - which is how a phase sees only its own part of the
/// record without a second server. THERE IS NO TOOL THAT APPROVES, FILES OR ANSWERS: the verdict and
/// Complete are REST routes a person presses, so a run that thinks a proposal is good has said so and
/// stopped, which is as far as it should get.
/// </para>
/// </summary>
[McpServerToolType]
public class DiscoveryTools
{
    private static object Refusal(DiscoveryService.Refused r) => new { error = r.Message };

    [McpServerTool, Description(
        "Read a discovery's record: its route, the observations logged so far (with their ids and screenshot paths), the pages visited, " +
        "and its proposals and dropped list. While Sifting it also returns what the proposals have to be checked against: the project's open " +
        "tickets and earlier discoveries' proposals with the owner's verdicts (a rejection's reason, a snooze that is due). While Rechecking it returns " +
        "the goals to check (the approved proposals, with the tickets they became and what earlier rechecks found), which round this is and how many get a verdict, " +
        "and the earlier observations as one line each - enough to know what was seen, not a list to work through again.")]
    public static async Task<object> GetDiscovery(DiscoveryService svc, DashboardPaths paths,
        [Description("The local ticket id of the discovery.")] int ticketId)
    {
        var v = await svc.GetAsync(ticketId);
        if (v is null) return new { error = $"Ticket {ticketId} is not a discovery (or does not exist)." };
        var dir = paths.DiscoveryTicketDir(ticketId);
        object? known = null;
        var phase = (await svc.PhaseAsync(ticketId))!.Value;
        if (phase is TicketPhase.Sifting or TicketPhase.Rechecking) known = await svc.KnownAsync(await svc.ProjectOfAsync(ticketId), ticketId);
        var rechecking = phase == TicketPhase.Rechecking;
        var round = rechecking ? v.Round : 0;
        object? recheck = !rechecking ? null : new
        {
            round,
            roundsWithVerdict = v.RecheckRounds,
            handOff = round <= v.RecheckRounds
                ? "Proposals from this recheck go to the owner's verdict (NeedsVerdict); with none, hand off to NeedsReview."
                : "This recheck is past the rounds that get a verdict: hand off to NeedsReview whatever you propose; the owner sees it there.",
            goals = v.Goals.Select(id => v.Proposals.First(p => p.Id == id)).Select(p => new
            {
                goalId = p.Id, kind = p.Kind.ToString(), p.Title, p.Why, p.Body, p.WorkItems, p.Area,
                filedTicket = v.Filed.FirstOrDefault(f => f.Id == p.FiledTicketId) is { } f ? new { f.Id, f.Title, f.Phase } : null,
                earlier = v.GoalChecks.Where(g => g.ProposalId == p.Id).Select(g => new { g.Round, outcome = g.Outcome.ToString(), g.Note }),
            }),
        };
        return new
        {
            recheck,
            route = v.Route is null ? null : new
            {
                v.Route.Persona, v.Route.SignedInAs, v.Route.Jobs, v.Route.StartCases, v.Route.Widths, v.Route.Notes, v.Route.Areas,
            },
            // A recheck sees its own round's observations whole and the earlier ones as a line each:
            // enough to know what was seen and not re-propose it, not so much that it walks the
            // scout's list again instead of using the product.
            observations = v.Observations.Where(o => !rechecking || o.Round == round).Select(o => new
            {
                o.Id, kind = o.Kind.ToString(), o.Title, o.Saw, o.Tried, o.Job, o.Url, o.Width, o.SignedInAs, o.Area, o.Round,
                shot = o.ShotPath is null ? null : Path.Combine(dir, o.ShotPath),
            }),
            earlierObservations = !rechecking ? null : v.Observations.Where(o => o.Round != round)
                .Select(o => new { o.Id, kind = o.Kind.ToString(), o.Title, o.Round }),
            visits = v.Visits.Select(x => new { x.Url, x.RoutePattern, x.Width, x.SignedInAs, x.Seconds, x.Area, x.Case }),
            proposals = v.Proposals.Where(p => p.Status != ProposalStatus.Dropped).Select(p => new
            {
                p.Id, p.Rank, kind = p.Kind.ToString(), status = p.Status.ToString(), p.Title, p.Why, p.Body, p.WorkItems,
                p.Size, p.Confidence, p.Area, p.ObservationIds, p.Round, recheckKind = p.RecheckKind?.ToString(), p.GoalId,
                rejected = p.RejectReason?.ToString(), p.RejectNote,
            }),
            dropped = v.Proposals.Where(p => p.Status == ProposalStatus.Dropped).Select(p => new { p.Id, idea = p.Title, reason = p.DropReason }),
            known,
        };
    }

    [McpServerTool, Description(
        "CHARTING ONLY. Write the route a scout will walk: who the person is and the jobs they came to do. Goals, not clicks - " +
        "the scout reacts to what it sees, and a click-by-click script would turn it into a scripted QA walk. Calling it again replaces the route.")]
    public static async Task<object> SetDiscoveryRoute(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("Who the person is, in a sentence or two: where they are, what they want, what they already know about the product.")] string persona,
        [Description("Three to five jobs, each one line, written as what the person came to get done ('plan this weekend', 'come back a week later and see what changed') - never as a feature to look at.")] string[] jobs,
        [Description("Which of the product's own roles they use it as - signed out, an ordinary member, an admin - as the product names them.")] string? signedInAs = null,
        [Description("Showcase cases to start from, by the id the project's showcase index gives them. Empty when the project has none.")] string[]? startCases = null,
        [Description("Viewport widths, as WIDTHxHEIGHT: '1440x900', '390x844'. Most products want both a desk and a phone.")] string[]? widths = null,
        [Description("Anything else the scout should know before it starts, in prose. Never what the specs say - the scout reads nothing about the product.")] string? notes = null,
        [Description("The project's areas (discovery.areas) this route is expected to reach, by name.")] string[]? areas = null)
    {
        try
        {
            var r = await svc.SetRouteAsync(ticketId, persona, jobs, startCases, widths, signedInAs, notes, areas);
            return new { ticketId, jobs = r.Jobs.Count, r.WrittenAt };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "SCOUTING AND RECHECKING ONLY. Log one thing you noticed, as you notice it - uncapped. Every wall you hit, every judgement, every bold idea, every rough edge, every bug: " +
        "the cutting is the next phase's job, done by a different agent, so leaving something out here is the one way to lose it. " +
        "Take a screenshot that shows it first (browser_take_screenshot) and pass its absolute path.")]
    public static async Task<object> LogObservation(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("Wall (you tried and could not), Judgement (you could, but it should be different), Idea (it does not do this and would be better for it), RoughEdge, or Bug.")] string kind,
        [Description("What you noticed, in a line.")] string title,
        [Description("What you saw, and why it matters to the person on the route. Judge it at the scale the feature is for, not the seed data's.")] string saw,
        [Description("What you were trying to do when you saw it.")] string? tried = null,
        [Description("Which of the route's jobs you were on, 0-based.")] int? job = null,
        [Description("The page's URL.")] string? url = null,
        [Description("The viewport width you were at, e.g. '390x844'.")] string? width = null,
        [Description("Who you were signed in as.")] string? signedInAs = null,
        [Description("The project's area it belongs to, by name, when the project names its areas.")] string? area = null,
        [Description("Absolute path of the screenshot that shows it.")] string? shotPath = null,
        [Description("Optional: the rectangle in the screenshot's pixels that shows it, when it is a small part of the frame.")] QaFocusInput? focus = null,
        [Description("The commit the product was built from, as your prompt names it.")] string? commit = null)
    {
        try
        {
            var o = await svc.LogObservationAsync(ticketId, new DiscoveryService.ObservationInput(kind, title, saw, tried, job, url, width,
                signedInAs, area, shotPath, focus?.X, focus?.Y, focus?.W, focus?.H, commit));
            return new { o.Id, kind = o.Kind.ToString(), shot = o.ShotPath };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "SCOUTING AND RECHECKING ONLY. Mark a chapter in this run's recording: each job as you begin it, and each step within a job that a person " +
        "watching would want to jump to. Call it right after browser_video_chapter (which shows the card in the video); this is how the board knows where " +
        "the chapter falls, and chapters are where a proposal's clip is cut. Harmless when the run is not being recorded.")]
    public static async Task<object> MarkChapter(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("The chapter's title, as on its card: the job, or the step within it.")] string title,
        [Description("Which of the route's jobs it begins or belongs to, 0-based.")] int? job = null)
    {
        try
        {
            var c = await svc.MarkChapterAsync(ticketId, title, job);
            return new { c.Id, c.At };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "SCOUTING AND RECHECKING ONLY. Log a page you were on, for coverage: where, at which width, as whom, and roughly how long. Once per page per width, " +
        "when you leave it. This is how a later run knows what has been scouted and what has not.")]
    public static async Task<object> LogVisit(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("The page's URL.")] string url,
        [Description("The route pattern it belongs to, where you can tell (/events/[slug]).")] string? routePattern = null,
        [Description("The viewport width, e.g. '1440x900'.")] string? width = null,
        [Description("Who you were signed in as.")] string? signedInAs = null,
        [Description("Roughly how many seconds you spent there.")] int? seconds = null,
        [Description("The project's area it belongs to, by name.")] string? area = null,
        [Description("The showcase case it belongs to, by id, when it does.")] string? showcaseCase = null,
        [Description("The commit the product was built from.")] string? commit = null)
    {
        try
        {
            var v = await svc.LogVisitAsync(ticketId, url, routePattern, width, signedInAs, seconds, area, showcaseCase, commit);
            return new { v.Id };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "SIFTING AND RECHECKING ONLY. Keep one proposal for the owner's verdict, best first: the first call is rank 1. At most the project's cap per pass. " +
        "A recheck says what each proposal is: recheckKind FixUp (an approved goal it found not met - name it in goalId) or New. " +
        "Only after checking it against what is known: the specs and their 'not in this slice' sections, the decisions - including those made after a spec - " +
        "open tickets and earlier proposals (get_discovery). Nothing here files anything: the owner approves, edits or rejects it.")]
    public static async Task<object> Propose(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("Ticket (one piece of work), Bug (it is broken), Slice (big enough to be a slice of its own), or Decide (a question only the owner can settle - a rule the finding argues against).")] string kind,
        [Description("What would be true for the person, in a sentence.")] string title,
        [Description("What it would look like - where it goes, what it shows, what it does - enough that someone could build it. Becomes the filed ticket's description. For Decide: the question and the options.")] string body,
        [Description("The ids of the observations it stands on. Their screenshots are its evidence.")] int[] observationIds,
        [Description("Why it matters to the person on the route.")] string? why = null,
        [Description("For Ticket and Bug: the filed ticket's work items, one line each.")] string[]? workItems = null,
        [Description("ticket | a few tickets | slice.")] string? size = null,
        [Description("high | medium | low, and why.")] string? confidence = null,
        [Description("The project's area it belongs to, by name.")] string? area = null,
        [Description("RECHECKING: FixUp (an approved goal not met) or New (anything else you found). Not used while Sifting.")] string? recheckKind = null,
        [Description("RECHECKING, for a FixUp: the goal's id, from get_discovery's recheck.goals.")] int? goalId = null)
    {
        try
        {
            var p = await svc.ProposeAsync(ticketId, new DiscoveryService.ProposalInput(kind, title, why, body, workItems, size, confidence, area, observationIds,
                recheckKind, goalId));
            return new { p.Id, p.Rank };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "SIFTING AND RECHECKING ONLY. Put an idea on the dropped list, with one line of reason: generic (could be said of any site), already decided (name the decision), " +
        "already planned (name the slice), already a ticket (name it), wrong, or not worth it. Every observation that does not end up in a proposal ends up here.")]
    public static async Task<object> DropIdea(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("The idea, in a line.")] string idea,
        [Description("Why it is dropped, in a line.")] string reason,
        [Description("The observations it came from.")] int[]? observationIds = null)
    {
        try
        {
            var p = await svc.DropIdeaAsync(ticketId, idea, reason, observationIds);
            return new { p.Id };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "SIFTING AND RECHECKING ONLY. Take back an undecided proposal - one of this pass's that a better one has displaced, or (sifting) an earlier sift's that no longer holds. " +
        "It moves to the dropped list with your reason. A proposal the owner has decided is theirs and cannot be withdrawn.")]
    public static async Task<object> WithdrawProposal(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("The proposal's id.")] int proposalId,
        [Description("Why it no longer makes the cut, in a line.")] string reason)
    {
        try
        {
            var p = await svc.WithdrawAsync(ticketId, proposalId, reason);
            return new { p.Id, status = p.Status.ToString() };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }

    [McpServerTool, Description(
        "RECHECKING ONLY. Record whether one approved goal landed, judged by using the product on the code as it now is - never by reading the ticket it became. " +
        "Met, NotMet, or Unclear (you could not tell, and say why). Every goal get_discovery lists needs one before you hand off; calling it again this round replaces your answer. " +
        "A goal NotMet usually wants a FixUp proposal naming it.")]
    public static async Task<object> CheckGoal(DiscoveryService svc,
        [Description("The local ticket id of the discovery.")] int ticketId,
        [Description("The goal's id: the approved proposal, from get_discovery's recheck.goals.")] int goalId,
        [Description("Met, NotMet or Unclear.")] string outcome,
        [Description("What you did and what you saw, in a sentence or two - the owner reads it beside the goal.")] string note,
        [Description("Observations you logged this round that show it, when there are any.")] int[]? observationIds = null,
        [Description("The commit the product was built from, as your prompt names it.")] string? commit = null)
    {
        try
        {
            var g = await svc.CheckGoalAsync(ticketId, goalId, outcome, note, observationIds, commit);
            return new { goalId = g.ProposalId, round = g.Round, outcome = g.Outcome.ToString() };
        }
        catch (DiscoveryService.Refused e) { return Refusal(e); }
    }
}
