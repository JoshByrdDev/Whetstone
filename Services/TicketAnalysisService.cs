using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>
/// Reads and writes Classroom analyses, and owns the one rule about which tickets can have one.
/// </summary>
public class TicketAnalysisService
{
    private readonly DashboardDbContext _db;

    public TicketAnalysisService(DashboardDbContext db) => _db = db;

    /// <summary>
    /// Whether this ticket can be studied yet, and if not, why not in words a person can act on.
    /// <para>
    /// Gated to post-implementation deliberately. Before code exists an analysis can only predict,
    /// and predicting costs a full run of reading to produce something the speculation phase is
    /// about to answer properly anyway. After implementation there is a diff to be concrete about,
    /// which is the whole difference between teaching and speculating.
    /// </para>
    /// <para>
    /// The test is "has implementation finished a pass", not "is the ticket past a given phase" —
    /// a ticket QA bounced back to <c>NeedsImplementation</c> has a branch full of real work and is
    /// worth studying, and a phase-ordinal check would call it ineligible.
    /// </para>
    /// </summary>
    public static (bool eligible, string? reason) Eligibility(Ticket t)
    {
        if (string.IsNullOrWhiteSpace(t.Branch))
            return (false, "it has no branch yet, so there is no work to read");

        // A blocked ticket is judged on where it stopped, not on Blocked itself — which sorts last in
        // the enum and would otherwise read as "past everything".
        var phase = t.Phase == TicketPhase.Blocked ? t.PhaseBeforeBlock ?? TicketPhase.Backlog : t.Phase;

        var landed = t.ImplementationSummary is { Length: > 0 }
                     || phase >= TicketPhase.NeedsImplementationReview;

        return landed ? (true, null) : (false, "implementation has not finished a first pass yet");
    }

    /// <summary>
    /// Whether an analysis still describes the ticket as it stands. Phase alone is not enough: a
    /// ticket can sit in one phase across four more commits, so the commit is the sharper half.
    /// A null <paramref name="headSha"/> on either side means we cannot tell, and cannot-tell is
    /// reported as fresh rather than as a warning nobody can act on.
    /// <para>
    /// Two refinements to both halves. The code is compared by tree where both trees are known,
    /// because refinement squashes the branch into a new commit holding identical code. And the
    /// three phases from NeedsRefinement to NeedsReview count as one: nothing a reader of the
    /// analysis relies on moves between them - QA is finished, and refinement writes the brief and
    /// the commit message, not the code. Without both, the independent analysis written just
    /// before refinement would greet its reader at review already marked stale.
    /// </para>
    /// </summary>
    public static bool IsStale(TicketAnalysis a, Ticket t, string? headSha, string? treeSha = null) =>
        StalenessBucket(a.PhaseAtGeneration) != StalenessBucket(t.Phase)
        || (a.TreeSha != null && treeSha != null
            ? a.TreeSha != treeSha
            : a.HeadSha != null && headSha != null && a.HeadSha != headSha);

    private static TicketPhase StalenessBucket(TicketPhase p) => p is
        TicketPhase.NeedsRefinement or TicketPhase.Refining ? TicketPhase.NeedsReview : p;

    /// <summary>Every analysis of one ticket, newest first.</summary>
    public Task<List<TicketAnalysis>> ForTicketAsync(int ticketId) =>
        _db.TicketAnalyses.AsNoTracking()
            .Where(a => a.TicketId == ticketId)
            .OrderByDescending(a => a.GeneratedAt)
            .ToListAsync();

    public Task<TicketAnalysis?> GetAsync(int id) =>
        _db.TicketAnalyses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);

    /// <summary>
    /// Tickets worth offering in the picker: everything eligible, plus how many analyses each
    /// already carries so the list can show which ones have been studied.
    /// </summary>
    public async Task<List<(Ticket Ticket, int Count, DateTime? Latest)>> StudiableAsync(string? project)
    {
        var q = _db.Tickets.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(project)) q = q.Where(t => t.ProjectKey == project);

        var tickets = (await q.ToListAsync())
            .Where(t => Eligibility(t).eligible)
            .ToList();

        var ids = tickets.Select(t => t.Id).ToList();
        var counts = await _db.TicketAnalyses.AsNoTracking()
            .Where(a => ids.Contains(a.TicketId))
            .GroupBy(a => a.TicketId)
            .Select(g => new { TicketId = g.Key, Count = g.Count(), Latest = g.Max(a => a.GeneratedAt) })
            .ToDictionaryAsync(x => x.TicketId);

        return tickets
            .Select(t => (t, counts.TryGetValue(t.Id, out var c) ? c.Count : 0,
                          counts.TryGetValue(t.Id, out var c2) ? c2.Latest : (DateTime?)null))
            .OrderByDescending(x => x.Item3 ?? DateTime.MinValue)
            .ThenByDescending(x => x.t.UpdatedAt)
            .ToList();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var row = await _db.TicketAnalyses.FirstOrDefaultAsync(a => a.Id == id);
        if (row == null) return false;
        _db.TicketAnalyses.Remove(row);
        await _db.SaveChangesAsync();
        return true;
    }
}
