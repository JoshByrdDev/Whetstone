using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>What the claim may do with a ticket waiting in NeedsRefinement.</summary>
/// <param name="Ready">False = not yet; pass it over this cycle, the way a ticket waiting on a
/// dependency is passed over.</param>
/// <param name="AnalysisId">The analysis refinement is to rule on, when there is one.</param>
/// <param name="Note">Why refinement is going ahead WITHOUT an analysis, when that is the reason
/// there is none - posted on the ticket so the gap is on the record rather than silent. Null when
/// the ticket was simply set to skip, and only ever for a ticket with nothing to read - an analysis
/// that was asked for and failed is <paramref name="Block"/>, not a note.</param>
/// <param name="Block">The analysis this trip asked for failed: the claim blocks the ticket with this
/// reason rather than refining without it. Not Ready.</param>
public record GateReadiness(bool Ready, int? AnalysisId = null, string? Note = null, string? Block = null)
{
    public static readonly GateReadiness NotYet = new(false);

    public static GateReadiness Blocked(string reason) => new(false, Block: reason);
}

/// <summary>
/// Whether a ticket is ready to be claimed into refinement, as far as its independent analysis
/// goes. An interface so the claim can be tested without a git repo or an agent behind it.
/// </summary>
public interface IClassroomGate
{
    Task<GateReadiness> CheckAsync(Ticket t);
}

/// <summary>
/// The independent review between QA and refinement.
/// <para>
/// Every phase before refinement reads the change against the PLAN - speculation writes it,
/// implementation builds it, QA checks the diff and the app against it - so a blind spot in the plan
/// passes through all four untouched. One ticket is the case that made this: speculation said the
/// saved group resolves against "the live chart of accounts" meaning "this office's accounts", and
/// three phases checked the work against that sentence while the account picker beside it filtered
/// by office AND chart type. The Classroom analysis found it, because it is asked to explain the
/// change from the system's side - how it worked before, the route a user takes - and that drew it
/// to the picker's own query. Nothing about 3913's bug class is written down anywhere for it; the
/// value is a reader asking different questions, which is why this is a gate rather than a rule.
/// </para>
/// <para>
/// Its <c>risk</c> callouts and the coverage map's gaps become <see cref="GateFinding"/> rows that
/// refinement must rule on (see <c>HandoffRequirements.GateFindingRequirement</c>). Refinement is
/// the triage: the analysis will raise things that are not defects, and a finding that sent the
/// ticket straight back would bounce good work on a reader's hunch.
/// </para>
/// </summary>
public class ClassroomGate : IClassroomGate
{
    private readonly DashboardDbContext _db;
    private readonly AnalysisQueue _queue;
    private readonly ProjectRegistry _registry;

    public ClassroomGate(DashboardDbContext db, AnalysisQueue queue, ProjectRegistry registry)
        => (_db, _queue, _registry) = (db, queue, registry);

    /// <summary>
    /// Whether this ticket's refinement wants an analysis at all. Delivery only: a review ticket's
    /// code is somebody else's and has no refinement phase to rule on anything.
    /// </summary>
    public static bool Applies(Ticket t) =>
        t.Phase == TicketPhase.NeedsRefinement && t.Track == TicketTrack.Delivery && !t.SkipClassroomAnalysis;

    public async Task<GateReadiness> CheckAsync(Ticket t)
    {
        if (!Applies(t)) return new GateReadiness(true);

        // The same directory and the same HEAD read the analysis run itself records, so "is there
        // one for this commit" compares like with like.
        var cfg = _registry.GetProjectConfig(t.ProjectKey);
        var dir = AnalysisRunner.ReadDir(t, cfg);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return new GateReadiness(true, Note: "there is no checkout of this branch on disk for it to read");
        // On a project of several repos, a fingerprint of each changed repo's commit and tree.
        var pin = AnalysisPin.Of(t, cfg);
        var head = pin.HeadSha;
        if (head is null)
            return new GateReadiness(true, Note: cfg.IsMultiRepo
                ? "no repo of this ticket's workspace has a commit of its own yet"
                : "the branch head could not be read");

        // Any lens counts. A person who already opened the Classroom on this exact code has paid for
        // the reading this gate wants; asking for a second one would be the same run twice. Matched
        // by tree as well as commit, so a ticket sent back to refinement after its squash does not
        // pay again for code that has not changed.
        var tree = pin.TreeSha;
        var existing = await _db.TicketAnalyses.AsNoTracking()
            .Where(a => a.TicketId == t.Id && (a.HeadSha == head || (tree != null && a.TreeSha == tree)))
            .OrderByDescending(a => a.GeneratedAt)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync();
        if (existing is not null) return new GateReadiness(true, existing);

        var (current, pending, recent) = _queue.Snapshot();
        if (current is { WalkthroughOf: null } c && c.TicketId == t.Id) return GateReadiness.NotYet;
        if (pending.Any(p => p.WalkthroughOf is null && p.TicketId == t.Id)) return GateReadiness.NotYet;

        // A failure since the ticket arrived here blocks it. Reading the analysis's risks is part of
        // what refinement is for, and a refinement that went ahead without one passed a ticket to
        // review that nothing independent had read - with a note on the record that is easy to miss
        // among the phase changes. Blocked rather than retried in place, because a retry loop would
        // park the ticket silently for as long as whatever broke the run stays broken; a block puts
        // it in front of a person. Unblocking moves it back into NeedsRefinement, which is a new
        // arrival, so the next claim asks for a fresh run - and one a person generated meanwhile from
        // the Classroom counts like any other. The queue's history is in memory, so a restart
        // forgets the failure, which is a retry, which is the right thing after a restart anyway.
        var arrived = await _db.PhaseTransitions.AsNoTracking()
            .Where(p => p.TicketId == t.Id && p.To == TicketPhase.NeedsRefinement)
            .OrderByDescending(p => p.At)
            .Select(p => (DateTime?)p.At)
            .FirstOrDefaultAsync() ?? DateTime.MinValue;
        var failed = recent.FirstOrDefault(o => o.TicketId == t.Id && o.AnalysisId is null && o.FinishedAt >= arrived);
        if (failed is not null)
            return GateReadiness.Blocked(
                $"The independent Classroom analysis failed: {failed.Error ?? "no reason given"}. "
                + "Refinement does not go ahead without it - its risks are part of what refinement rules on. "
                + "Unblock to run it again, generate one from the Classroom first, or set this ticket to skip the analysis.");

        // A full queue refuses; the next cycle simply asks again.
        _queue.Enqueue(t.Id, AnalysisLens.Full);
        return GateReadiness.NotYet;
    }

    /// <summary>
    /// The findings in one stored analysis: every <c>risk</c> callout, then every requirement the
    /// coverage map rules a gap or only partly met, in document order.
    /// <para>
    /// Deliberately NOT the <c>gotcha</c> and <c>insight</c> callouts, nor <c>unknown</c> rulings.
    /// Those are the analysis teaching the reader, and a gate that made refinement argue with every
    /// one of them would train it to wave the whole list through.
    /// </para>
    /// </summary>
    public static List<GateFinding> ExtractFindings(int ticketId, int analysisId, string blocksJson)
    {
        var doc = AnalysisDocument.Deserialize(blocksJson);
        var findings = new List<GateFinding>();
        if (doc is null) return findings;

        void Add(GateFindingKind kind, string? title, string? text)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text)) return;
            findings.Add(new GateFinding
            {
                TicketId = ticketId,
                AnalysisId = analysisId,
                Order = findings.Count,
                Kind = kind,
                Title = (title ?? "").Trim(),
                Text = (text ?? "").Trim(),
            });
        }

        var blocks = doc.Sections.SelectMany(s => s.Blocks).ToList();
        foreach (var b in blocks.Where(b => b.Kind == "callout" && string.Equals(b.Tone, "risk", StringComparison.OrdinalIgnoreCase)))
            Add(GateFindingKind.Risk, b.Title, b.Text);

        foreach (var seg in blocks.Where(b => b.Kind == "coverage").SelectMany(b => b.Segments ?? []))
        {
            var kind = seg.Verdict switch
            {
                "gap" => GateFindingKind.Gap,
                "partial" => GateFindingKind.Partial,
                _ => (GateFindingKind?)null,
            };
            if (kind is not null) Add(kind.Value, seg.Text, seg.Evidence);
        }

        return findings;
    }
}
