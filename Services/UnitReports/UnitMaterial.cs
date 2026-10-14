using Whetstone.Models;

namespace Whetstone.Services.UnitReports;

/// <summary>
/// What a unit report is written from: one unit of a plan, its outcomes, the tickets that carried
/// them, each ticket's own Classroom analysis, and every QA screenshot those tickets took.
/// <para>
/// A pure function of rows already read, so it can be tested against rows. Everything that needs a
/// repo or the database - the branch tips, when each ticket went to Done - is asked for by the
/// caller and handed in, which is also what lets the gate and the run read the same answer about
/// what is missing.
/// </para>
/// <para>
/// The report is a synthesis over the ticket analyses, not a fresh read of the code: each analysis
/// is already a validated document about one branch, and reading eight of them is a fraction of
/// what re-reading eight branches would cost. So an analysis that no longer describes its ticket's
/// branch is not material, it is a reason to wait - see <see cref="Result.Missing"/>.
/// </para>
/// </summary>
public static class UnitMaterial
{
    /// <summary>A ticket branch's tip as it is now, for telling whether its analysis still describes it.</summary>
    public record BranchTip(string? HeadSha, string? TreeSha);

    /// <param name="Tickets">The project's tickets, with their QA shots loaded. Filtered here, on
    /// both the plan's key and the unit's number: a unit's number names nothing without its plan.</param>
    /// <param name="Analyses">Every analysis of those tickets, any order.</param>
    /// <param name="Tips">Each ticket's branch tip, where it could be resolved.</param>
    /// <param name="DoneAt">When each ticket last moved to Done: when it landed, for tickets merged
    /// before <see cref="Ticket.MergedAt"/> was recorded or merged somewhere this machine does not watch.</param>
    public record Input(
        string PlanKey,
        PlanUnit Unit,
        IReadOnlyList<PlanGroup> Groups,
        IReadOnlyList<Ticket> Tickets,
        IReadOnlyList<StewardOutcome> States,
        IReadOnlyList<StewardEntry> Entries,
        IReadOnlyList<TicketAnalysis> Analyses,
        IReadOnlyDictionary<int, BranchTip> Tips,
        IReadOnlyDictionary<int, DateTime> DoneAt);

    /// <summary>One screenshot in the report's pool. <see cref="N"/> is what the report's blocks cite.</summary>
    /// <param name="LandedAt">When the ticket that took it landed. Null while it has not.</param>
    public record PoolShot(int N, int TicketId, int TicketOrder, string Caption, string ImageFile,
        ImageRect? Focus, DateTime? LandedAt, DateTime TakenAt);

    /// <param name="Analysis">The analysis the report reads for this ticket, or null - and then
    /// <see cref="Result.Missing"/> says why.</param>
    /// <param name="Doc">That analysis's document, with every shot it cites already renumbered into
    /// the pool. A shot the ticket has since replaced has no number and is cited as none.</param>
    public record TicketPart(Ticket Ticket, TicketAnalysis? Analysis, AnalysisDocument.Doc? Doc,
        DateTime? LandedAt, IReadOnlyList<string> Outcomes);

    public enum MissingKind
    {
        /// <summary>The ticket is not Done: the unit is not finished being built.</summary>
        Open,
        /// <summary>The ticket was never studied in the Classroom.</summary>
        NoAnalysis,
        /// <summary>It was, but its branch has moved since.</summary>
        StaleAnalysis,
    }

    public record Missing(int TicketId, MissingKind Kind, string Why);

    /// <param name="Outcomes">The unit's outcomes as the lines a coverage map rules on: the id is
    /// the spec's (<c>A2</c>), the words are the spec's, the origin is the heading it stands under.</param>
    /// <param name="Calls">The steward's calls on the unit's tickets, oldest first.</param>
    public record Result(
        IReadOnlyList<DescriptionSegmenter.Segment> Outcomes,
        IReadOnlyList<TicketPart> Tickets,
        IReadOnlyList<PoolShot> Shots,
        IReadOnlyList<StewardEntry> Calls,
        IReadOnlyList<Missing> Missing);

    public static Result Build(Input input)
    {
        var unit = input.Unit;
        var mine = input.Tickets
            .Where(t => (t.PlanKey ?? "") == input.PlanKey && t.PlanUnit == unit.Number)
            .OrderBy(t => t.Id)
            .ToList();

        var outcomes = input.Groups
            .SelectMany(g => g.Outcomes.Select(o => new DescriptionSegmenter.Segment(
                o.Outcome, "outcome", o.Text, 0, string.IsNullOrWhiteSpace(g.Heading) ? null : g.Heading, null)))
            .ToList();

        DateTime? Landed(Ticket t) =>
            t.MergedAt ?? (t.Phase == TicketPhase.Done && input.DoneAt.TryGetValue(t.Id, out var at) ? at : null);

        // The pool, newest-landed first: when two tickets photographed one screen, the later one's
        // picture is the screen as it ships, and the order is the first thing that says so. A ticket
        // that has not landed goes last - its screen may never ship as photographed.
        var pool = new List<PoolShot>();
        var renumber = new Dictionary<(int TicketId, int Order), int>();
        foreach (var t in mine.OrderBy(t => Landed(t) is null).ThenByDescending(t => Landed(t)).ThenByDescending(t => t.Id))
            foreach (var s in t.QaShots.OrderBy(s => s.Order))
            {
                var n = pool.Count + 1;
                pool.Add(new PoolShot(n, t.Id, s.Order, s.Caption, s.ImageFile, s.Focus(), Landed(t), s.CreatedAt));
                renumber[(t.Id, s.Order)] = n;
            }

        var missing = new List<Missing>();
        var parts = new List<TicketPart>();
        foreach (var t in mine)
        {
            if (t.Phase != TicketPhase.Done)
                missing.Add(new Missing(t.Id, MissingKind.Open, $"#{t.Id} is in {t.Phase}, not Done"));

            var analysis = Pick(input.Analyses.Where(a => a.TicketId == t.Id), input.Tips.GetValueOrDefault(t.Id));
            AnalysisDocument.Doc? doc = null;
            if (analysis.Chosen is { } a)
                doc = AnalysisDocument.Deserialize(a.BlocksJson) is { } d ? Renumber(d, t.Id, renumber) : null;
            if (analysis.Chosen is null)
                missing.Add(analysis.AnyStale
                    ? new Missing(t.Id, MissingKind.StaleAnalysis, $"#{t.Id}'s analysis was written before its branch last moved")
                    : new Missing(t.Id, MissingKind.NoAnalysis, $"#{t.Id} has no Classroom analysis"));

            parts.Add(new TicketPart(t, analysis.Chosen, doc, Landed(t), StewardProgress.OutcomesOf(t)));
        }

        var ids = mine.Select(t => t.Id).ToHashSet();
        var calls = input.Entries
            .Where(e => e.Kind == StewardEntryKind.Call && e.TicketId is int id && ids.Contains(id))
            .OrderBy(e => e.At)
            .ToList();

        return new Result(outcomes, parts, pool, calls, missing);
    }

    /// <summary>
    /// The analysis a report reads for one ticket: the newest one that describes the branch as it is.
    /// <para>
    /// Not <see cref="TicketAnalysisService.IsStale"/>, on purpose. That also counts a change of
    /// phase, which is right on a ticket being worked and wrong here: every ticket in a finished
    /// unit has moved to Done since its last analysis, and not one byte of its code moved with it.
    /// Here only the code counts - the tree where both sides have one, else the commit.
    /// </para>
    /// <para>
    /// A Full analysis is preferred over a newer one through a narrower lens, because the report
    /// needs the ticket's flows and coverage and a lens may have weighted them out. With no tip
    /// (the branch is gone) the newest is taken as it stands: there is nothing left to be stale against.
    /// </para>
    /// </summary>
    public static (TicketAnalysis? Chosen, bool AnyStale) Pick(IEnumerable<TicketAnalysis> analyses, BranchTip? tip)
    {
        var all = analyses.OrderByDescending(a => a.GeneratedAt).ToList();
        if (all.Count == 0) return (null, false);

        bool Current(TicketAnalysis a) => tip is null
            || (a.TreeSha is not null && tip.TreeSha is not null ? a.TreeSha == tip.TreeSha
                : a.HeadSha is not null && tip.HeadSha is not null ? a.HeadSha == tip.HeadSha
                : true);

        var current = all.Where(Current).ToList();
        var chosen = current.FirstOrDefault(a => a.Lens == AnalysisLens.Full) ?? current.FirstOrDefault();
        return (chosen, chosen is null);
    }

    /// <summary>A ticket's document with its shot numbers moved into the pool's.</summary>
    private static AnalysisDocument.Doc Renumber(AnalysisDocument.Doc doc, int ticketId, Dictionary<(int, int), int> pool)
    {
        int? N(int? order) => order is int o && pool.TryGetValue((ticketId, o), out var n) ? n : null;

        return doc with
        {
            Sections = doc.Sections.Select(s => s with
            {
                Blocks = s.Blocks.Select(b => b with
                {
                    Nodes = b.Nodes?.Select(n => n with { Shot = N(n.Shot) }).ToList(),
                    Segments = b.Segments?.Select(g => g with { Shot = N(g.Shot) }).ToList(),
                }).ToList(),
            }).ToList(),
        };
    }
}
