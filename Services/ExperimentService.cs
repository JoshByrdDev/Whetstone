using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// A/B experiments on one phase's skill or agent settings (docs/bugs-and-experiments-plan.md,
/// section 2): assignment at the claim, the per-ticket measures, the guardrail behind auto-stop, and
/// the stages a verdict waits through.
/// <para>
/// Starting, stopping and declaring are the owner's: they are called from REST routes and from
/// nothing else, and <c>ExperimentTests.No_tool_can_confirm_a_bug_link_or_steer_an_experiment</c> holds that no MCP tool reaches this service.
/// </para>
/// </summary>
public partial class ExperimentService
{
    /// <summary>The fewest variant tickets with an outcome, and the fewest bad ones, before a guardrail may stop anything.</summary>
    public const int StopMinTickets = 6;
    public const int StopMinBad = 4;
    /// <summary>One-sided Fisher p below which a guardrail stops the experiment. Strict, because it is checked after every run.</summary>
    public const double StopP = 0.01;
    /// <summary>The same test at the end, for the suggested verdict: one look, so looser.</summary>
    public const double VerdictP = 0.10;

    /// <summary>The reasons a run can be on a ticket that count as rework against the arm. A conflict is caused by other tickets, so it does not.</summary>
    public static readonly string[] ReworkReasons =
        { RunEntryReasons.QaFailed, RunEntryReasons.RefineSentBack, RunEntryReasons.ReviewChanges };

    private static readonly SemaphoreSlim EnrolLock = new(1, 1);
    private static readonly Regex VariantName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private readonly DashboardDbContext _db;
    private readonly Func<int> _coin;

    /// <param name="coin">0 or 1, for the order inside each pair. Random unless a test pins it.</param>
    public ExperimentService(DashboardDbContext db, Func<int>? coin = null)
        => (_db, _coin) = (db, coin ?? (() => RandomNumberGenerator.GetInt32(2)));

    // ---- The claim -------------------------------------------------------------------------

    /// <summary>
    /// What a claimed run gets from an experiment: its arm, whether the arm's overrides apply, and the
    /// overrides. Null when the ticket is in no experiment for this skill.
    /// </summary>
    public record ClaimArm(int Id, string Arm, bool Applied, string? SkillVariant, string? Model, string? Effort, string? Provider,
        string? Note = null);

    /// <summary>
    /// The arm for a run that has just been claimed (2.3). A ticket already in an experiment for this
    /// skill keeps its arm; a ticket new to the skill is enrolled while enrolment is open; anything
    /// else runs as configured. After a stop, every enrolled ticket reports its arm unapplied.
    /// </summary>
    /// <param name="variantExists">Whether a variant file exists for (skill, name). Checked at every claim, since the file is the owner's to delete.</param>
    public async Task<ClaimArm?> ForClaimAsync(Ticket ticket, string skill, Func<string, string, bool> variantExists)
    {
        var assignment = await _db.ExperimentAssignments
            .Include(a => a.Experiment!).ThenInclude(e => e.Arms)
            .Where(a => a.TicketId == ticket.Id && a.Experiment!.Skill == skill && a.Experiment.ProjectKey == ticket.ProjectKey)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        if (assignment is null)
        {
            var running = await _db.Experiments.Include(e => e.Arms)
                .FirstOrDefaultAsync(e => e.ProjectKey == ticket.ProjectKey && e.Skill == skill && e.Status == ExperimentStatus.Running);
            if (running is null) return null;
            assignment = await EnrolAsync(running, ticket);
            if (assignment is null) return null;
        }

        var exp = assignment.Experiment!;
        var arm = exp.Arms.First(a => a.Key == assignment.ArmKey);
        if (exp.Status != ExperimentStatus.Running)
            return new ClaimArm(exp.Id, arm.Key, false, null, null, null, null, $"experiment {exp.Status.ToString().ToLowerInvariant()}: running as configured");
        if (arm.SkillVariant is { Length: > 0 } v && !variantExists(skill, v))
            return new ClaimArm(exp.Id, arm.Key, false, null, null, null, null, $"variant file '{v}.md' is missing: running skill.md");
        return new ClaimArm(exp.Id, arm.Key, true, arm.SkillVariant, arm.Model, arm.Effort, arm.Provider);
    }

    /// <summary>
    /// Enrol a ticket, or null when it may not be: enrolment is full, or the ticket has already run
    /// this skill (it would join halfway, and its earlier passes would count for no arm).
    /// </summary>
    private async Task<ExperimentAssignment?> EnrolAsync(Experiment exp, Ticket ticket)
    {
        if (await _db.CycleRuns.AnyAsync(r => r.TicketId == ticket.Id && r.Skill == exp.Skill)) return null;

        await EnrolLock.WaitAsync();
        try
        {
            var taken = await _db.ExperimentAssignments.Where(a => a.ExperimentId == exp.Id)
                .OrderBy(a => a.Ordinal).Select(a => a.ArmKey).ToListAsync();
            if (!EnrolmentOpen(exp, taken)) return null;

            var ordinal = taken.Count + 1;
            var arm = NextArm(taken, _coin);
            var a = new ExperimentAssignment { ExperimentId = exp.Id, TicketId = ticket.Id, ArmKey = arm, Ordinal = ordinal, Experiment = exp };
            _db.ExperimentAssignments.Add(a);
            await _db.SaveChangesAsync();
            return a;
        }
        finally { EnrolLock.Release(); }
    }

    public static bool EnrolmentOpen(Experiment exp, IReadOnlyList<string> taken) =>
        taken.Count(k => k == ExperimentArm.Control) < exp.MinTicketsPerArm
        || taken.Count(k => k == ExperimentArm.Variant) < exp.MinTicketsPerArm;

    /// <summary>
    /// Permuted blocks of two: the first of each pair by a coin, the second the other arm - balanced
    /// to within one at every count, and unpredictable inside each pair (2.3). Once one arm has its
    /// minimum, the other is filled.
    /// </summary>
    public static string NextArm(IReadOnlyList<string> taken, Func<int> coin)
    {
        if (taken.Count % 2 == 1)
            return taken[^1] == ExperimentArm.Control ? ExperimentArm.Variant : ExperimentArm.Control;
        return coin() == 0 ? ExperimentArm.Control : ExperimentArm.Variant;
    }

    // ---- The owner's moves -------------------------------------------------------------------

    public record ArmSpec(string? Name, string? SkillVariant, string? Model, string? Effort, string? Provider);

    public record CreateSpec(string ProjectKey, string Skill, string Name, string? Hypothesis,
        int? MinTicketsPerArm, int? WaitDays, ArmSpec? Control, ArmSpec? Variant);

    /// <summary>Thrown for an experiment that cannot be started, stopped or declared as asked; the route answers 400 with it.</summary>
    public class Refused(string message) : Exception(message);

    /// <param name="variantExists">Whether a variant file exists for (skill, name).</param>
    /// <param name="providersInUse">Providers some configured role already runs on - the only ones an arm may switch to (2.4).</param>
    public async Task<Experiment> CreateAsync(CreateSpec spec, Func<string, string, bool> variantExists,
        IReadOnlyCollection<string> providersInUse)
    {
        if (!PhaseMachine.AllSkills.Contains(spec.Skill))
            throw new Refused($"'{spec.Skill}' is not a phase's skill. One of: {string.Join(", ", PhaseMachine.AllSkills)}.");
        if (string.IsNullOrWhiteSpace(spec.Name)) throw new Refused("Give the experiment a name.");
        if (await _db.Experiments.AnyAsync(e => e.ProjectKey == spec.ProjectKey && e.Skill == spec.Skill && e.Status == ExperimentStatus.Running))
            throw new Refused($"An experiment on {spec.Skill} is already running in this project. Stop it first.");

        var min = spec.MinTicketsPerArm ?? 8;
        var wait = spec.WaitDays ?? 21;
        if (min is < 2 or > 200) throw new Refused("Tickets per arm must be between 2 and 200.");
        if (wait is < 0 or > 365) throw new Refused("The wait must be between 0 and 365 days.");

        ExperimentArm Arm(string key, ArmSpec? a, string fallbackName)
        {
            var arm = new ExperimentArm
            {
                Key = key,
                Name = string.IsNullOrWhiteSpace(a?.Name) ? fallbackName : a!.Name!.Trim(),
                SkillVariant = Blank(a?.SkillVariant), Model = Blank(a?.Model),
                Effort = Blank(a?.Effort)?.ToLowerInvariant(), Provider = Blank(a?.Provider)?.ToLowerInvariant(),
            };
            if (arm.SkillVariant is { } v)
            {
                if (v.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) arm.SkillVariant = v = v[..^3];
                if (!VariantName.IsMatch(v)) throw new Refused($"'{v}' is not a variant file name.");
                if (!variantExists(spec.Skill, v)) throw new Refused($"There is no skills/{spec.Skill}/variants/{v}.md in this project's folder.");
            }
            if (arm.Effort is { } e && !AgentRoles.Efforts.Contains(e))
                throw new Refused($"Effort must be one of {string.Join(", ", AgentRoles.Efforts)}.");
            if (arm.Provider is { } p && !providersInUse.Contains(p))
                throw new Refused($"'{p}' is not a provider any role runs on ({string.Join(", ", providersInUse)}), so nothing would reap its processes.");
            return arm;
        }

        var exp = new Experiment
        {
            ProjectKey = spec.ProjectKey, Skill = spec.Skill, Name = spec.Name.Trim(), Hypothesis = Blank(spec.Hypothesis),
            MinTicketsPerArm = min, WaitDays = wait, StartedAt = DateTime.UtcNow,
            Arms = { Arm(ExperimentArm.Control, spec.Control, "Control"), Arm(ExperimentArm.Variant, spec.Variant, "Variant") },
        };
        if (exp.Arms.All(a => a.SkillVariant is null && a.Model is null && a.Effort is null && a.Provider is null))
            throw new Refused("The two arms are the same. Give the variant at least one override.");
        if (Same(exp.Arms[0], exp.Arms[1])) throw new Refused("The two arms are the same.");
        _db.Experiments.Add(exp);
        await _db.SaveChangesAsync();
        return exp;
    }

    private static bool Same(ExperimentArm a, ExperimentArm b) =>
        a.SkillVariant == b.SkillVariant && a.Model == b.Model && a.Effort == b.Effort && a.Provider == b.Provider;

    /// <summary>Stop it: every later run gets control's settings (2.6). Refused for one that is not running.</summary>
    public async Task<Experiment?> StopAsync(int id, string? reason, ExperimentStopKind kind = ExperimentStopKind.Human)
    {
        var exp = await _db.Experiments.FindAsync(id);
        if (exp is null) return null;
        if (exp.Status != ExperimentStatus.Running) throw new Refused("It is not running.");
        exp.Status = ExperimentStatus.Stopped;
        exp.StoppedAt = DateTime.UtcNow;
        exp.StopKind = kind;
        exp.StopReason = Blank(reason) ?? (kind == ExperimentStopKind.Human ? "Stopped by the owner." : "Stopped by a guardrail.");

        var inFlight = await (from a in _db.ExperimentAssignments
                              join t in _db.Tickets on a.TicketId equals t.Id
                              where a.ExperimentId == id && t.Phase != TicketPhase.Done
                              select t.Id).ToListAsync();
        foreach (var t in inFlight)
            _db.Comments.Add(new Comment
            {
                TicketId = t, Author = AuthorKind.System,
                Body = $"Experiment \"{exp.Name}\" on {exp.Skill} stopped ({exp.StopReason}). Every later {exp.Skill} run on this ticket uses the configured settings.",
            });
        await _db.SaveChangesAsync();
        return exp;
    }

    /// <summary>Declare a winner, once the wait for escaped bugs is over or after a stop (2.7).</summary>
    public async Task<Experiment?> DeclareAsync(int id, string? winner, string? note, DateTime? now = null)
    {
        var exp = await _db.Experiments.Include(e => e.Arms).FirstOrDefaultAsync(e => e.Id == id);
        if (exp is null) return null;
        var w = (winner ?? "").Trim().ToLowerInvariant();
        if (w is not (ExperimentArm.Control or ExperimentArm.Variant or "none"))
            throw new Refused("The winner is control, variant or none.");
        var view = await ResultsAsync(exp, now ?? DateTime.UtcNow);
        if (view.Stage is not (Stages.Ready or Stages.Stopped))
            throw new Refused(view.Stage switch
            {
                Stages.Enrolling => "Enrolment is still open.",
                Stages.Landing => "Some enrolled tickets have not reached Done yet. Stop it to declare early.",
                Stages.Waiting => $"Escaped bugs are still being waited for, until {view.VerdictFrom:yyyy-MM-dd}. Stop it to declare early.",
                _ => "It has already been declared.",
            });
        exp.Status = ExperimentStatus.Concluded;
        exp.ConcludedAt = DateTime.UtcNow;
        exp.Winner = w;
        exp.ConclusionNote = Blank(note);
        await _db.SaveChangesAsync();
        return exp;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
