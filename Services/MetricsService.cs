using System.Text.Json;
using System.Text.RegularExpressions;
using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>
/// Reads and writes the pipeline's own numbers — what a run spent, and how often work goes
/// backwards. Kept apart from <see cref="TicketService"/> deliberately: nothing here may ever change
/// a ticket, so measurement can never become another thing that moves the board.
/// </summary>
public class MetricsService
{
    private readonly DashboardDbContext _db;

    public MetricsService(DashboardDbContext db) => _db = db;

    /// <summary>What a finished worker run reported about itself.</summary>
    /// <remarks>
    /// Everything after <c>Mutations</c> is nullable because a run that crashed or was killed at the
    /// timeout never produced a result event to read them from — and those are exactly the runs worth
    /// recording. A cycle with no token counts is a fact about the cycle, not a missing row.
    /// </remarks>
    public record CycleReport(
        int TicketId,
        string ProjectKey,
        TicketPhase Phase,
        string Skill,
        bool Resumed,
        string? Model,
        DateTime StartedAt,
        double DurationSeconds,
        int Mutations,
        CycleOutcome Outcome,
        double? ApiDurationSeconds = null,
        int? NumTurns = null,
        int? InputTokens = null,
        int? OutputTokens = null,
        int? CacheReadTokens = null,
        int? CacheCreationTokens = null,
        int? PeakContextTokens = null,
        int? ToolResultChars = null,
        IReadOnlyList<ToolResultSize>? LargestToolResults = null);

    /// <summary>One tool call and how much it returned. <c>Detail</c> is the call's caption from the feed.</summary>
    public record ToolResultSize(string Tool, string? Detail, int Chars, int Images = 0);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A cycle's largest tool results, read back. Empty when none were recorded or the column will not parse.</summary>
    public static IReadOnlyList<ToolResultSize> LargestToolResultsOf(CycleRun run)
    {
        if (string.IsNullOrEmpty(run.LargestToolResultsJson)) return [];
        try { return JsonSerializer.Deserialize<List<ToolResultSize>>(run.LargestToolResultsJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Record one heartbeat worker run. Returns null when the ticket no longer exists — a cycle
    /// against a deleted ticket is dropped rather than stored, since every metric it would feed is
    /// grouped by a ticket that is not there any more.
    /// <para>
    /// The ticket's phase is read here, not sent by the worker: the run's own hand-off is the last
    /// thing to happen before it ends, so the dashboard already knows where the ticket landed and
    /// asking the worker to report it would only add a way for the two to disagree.
    /// </para>
    /// </summary>
    public async Task<CycleRun?> RecordCycleAsync(CycleReport r)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == r.TicketId);
        if (ticket is null) return null;

        var run = new CycleRun
        {
            TicketId            = r.TicketId,
            ProjectKey          = string.IsNullOrWhiteSpace(r.ProjectKey) ? ticket.ProjectKey : r.ProjectKey,
            // Read off the ticket rather than reported by the worker, for the same reason PhaseAfter
            // below is: the dashboard already knows, and asking the worker only adds a way to disagree.
            Track               = ticket.Track,
            Phase               = r.Phase,
            Skill               = r.Skill,
            Resumed             = r.Resumed,
            Model               = r.Model,
            StartedAt           = r.StartedAt,
            EndedAt             = DateTime.UtcNow,
            DurationSeconds     = r.DurationSeconds,
            ApiDurationSeconds  = r.ApiDurationSeconds,
            NumTurns            = r.NumTurns,
            InputTokens         = r.InputTokens,
            OutputTokens        = r.OutputTokens,
            CacheReadTokens     = r.CacheReadTokens,
            CacheCreationTokens = r.CacheCreationTokens,
            PeakContextTokens   = r.PeakContextTokens,
            ToolResultChars     = r.ToolResultChars,
            LargestToolResultsJson = r.LargestToolResults is { Count: > 0 } top
                ? JsonSerializer.Serialize(top, Json) : null,
            Mutations           = r.Mutations,
            Outcome             = r.Outcome,
            PhaseAfter          = ticket.Phase,
        };

        _db.CycleRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    // ---- Reading the numbers back ------------------------------------------------

    /// <summary>How often work leaving a phase goes backwards instead of forwards.</summary>
    /// <param name="Backward">Moves to an earlier phase - QA failing, refinement finding gaps, a reviewer asking for changes.</param>
    public record PhaseBounce(string From, int Total, int Backward, double Rate);

    /// <summary>What a phase costs per run. Medians, not means: one two-hour timeout would drag a mean somewhere useless.</summary>
    public record PhaseCost(string Phase, int Runs, double MedianMinutes, double MedianTokens,
        int Completed, int Timeouts, int Errors);

    /// <summary>Token spend for one day, split by kind - cache reads are counted apart because they are not the same cost.</summary>
    public record DayTokens(string Day, long Input, long Output, long CacheRead, long CacheCreate, int Runs, double Minutes);

    public record TokenTotals(long Input, long Output, long CacheRead, long CacheCreate, int Runs, double Minutes);

    /// <param name="FirstPassQa">Tickets that reached refinement without QA ever sending them back.</param>
    /// <param name="QaAttempted">Tickets that have been through QA at all - the denominator.</param>
    public record Quality(int QaAttempted, int FirstPassQa, int TotalTransitions, int TotalBackward,
        int BackfilledTransitions, int Blocked, int ReachedDone);

    public record MetricsSummary(
        int Days, string? Project, bool HasCycleData, int CycleCount,
        IReadOnlyList<PhaseBounce> Bounces, IReadOnlyList<PhaseCost> PhaseCosts,
        IReadOnlyList<DayTokens> Daily, TokenTotals Last5Hours, TokenTotals Last24Hours,
        Quality Quality);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// Everything the metrics view shows, in one pass.
    /// <para>
    /// Aggregated in memory rather than in SQL: the row counts are tiny (one per worker run, one per
    /// phase change) and medians and rolling windows are considerably more legible in C# than in
    /// SQLite. If this ever stops being tiny, it wants a rethink rather than a bigger query.
    /// </para>
    /// </summary>
    /// <param name="track">
    /// Which pipeline to report on. Defaults to <see cref="TicketTrack.Delivery"/> rather than to
    /// everything, because almost every number below describes work we own: a peer-review pass costs
    /// a different shape of run than a QA pass, and its re-review loop is a backward move meaning
    /// "the author pushed a fix" rather than "we got it wrong". Averaged together they describe
    /// neither. Null asks for both, which is a question worth being able to ask and not the default
    /// answer to "how is the pipeline doing".
    /// </param>
    public async Task<MetricsSummary> SummaryAsync(string? project, int days = 30,
        TicketTrack? track = TicketTrack.Delivery)
    {
        if (days < 1) days = 1;
        var since = DateTime.UtcNow.AddDays(-days);

        var transQ = _db.PhaseTransitions.Where(p => p.At >= since);
        if (!string.IsNullOrEmpty(project)) transQ = transQ.Where(p => p.ProjectKey == project);
        if (track is not null) transQ = transQ.Where(p => p.Track == track);
        var transitions = await transQ.ToListAsync();

        var runsQ = _db.CycleRuns.Where(c => c.StartedAt >= since);
        if (!string.IsNullOrEmpty(project)) runsQ = runsQ.Where(c => c.ProjectKey == project);
        if (track is not null) runsQ = runsQ.Where(c => c.Track == track);
        var runs = await runsQ.ToListAsync();

        // Bounce rate, per phase work can actually be sent back FROM - the gates. Listing every
        // phase instead would bury the two numbers that matter under a dozen rows reading 0%, since
        // most phases have no backward move available and can only ever score zero. Worse, Blocked
        // would appear with exits and no entries: the backfill deliberately skipped the old
        // "Blocked: <reason>" comments because they name no from-phase, while the unblock moves it
        // recovered look like ordinary transitions.
        var bounces = transitions
            .Where(t => PhaseMachine.NextPhases(t.From, t.Track).Any(to => PhaseMachine.IsBackward(t.From, to)))
            .GroupBy(t => t.From)
            .Select(g => new PhaseBounce(g.Key.ToString(), g.Count(), g.Count(t => t.Backward),
                g.Count() == 0 ? 0 : Math.Round(100.0 * g.Count(t => t.Backward) / g.Count(), 1)))
            .OrderByDescending(b => b.Backward).ThenByDescending(b => b.Total)
            .ToList();

        var phaseCosts = runs
            .GroupBy(r => r.Phase)
            .Select(g => new PhaseCost(
                g.Key.ToString(), g.Count(),
                Math.Round(Median(g.Select(r => r.DurationSeconds / 60).ToList()), 1),
                Math.Round(Median(g.Select(r => (double)((r.InputTokens ?? 0) + (r.OutputTokens ?? 0))).ToList()), 0),
                g.Count(r => r.Outcome == CycleOutcome.Completed),
                g.Count(r => r.Outcome == CycleOutcome.Timeout),
                g.Count(r => r.Outcome == CycleOutcome.Error)))
            .OrderByDescending(p => p.Runs)
            .ToList();

        var daily = runs
            .GroupBy(r => r.StartedAt.Date)
            .Select(g => new DayTokens(
                g.Key.ToString("yyyy-MM-dd"),
                g.Sum(r => (long)(r.InputTokens ?? 0)), g.Sum(r => (long)(r.OutputTokens ?? 0)),
                g.Sum(r => (long)(r.CacheReadTokens ?? 0)), g.Sum(r => (long)(r.CacheCreationTokens ?? 0)),
                g.Count(), Math.Round(g.Sum(r => r.DurationSeconds) / 60, 1)))
            .OrderBy(d => d.Day)
            .ToList();

        // The rate-limit windows this account actually runs against. No dollar figure anywhere -
        // on a Max subscription the scarce thing is headroom, and headroom is tokens.
        TokenTotals Window(TimeSpan back)
        {
            var from = DateTime.UtcNow.Subtract(back);
            var w = runs.Where(r => r.StartedAt >= from).ToList();
            return new TokenTotals(
                w.Sum(r => (long)(r.InputTokens ?? 0)), w.Sum(r => (long)(r.OutputTokens ?? 0)),
                w.Sum(r => (long)(r.CacheReadTokens ?? 0)), w.Sum(r => (long)(r.CacheCreationTokens ?? 0)),
                w.Count, Math.Round(w.Sum(r => r.DurationSeconds) / 60, 1));
        }

        // First-pass QA: of the tickets QA has looked at, how many got through without being sent
        // back. Measured over the ticket's whole history rather than the window, because "did QA
        // ever fail this ticket" is not a question a date range can answer honestly.
        var ticketIds = transitions.Select(t => t.TicketId).Distinct().ToList();
        var allForThose = await _db.PhaseTransitions.Where(p => ticketIds.Contains(p.TicketId)).ToListAsync();
        var byTicket = allForThose.GroupBy(p => p.TicketId).ToList();
        var qaAttempted = byTicket.Count(g => g.Any(p => p.To == TicketPhase.QaTesting));
        var firstPass = byTicket.Count(g => g.Any(p => p.To == TicketPhase.QaTesting)
                                         && !g.Any(p => p.From == TicketPhase.QaTesting && p.Backward));

        var quality = new Quality(
            qaAttempted, firstPass,
            transitions.Count, transitions.Count(t => t.Backward),
            transitions.Count(t => t.Backfilled),
            transitions.Count(t => t.To == TicketPhase.Blocked),
            transitions.Count(t => t.To == TicketPhase.Done));

        return new MetricsSummary(
            days, project, runs.Count > 0, runs.Count,
            bounces, phaseCosts, daily, Window(TimeSpan.FromHours(5)), Window(TimeSpan.FromHours(24)),
            quality);
    }

    /// <summary>
    /// Every worker run started since <paramref name="since"/>, newest first. Unbounded on purpose:
    /// this feeds the history timeline, which is a window over time rather than a top-N list, and a
    /// row limit there would silently drop the oldest part of the range the caller asked for.
    /// </summary>
    public async Task<List<CycleRun>> CyclesSinceAsync(DateTime since) =>
        await _db.CycleRuns.Where(c => c.StartedAt >= since)
            .OrderByDescending(c => c.StartedAt)
            .ToListAsync();

    /// <summary>The most recent worker runs, newest first - the detail behind the aggregates.</summary>
    public async Task<List<CycleRun>> RecentCyclesAsync(string? project, int limit = 25)
    {
        var q = _db.CycleRuns.AsQueryable();
        if (!string.IsNullOrEmpty(project)) q = q.Where(c => c.ProjectKey == project);
        return await q.OrderByDescending(c => c.StartedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync();
    }

    /// <summary>
    /// The exact sentence <c>TicketService.TransitionAsync</c> has always written for a phase move.
    /// Machine-written and never edited, which is what makes reading it back defensible as a
    /// one-off; it is emphatically not how transitions are recorded from now on.
    /// </summary>
    private static readonly Regex PhaseChangeComment = new(
        @"^Phase changed (?<from>\w+) → (?<to>\w+)(?: — (?<reason>.*))?$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Reconstruct the transition history from the System comments written before this table
    /// existed, so the first look at a bounce rate is not an empty chart waiting a fortnight for
    /// data. Does nothing once any transition has been recorded — including on a second call, so it
    /// is safe to run on every startup.
    /// <para>
    /// Deliberately partial: only the "Phase changed X → Y" comments are read, because they name
    /// both ends of the move. Old Blocked comments say only that a ticket was blocked, and guessing
    /// what it was blocked <i>from</i> would put invented data in the one table that exists to be
    /// counted. Blocked edges are not rework anyway, so nothing the backfill feeds is distorted by
    /// leaving them out.
    /// </para>
    /// </summary>
    public async Task<int> BackfillTransitionsAsync()
    {
        if (await _db.PhaseTransitions.AnyAsync()) return 0;

        var comments = await _db.Comments
            .Where(c => c.Author == AuthorKind.System && c.Body.StartsWith("Phase changed "))
            .Join(_db.Tickets, c => c.TicketId, t => t.Id, (c, t) => new { c, t.ProjectKey })
            .OrderBy(x => x.c.CreatedAt).ThenBy(x => x.c.Id)
            .ToListAsync();

        var rows = new List<PhaseTransition>();
        foreach (var x in comments)
        {
            var m = PhaseChangeComment.Match(x.c.Body.Trim());
            if (!m.Success) continue;
            if (!Enum.TryParse<TicketPhase>(m.Groups["from"].Value, out var from)) continue;
            if (!Enum.TryParse<TicketPhase>(m.Groups["to"].Value, out var to)) continue;

            rows.Add(new PhaseTransition
            {
                TicketId   = x.c.TicketId,
                ProjectKey = x.ProjectKey,
                From       = from,
                To         = to,
                Actor      = TransitionActor.System,
                Reason     = m.Groups["reason"].Success ? m.Groups["reason"].Value : null,
                Backward   = PhaseMachine.IsBackward(from, to),
                Backfilled = true,
                At         = x.c.CreatedAt,
            });
        }

        if (rows.Count == 0) return 0;
        _db.PhaseTransitions.AddRange(rows);
        await _db.SaveChangesAsync();
        return rows.Count;
    }
}
