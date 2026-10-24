using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// What is not a decision (docs/plans-as-slices.md, section 3): instructions with an end, the
/// decisions a ruling reads, and the follow-ups an answer leaves open. None of them is in the
/// register, and each points into it by number rather than restating it.
/// </summary>
public partial class StewardService
{
    public const int InstructionMaxChars = 1200;

    // ---- instructions --------------------------------------------------------------------------

    /// <summary>The owner tells the steward to do something until a condition holds. A reason to run.</summary>
    public async Task<StewardReply> OwnerInstructsAsync(string projectKey, string? text, string? until, CancellationToken ct = default)
    {
        if (Config(projectKey) is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        text = text?.Trim() ?? "";
        until = until?.Trim() ?? "";
        if (text.Length == 0) return Refuse("bad-request", "Say what to do.");
        if (until.Length == 0) return Refuse("bad-request", "Say when it ends: an instruction without an end is a decision, and goes in the register.");
        if (text.Length > InstructionMaxChars || until.Length > 600) return Refuse("bad-request", "Keep an instruction to a few sentences, and its end to one.");
        var i = new StewardInstruction { ProjectKey = projectKey, Text = text, Until = until };
        _db.StewardInstructions.Add(i);
        await StewardDue.MarkAsync(_db, projectKey, "the owner left an instruction");
        await _db.SaveChangesAsync(ct);
        _heartbeat?.Wake();
        return new StewardReply("recorded", null, new { id = i.Id });
    }

    /// <summary>The owner closes an instruction, whether or not its end has come.</summary>
    public async Task<StewardReply> OwnerClosesInstructionAsync(string projectKey, int id, string? note, CancellationToken ct = default)
    {
        var i = await _db.StewardInstructions.FirstOrDefaultAsync(x => x.Id == id && x.ProjectKey == projectKey, ct);
        if (i is null) return Refuse("not-found", $"There is no instruction {id} here.");
        if (i.ClosedAt is not null) return Refuse("bad-request", $"Instruction {id} was closed on {i.ClosedAt:yyyy-MM-dd}.");
        (i.ClosedAt, i.ClosedBy, i.ClosedNote) = (DateTime.UtcNow, "owner", string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        await _db.SaveChangesAsync(ct);
        return new StewardReply("closed");
    }

    /// <summary>The steward closes an instruction whose end has come, saying how it came.</summary>
    public async Task<StewardReply> CloseInstructionAsync(string projectKey, int id, string? how, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var i = await _db.StewardInstructions.FirstOrDefaultAsync(x => x.Id == id && x.ProjectKey == projectKey, ct);
        if (i is null) return Refuse("not-found", $"There is no instruction {id} here.");
        if (i.ClosedAt is not null) return Refuse("already-closed", $"Instruction {id} is closed already.");
        if (string.IsNullOrWhiteSpace(how)) return Refuse("bad-request", $"Say how its end came: \"{i.Until}\". If it has not, it is still in force.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"close instruction {id}: {Head(how, 200)}");
        (i.ClosedAt, i.ClosedBy, i.ClosedNote) = (DateTime.UtcNow, "steward", how.Trim());
        await _db.SaveChangesAsync(ct);
        return new StewardReply("closed");
    }

    private Task<List<StewardInstruction>> OpenInstructionsAsync(string projectKey, CancellationToken ct) =>
        _db.StewardInstructions.AsNoTracking().Where(i => i.ProjectKey == projectKey && i.ClosedAt == null)
            .OrderBy(i => i.At).ToListAsync(ct);

    // ---- follow-ups ----------------------------------------------------------------------------

    public const int FollowUpMaxChars = 600;

    /// <summary>
    /// The steward records a caveat the owner left in an answer, against the decision it came with.
    /// The owner fills in nothing extra: reading an answer for what it leaves open is the steward's.
    /// </summary>
    public async Task<StewardReply> FollowUpAsync(string projectKey, int decision, string? text, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        text = text?.Trim() ?? "";
        if (text.Length == 0) return Refuse("bad-request", "Say what was left open, in a line, quoting the owner where you can.");
        if (text.Length > FollowUpMaxChars) return Refuse("bad-request", $"A follow-up is a line, at most {FollowUpMaxChars} characters.");
        await EnsureRegisterAsync(cfg, ct);
        if (!await _db.StewardDecisions.AnyAsync(d => d.ProjectKey == projectKey && d.Number == decision, ct))
            return Refuse("bad-request", $"There is no decision #{decision}.");
        if (await _db.StewardFollowUps.AnyAsync(f => f.ProjectKey == projectKey && f.Decision == decision && f.Text == text, ct))
            return new StewardReply("already-noted");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"follow up #{decision}: {Head(text, 200)}");
        var f = new StewardFollowUp { ProjectKey = projectKey, Decision = decision, Text = text };
        _db.StewardFollowUps.Add(f);
        await _db.SaveChangesAsync(ct);
        return new StewardReply("noted", null, new { id = f.Id });
    }

    /// <summary>The owner drops a follow-up as nothing to take up.</summary>
    public async Task<StewardReply> OwnerDropsFollowUpAsync(string projectKey, int id, CancellationToken ct = default)
    {
        var f = await _db.StewardFollowUps.FirstOrDefaultAsync(x => x.Id == id && x.ProjectKey == projectKey, ct);
        if (f is null) return Refuse("not-found", $"There is no follow-up {id} here.");
        if (f.CitedBy is not null || f.DroppedAt is not null) return Refuse("bad-request", "That follow-up is no longer waiting.");
        f.DroppedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new StewardReply("dropped");
    }

    /// <summary>Follow-ups a question or an idea takes up. Refused, before anything is written, when one is not waiting.</summary>
    private async Task<(List<StewardFollowUp> Rows, StewardReply? No)> FollowUpsToCiteAsync(string projectKey, IReadOnlyList<int>? ids, CancellationToken ct)
    {
        var want = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (want.Count == 0) return (new(), null);
        var rows = await _db.StewardFollowUps.Where(f => f.ProjectKey == projectKey && want.Contains(f.Id)).ToListAsync(ct);
        if (want.FirstOrDefault(i => rows.All(r => r.Id != i)) is var missing and > 0)
            return (rows, Refuse("bad-request", $"There is no follow-up {missing}."));
        if (rows.FirstOrDefault(r => r.CitedBy is not null || r.DroppedAt is not null) is { } gone)
            return (rows, Refuse("bad-request", $"Follow-up {gone.Id} is no longer waiting."));
        return (rows, null);
    }

    private static void Cite(IEnumerable<StewardFollowUp> rows, string by)
    {
        foreach (var r in rows) (r.CitedBy, r.CitedAt) = (by, DateTime.UtcNow);
    }

    private Task<List<StewardFollowUp>> OpenFollowUpsAsync(string projectKey, CancellationToken ct) =>
        _db.StewardFollowUps.AsNoTracking().Where(f => f.ProjectKey == projectKey && f.CitedBy == null && f.DroppedAt == null)
            .OrderBy(f => f.At).ToListAsync(ct);

    // ---- rulings on decisions that no longer hold ---------------------------------------------

    /// <summary>
    /// Each ruling in force that reads a decision which no longer holds, with what happened to it.
    /// A ruling is a reading of decisions, so a reading of a dead one is flagged until the owner
    /// reverses it or keeps it.
    /// </summary>
    public async Task<Dictionary<int, string>> RulingFlagsAsync(string projectKey, CancellationToken ct = default)
    {
        var rulings = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Ruling && e.ReversedAt == null && e.Reads != null)
            .ToListAsync(ct);
        var flags = new Dictionary<int, string>();
        if (rulings.Count == 0) return flags;
        var all = await RegisterAsync(projectKey, track: false, ct);
        var map = ReplacedIndex(all);
        foreach (var r in rulings)
        {
            var dead = Numbers(r.Reads).Select(n => (n, d: all.FirstOrDefault(x => x.Number == n))).Where(x => x.d is not null)
                .Select(x => (x.n, st: StatusOf(x.d!, map))).Where(x => x.st.Name is Replaced or Withdrawn)
                .Select(x => x.st.Name == Withdrawn ? $"#{x.n} was withdrawn"
                    : $"#{x.n} was replaced by {string.Join(", ", x.st.ReplacedBy.Select(b => $"#{b.By}"))}").ToList();
            if (dead.Count > 0) flags[r.Id] = string.Join("; ", dead);
        }
        return flags;
    }

    // ---- provisional decisions coming due ---------------------------------------------------------

    /// <summary>The units of the plan being worked whose every outcome is done.</summary>
    private async Task<HashSet<int>> BuiltUnitsAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var built = new HashSet<int>();
        if (!cfg.Steward.Plan.Configured) return built;
        var key = cfg.Steward.Plan.Key;
        var plan = await _git.PlanAsync(cfg.Name, ct);
        var states = await StatesAsync(cfg.Name, key, ct);
        var mine = (await _tickets.ListAsync(cfg.Name, null)).Where(t => (t.PlanKey ?? "") == key).ToList();
        foreach (var u in plan.Where(u => u.Agreed))
        {
            var p = StewardProgress.For(u, states, mine);
            if (p.Outcomes > 0 && p.Done == p.Outcomes) built.Add(u.Number);
        }
        return built;
    }

    /// <summary>
    /// The owner confirms a provisional decision: it stops being provisional and holds like any
    /// other. To change it instead, record one that replaces it; to let it lapse, withdraw it.
    /// </summary>
    public async Task<StewardReply> OwnerConfirmsDecisionAsync(string projectKey, int number, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        await EnsureRegisterAsync(cfg, ct);
        var all = await RegisterAsync(projectKey, track: true, ct);
        var d = all.FirstOrDefault(x => x.Number == number);
        if (d is null) return Refuse("bad-request", $"There is no decision #{number}.");
        if (StatusOf(d, ReplacedIndex(all)).Name != Provisional) return Refuse("bad-request", $"#{number} is not provisional.");
        (d.Provisional, d.ProvisionalUnit, d.ChangedAt, d.DeliveredAt) = (false, null, DateTime.UtcNow, null);
        await StewardDue.MarkAsync(_db, projectKey, $"the owner confirmed #{number}");
        await _db.SaveChangesAsync(ct);
        var exported = await ExportDecisionsAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("confirmed", exported);
    }

    // ---- the briefing -------------------------------------------------------------------------

    /// <summary>
    /// What a run is handed at its start besides the board: the decisions in force for the slice
    /// being worked, the provisional ones coming due, rulings that read a decision no longer in
    /// force, open follow-ups and open instructions.
    /// </summary>
    private async Task<object> BriefingAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var projectKey = cfg.Name;
        await EnsureRegisterAsync(cfg, ct);
        var all = await RegisterAsync(projectKey, track: false, ct);
        var map = ReplacedIndex(all);
        var titles = await UnitTitlesAsync(cfg, ct);
        var plan = cfg.Steward.Plan.Configured ? await _git.PlanAsync(projectKey, ct) : Array.Empty<PlanUnit>();
        var current = plan.Count == 0 ? null
            : StewardProgress.Current(plan, await StatesAsync(projectKey, cfg.Steward.Plan.Key, ct))?.Number;
        var built = await BuiltUnitsAsync(cfg, ct);
        var standing = all.Select(d => (d, st: StatusOf(d, map))).Where(x => x.st.Name is InForce or Provisional).ToList();
        var flags = await RulingFlagsAsync(projectKey, ct);
        var rulings = flags.Count == 0 ? new List<StewardEntry>() : await _db.StewardEntries.AsNoTracking()
            .Where(e => flags.Keys.Contains(e.Id)).ToListAsync(ct);
        return new
        {
            slice = current,
            // The slice's own; the project's are a steward_decisions call away, and are many.
            sliceDecisions = current is null ? null : standing.Where(x => x.d.PlanUnit == current && (x.d.PlanKey ?? "") == cfg.Steward.Plan.Key)
                .Select(x => Shape(x.d, x.st, titles)),
            projectDecisions = standing.Count(x => x.d.PlanUnit is null),
            provisionalDue = standing.Where(x => x.d.Provisional && x.d.ProvisionalUnit is { } u && built.Contains(u))
                .Select(x => new { number = x.d.Number, rule = x.d.Rule, until = $"unit {x.d.ProvisionalUnit} is built" }),
            flaggedRulings = rulings.Select(e => new { e.Id, ruling = e.Text, flag = flags[e.Id] }),
            followUps = (await OpenFollowUpsAsync(projectKey, ct)).Select(f => new { f.Id, decision = f.Decision, f.Text }),
            instructions = (await OpenInstructionsAsync(projectKey, ct)).Select(i => new { i.Id, i.Text, i.Until }),
        };
    }
}
