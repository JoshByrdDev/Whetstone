using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>The measures, the guardrails and the stages (docs/bugs-and-experiments-plan.md, 2.5 to 2.7).</summary>
public partial class ExperimentService
{
    public static class Stages
    {
        public const string Enrolling = "enrolling";
        public const string Landing = "landing";
        public const string Waiting = "waiting";
        public const string Ready = "ready";
        public const string Stopped = "stopped";
        public const string Concluded = "concluded";
    }

    public static class Guardrails
    {
        public const string QaFailure = "qa-failure";
        public const string Rework = "rework";
        public const string EscapedBug = "escaped-bug";
        public static readonly string[] All = { QaFailure, Rework, EscapedBug };
    }

    /// <summary>One enrolled ticket, counted from its assignment onward across every skill.</summary>
    public record TicketMeasure(int TicketId, string? Title, string Arm, int Ordinal, DateTime AssignedAt,
        string Phase, DateTime? DoneAt, int Runs, double WorkerMinutes, long FreshTokens, long CacheReadTokens,
        bool HadQaFailure, bool HadRework, int ReworkRuns, bool EscapedBug, int RunsApplied);

    /// <summary>A guardrail for one arm: tickets with an outcome for it, and how many were bad.</summary>
    public record Rate(int N, int Bad)
    {
        public double? Value => N == 0 ? null : (double)Bad / N;
    }

    public record ArmResult(string Key, string Name, string? SkillVariant, string? Model, string? Effort, string? Provider,
        int Tickets, int Done, double? MedianMinutes, double? MedianFreshTokens, double? MedianCacheRead,
        Rate QaFailure, Rate Rework, Rate EscapedBug, double ReworkShare);

    /// <summary>The variant against control on one guardrail.</summary>
    public record GuardrailCheck(string Name, double P, bool Breaks, bool Passes);

    public record ExperimentView(
        int Id, string ProjectKey, string Skill, string Name, string? Hypothesis, string Status, string Stage,
        int MinTicketsPerArm, int WaitDays, DateTime StartedAt, DateTime? StoppedAt, string? StopKind, string? StopReason,
        DateTime? ConcludedAt, string? Winner, string? ConclusionNote, DateTime? LastDoneAt, DateTime? VerdictFrom,
        IReadOnlyList<ArmResult> Arms, IReadOnlyList<GuardrailCheck> Checks, string? Suggestion, string SuggestionWhy,
        IReadOnlyList<TicketMeasure> Tickets);

    public async Task<IReadOnlyList<ExperimentView>> ListAsync(string? projectKey, DateTime? now = null)
    {
        var q = _db.Experiments.Include(e => e.Arms).AsQueryable();
        if (!string.IsNullOrWhiteSpace(projectKey)) q = q.Where(e => e.ProjectKey == projectKey);
        var list = await q.OrderByDescending(e => e.StartedAt).ToListAsync();
        var views = new List<ExperimentView>();
        foreach (var e in list) views.Add(await ResultsAsync(e, now ?? DateTime.UtcNow));
        return views;
    }

    public async Task<ExperimentView?> ViewAsync(int id, DateTime? now = null)
    {
        var exp = await _db.Experiments.Include(e => e.Arms).FirstOrDefaultAsync(e => e.Id == id);
        return exp is null ? null : await ResultsAsync(exp, now ?? DateTime.UtcNow);
    }

    public async Task<ExperimentView> ResultsAsync(Experiment exp, DateTime now)
    {
        var measures = await MeasureAsync(exp);
        var arms = exp.Arms.OrderBy(a => a.Key == ExperimentArm.Control ? 0 : 1)
            .Select(a => ArmOf(a, measures.Where(m => m.Arm == a.Key).ToList(), exp.WaitDays, now)).ToList();

        var taken = measures.OrderBy(m => m.Ordinal).Select(m => m.Arm).ToList();
        var lastDone = measures.Count > 0 && measures.All(m => m.DoneAt is not null) ? measures.Max(m => m.DoneAt) : null;
        var verdictFrom = lastDone?.AddDays(exp.WaitDays);
        var stage = exp.Status switch
        {
            ExperimentStatus.Concluded => Stages.Concluded,
            ExperimentStatus.Stopped => Stages.Stopped,
            _ when EnrolmentOpen(exp, taken) => Stages.Enrolling,
            _ when lastDone is null => Stages.Landing,
            _ when verdictFrom > now => Stages.Waiting,
            _ => Stages.Ready,
        };

        var control = arms.FirstOrDefault(a => a.Key == ExperimentArm.Control);
        var variant = arms.FirstOrDefault(a => a.Key == ExperimentArm.Variant);
        var checks = new List<GuardrailCheck>();
        if (control is not null && variant is not null)
            foreach (var (name, c, v) in new[]
                     {
                         (Guardrails.QaFailure, control.QaFailure, variant.QaFailure),
                         (Guardrails.Rework, control.Rework, variant.Rework),
                         (Guardrails.EscapedBug, control.EscapedBug, variant.EscapedBug),
                     })
                checks.Add(Check(name, c, v));

        var (suggestion, why) = Suggest(control, variant, checks);
        return new ExperimentView(exp.Id, exp.ProjectKey, exp.Skill, exp.Name, exp.Hypothesis, exp.Status.ToString(), stage,
            exp.MinTicketsPerArm, exp.WaitDays, exp.StartedAt, exp.StoppedAt, exp.StopKind?.ToString(), exp.StopReason,
            exp.ConcludedAt, exp.Winner, exp.ConclusionNote, lastDone, verdictFrom, arms, checks, suggestion, why, measures);
    }

    /// <summary>The auto-stop rule (2.6) and the verdict's pass (2.7), for one guardrail.</summary>
    public static GuardrailCheck Check(string name, Rate control, Rate variant)
    {
        var p = FisherOneSided(variant.Bad, variant.N, control.Bad, control.N);
        var breaks = variant.N >= StopMinTickets && variant.Bad >= StopMinBad && p < StopP;
        return new GuardrailCheck(name, p, breaks, p >= VerdictP);
    }

    private static (string?, string) Suggest(ArmResult? control, ArmResult? variant, IReadOnlyList<GuardrailCheck> checks)
    {
        if (control is null || variant is null) return (null, "Both arms are needed.");
        if (checks.FirstOrDefault(c => !c.Passes) is { } failed)
            return (ExperimentArm.Control, $"The variant is worse on {failed.Name} (p = {failed.P:0.000}).");
        if (control.MedianMinutes is null || variant.MedianMinutes is null || control.MedianFreshTokens is null || variant.MedianFreshTokens is null)
            return (null, "No ticket of one arm has reached Done yet.");
        var faster = variant.MedianMinutes < control.MedianMinutes;
        var cheaper = variant.MedianFreshTokens < control.MedianFreshTokens;
        if (faster && cheaper) return (ExperimentArm.Variant, "The variant passes every guardrail and is quicker and cheaper per ticket to Done.");
        if (!faster && !cheaper) return (ExperimentArm.Control, "The variant passes every guardrail but is no quicker and no cheaper per ticket to Done.");
        return (null, faster
            ? "The variant is quicker per ticket but uses more tokens. Your call."
            : "The variant uses fewer tokens per ticket but takes longer. Your call.");
    }

    private static ArmResult ArmOf(ExperimentArm a, IReadOnlyList<TicketMeasure> m, int waitDays, DateTime now)
    {
        var done = m.Where(x => x.DoneAt is not null).ToList();
        // QA failure and rework are known once a ticket is Done. An escaped bug is known once it has
        // been Done for the wait - or as soon as one is confirmed, since a bad outcome cannot un-happen.
        var matured = m.Where(x => x.EscapedBug || (x.DoneAt is { } d && d.AddDays(waitDays) <= now)).ToList();
        var runs = m.Sum(x => x.Runs);
        return new ArmResult(a.Key, a.Name, a.SkillVariant, a.Model, a.Effort, a.Provider,
            m.Count, done.Count,
            MedianOf(done.Select(x => x.WorkerMinutes)), MedianOf(done.Select(x => (double)x.FreshTokens)),
            MedianOf(done.Select(x => (double)x.CacheReadTokens)),
            new Rate(done.Count, done.Count(x => x.HadQaFailure)),
            new Rate(done.Count, done.Count(x => x.HadRework)),
            new Rate(matured.Count, matured.Count(x => x.EscapedBug)),
            runs == 0 ? 0 : (double)m.Sum(x => x.ReworkRuns) / runs);
    }

    private static double? MedianOf(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 0) return null;
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }

    private async Task<List<TicketMeasure>> MeasureAsync(Experiment exp)
    {
        var assigned = await _db.ExperimentAssignments.Where(a => a.ExperimentId == exp.Id).ToListAsync();
        if (assigned.Count == 0) return [];
        var ids = assigned.Select(a => a.TicketId).ToList();
        var tickets = await _db.Tickets.Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, Title = t.Title ?? t.IssueKey, t.Phase }).ToDictionaryAsync(t => t.Id);
        var runs = await _db.CycleRuns.Where(r => ids.Contains(r.TicketId)).ToListAsync();
        var moves = await _db.PhaseTransitions.Where(p => ids.Contains(p.TicketId)).ToListAsync();
        var escaped = (await _db.BugLinks
                .Where(l => l.SourceTicketId != null && ids.Contains(l.SourceTicketId.Value) && l.Status == BugLinkStatus.Confirmed && l.Escaped)
                .Select(l => l.SourceTicketId!.Value).ToListAsync())
            .ToHashSet();

        var result = new List<TicketMeasure>();
        foreach (var a in assigned)
        {
            // A ticket since deleted is no longer anyone's evidence.
            if (!tickets.TryGetValue(a.TicketId, out var t)) continue;
            var mine = runs.Where(r => r.TicketId == a.TicketId && r.StartedAt >= a.AssignedAt.AddMinutes(-1)).ToList();
            var after = moves.Where(p => p.TicketId == a.TicketId && p.At >= a.AssignedAt).ToList();
            var doneAt = t.Phase == TicketPhase.Done
                ? after.Where(p => p.To == TicketPhase.Done).OrderByDescending(p => p.At).Select(p => (DateTime?)p.At).FirstOrDefault()
                  ?? moves.Where(p => p.TicketId == a.TicketId && p.To == TicketPhase.Done).OrderByDescending(p => p.At).Select(p => (DateTime?)p.At).FirstOrDefault()
                : null;
            var rework = mine.Count(r => r.EntryReason is { } e && ReworkReasons.Contains(e));
            result.Add(new TicketMeasure(a.TicketId, t.Title, a.ArmKey, a.Ordinal, a.AssignedAt, t.Phase.ToString(), doneAt,
                mine.Count, mine.Sum(r => r.DurationSeconds) / 60.0,
                mine.Sum(r => (long)(r.InputTokens ?? 0) + (r.OutputTokens ?? 0) + (r.CacheCreationTokens ?? 0)),
                mine.Sum(r => (long)(r.CacheReadTokens ?? 0)),
                after.Any(p => p.From == TicketPhase.QaTesting && p.Backward),
                rework > 0, rework, escaped.Contains(a.TicketId),
                mine.Count(r => r.ExperimentId == exp.Id && r.ExperimentApplied == true)));
        }
        return result;
    }

    /// <summary>
    /// Stop every running experiment in the project whose variant has clearly broken a guardrail
    /// (2.6). Called after each recorded run and each confirmed bug link. Returns those it stopped.
    /// </summary>
    public async Task<IReadOnlyList<Experiment>> CheckGuardrailsAsync(string projectKey, DateTime? now = null)
    {
        var stopped = new List<Experiment>();
        var running = await _db.Experiments.Include(e => e.Arms)
            .Where(e => e.ProjectKey == projectKey && e.Status == ExperimentStatus.Running).ToListAsync();
        foreach (var exp in running)
        {
            var view = await ResultsAsync(exp, now ?? DateTime.UtcNow);
            var broken = view.Checks.FirstOrDefault(c => c.Breaks);
            if (broken is null) continue;
            var v = view.Arms.First(a => a.Key == ExperimentArm.Variant);
            var c = view.Arms.First(a => a.Key == ExperimentArm.Control);
            var (vr, cr) = broken.Name switch
            {
                Guardrails.QaFailure => (v.QaFailure, c.QaFailure),
                Guardrails.Rework => (v.Rework, c.Rework),
                _ => (v.EscapedBug, c.EscapedBug),
            };
            await StopAsync(exp.Id,
                $"the variant broke the {broken.Name} guardrail: {vr.Bad} of {vr.N} tickets against {cr.Bad} of {cr.N} for control (one-sided Fisher p = {broken.P:0.0000})",
                ExperimentStopKind.Guardrail);
            stopped.Add(exp);
        }
        return stopped;
    }

    // ---- The arithmetic --------------------------------------------------------------------

    /// <summary>
    /// One-sided Fisher's exact test: the chance, with the margins fixed, of the variant having at
    /// least <paramref name="badV"/> bad tickets of <paramref name="nV"/> when control had
    /// <paramref name="badC"/> of <paramref name="nC"/> and the arms do not differ. 1 when there is
    /// nothing to compare.
    /// </summary>
    public static double FisherOneSided(int badV, int nV, int badC, int nC)
    {
        if (nV <= 0 || nC <= 0) return 1;
        var n = nV + nC;
        var k = badV + badC;
        double p = 0;
        for (var x = badV; x <= Math.Min(k, nV); x++)
            p += Math.Exp(LogChoose(k, x) + LogChoose(n - k, nV - x) - LogChoose(n, nV));
        return Math.Min(1, p);
    }

    private static double LogChoose(int n, int k) =>
        k < 0 || k > n ? double.NegativeInfinity : LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    private static double LogFactorial(int n)
    {
        double s = 0;
        for (var i = 2; i <= n; i++) s += Math.Log(i);
        return s;
    }
}
