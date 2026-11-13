using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>A decision the owner records from the register page, and what it replaces.</summary>
public record StewardNewDecision(string? Rule, string? Title = null, int? Unit = null, IReadOnlyList<string>? Outcomes = null,
    bool Provisional = false, int? ProvisionalUnit = null, IReadOnlyList<StewardReplacing>? Replaces = null, bool Convention = false);

/// <summary>One decision a newer one overrides, and in a line why.</summary>
public record StewardReplacing(int Number, string? Why);

/// <summary>
/// The register of decisions (docs/plans-as-slices.md, section 3): the one place a decision's status
/// lives. Everything that used to count as decided - the plan document's decisions section, each
/// spec's "Decided", the answers to questions, the owner's standing words - is taken into it once,
/// and from then on each of those roads writes a row here as the decision is made. The decisions
/// file and <c>steward_decisions</c> are read from it, never from the places it was built from.
/// </summary>
public partial class StewardService
{
    /// <summary>Statuses, as the register writes them.</summary>
    public const string InForce = "inForce", Replaced = "replaced", Provisional = "provisional", Withdrawn = "withdrawn",
        Retired = "retired";

    private sealed record Status(string Name, IReadOnlyList<(int By, string Why)> ReplacedBy)
    {
        /// <summary>No longer holds: replaced, withdrawn, or retired with its slice. Never acted on.</summary>
        public bool Gone => Name is Replaced or Withdrawn or Retired;
    }

    /// <summary>
    /// What a decision's status is read from besides itself: what replaced it, and which slices
    /// are over, released or abandoned, so their decisions are retired (docs/slice-branches.md,
    /// section 4). Retired is derived, never written.
    /// </summary>
    private sealed class DecisionIndex : Dictionary<int, List<(int By, string Why)>>
    {
        public HashSet<int> Over { get; } = new();
    }

    private static DecisionIndex ReplacedIndex(IEnumerable<StewardDecision> all)
    {
        var map = new DecisionIndex();
        foreach (var g in all.SelectMany(d => d.Replaces.Select(r => (r.Number, By: d.Number, r.Why))).GroupBy(x => x.Number))
            map[g.Key] = g.Select(x => (x.By, x.Why)).OrderBy(x => x.By).ToList();
        return map;
    }

    /// <summary>The index with the slices that are over.</summary>
    private async Task<DecisionIndex> IndexAsync(string projectKey, IEnumerable<StewardDecision> all, CancellationToken ct)
    {
        var map = ReplacedIndex(all);
        foreach (var u in await _db.StewardUnits.AsNoTracking()
                     .Where(u => u.ProjectKey == projectKey && (u.ReleasedAt != null || u.AbandonedAt != null))
                     .Select(u => u.Unit).ToListAsync(ct))
            map.Over.Add(u);
        return map;
    }

    private static Status StatusOf(StewardDecision d, DecisionIndex replaced) =>
        d.WithdrawnAt is not null ? new(Withdrawn, Array.Empty<(int, string)>())
        : replaced.TryGetValue(d.Number, out var by) ? new(Replaced, by)
        // The code is the decision now: a later slice that changes it simply decides again.
        : !d.Convention && (d.RetiredAt is not null || d.PlanUnit is { } u && replaced.Over.Contains(u))
            ? new(Retired, Array.Empty<(int, string)>())
        : d.Provisional ? new(Provisional, Array.Empty<(int, string)>())
        : new(InForce, Array.Empty<(int, string)>());

    private Task<List<StewardDecision>> RegisterAsync(string projectKey, bool track, CancellationToken ct)
    {
        var q = _db.StewardDecisions.Include(d => d.Replaces).Where(d => d.ProjectKey == projectKey);
        return (track ? q : q.AsNoTracking()).OrderBy(d => d.Number).ToListAsync(ct);
    }

    /// <summary>
    /// The next free number: after the highest in the register, and after every number a slice's spec
    /// has already given a decision - an open one is the number it keeps when
    /// it is settled, so nothing else may take it in the meantime.
    /// </summary>
    private async Task<int> NextNumberAsync(string projectKey, CancellationToken ct)
    {
        var inRegister = await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey).Select(d => (int?)d.Number).MaxAsync(ct) ?? 0;
        var inSpecs = (await _git.PlanAsync(projectKey, ct))
            .SelectMany(u => UnitDecisions.Open(u.Text).Select(d => d.Number).Concat(DecisionText.Decided(u.Text).Select(d => d.Number ?? 0)))
            .DefaultIfEmpty(0).Max();
        // Tracked rows not yet saved count too: one call can add several.
        var pending = _db.ChangeTracker.Entries<StewardDecision>().Where(e => e.Entity.ProjectKey == projectKey)
            .Select(e => e.Entity.Number).DefaultIfEmpty(0).Max();
        return Math.Max(Math.Max(inRegister, inSpecs), pending) + 1;
    }

    /// <summary>
    /// What a question was about: the slice it named, else its ticket's slice, else a convention
    /// when it said so. A question asked before slices were the top level may be about none.
    /// </summary>
    private async Task<(int? Unit, string? Outcomes)> AboutAsync(StewardQuestion q, CancellationToken ct)
    {
        if (q.Convention) return (null, null);
        if (q.PlanUnit is { } u) return (u, null);
        if (q.TicketId is { } id && await _db.Tickets.AsNoTracking().Where(t => t.Id == id && t.ProjectKey == q.ProjectKey)
                .Select(t => new { t.PlanUnit, t.PlanOutcomes }).FirstOrDefaultAsync(ct) is { PlanUnit: not null } t)
            return (t.PlanUnit, string.IsNullOrWhiteSpace(t.PlanOutcomes) ? null : t.PlanOutcomes);
        return (null, null);
    }

    private static StewardDecision FromQuestion(StewardQuestion q, int number, (int? Unit, string? Outcomes) about, string? rule = null) => new()
    {
        ProjectKey = q.ProjectKey, Number = number, Rule = rule ?? q.Answer ?? "", Source = StewardDecisionSource.Question,
        QuestionKey = q.Key, Question = q.Question, OptionsJson = q.OptionsJson,
        Convention = about.Unit is null && q.Convention,
        PlanUnit = about.Unit, Outcomes = about.Outcomes,
        MadeAt = q.AnsweredAt ?? q.AskedAt, DeliveredAt = DateTime.UtcNow,
    };

    // ---- writing a decision ----------------------------------------------------------------

    /// <summary>The owner records a decision directly, and with it what it replaces. Never a steward's: no tool reaches this.</summary>
    public async Task<StewardReply> OwnerRecordsDecisionAsync(string projectKey, StewardNewDecision n, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var rule = Regex.Replace(n.Rule?.Trim() ?? "", @"\s+", " ");
        if (rule.Length == 0) return Refuse("bad-request", "Say what is decided, as what is now true.");
        if (rule.Length > 2000) return Refuse("bad-request", "A decision is a sentence or two. Put the reasoning in the plan.");
        if (n.Outcomes is { Count: > 0 } && n.Unit is null) return Refuse("bad-request", "Outcomes belong to a unit: name the unit too.");
        if (n.ProvisionalUnit is not null && !n.Provisional) return Refuse("bad-request", "Only a provisional decision lasts until a unit.");
        if (n.Convention && n.Unit is not null) return Refuse("bad-request", "A convention is every slice's: it names no unit.");
        // Every decision is a slice's or a convention (docs/slice-branches.md, section 4).
        if (n.Unit is null && !n.Convention)
            return Refuse("bad-request", "Say which slice this decides for, or that it is a convention every slice keeps.");

        string? outcomes = null;
        if (n.Unit is { } unit)
        {
            var u = (await _git.PlanAsync(projectKey, ct)).FirstOrDefault(x => x.Number == unit);
            if (u is null) return Refuse("bad-request", $"There is no slice numbered {unit}.");
            var picked = (n.Outcomes ?? Array.Empty<string>()).Select(o => o.Trim().ToUpperInvariant()).Where(o => o.Length > 0).Distinct().ToList();
            if (picked.FirstOrDefault(o => !u.Outcomes.Contains(o, StringComparer.OrdinalIgnoreCase)) is { } missing)
                return Refuse("bad-request", $"Slice {unit} has no outcome {missing}.");
            outcomes = picked.Count == 0 ? null : string.Join(", ", picked);
            if (await _db.StewardUnits.AsNoTracking().AnyAsync(x => x.ProjectKey == projectKey
                    && x.Unit == unit && (x.ReleasedAt != null || x.AbandonedAt != null), ct))
                return Refuse("bad-request", $"Slice {unit} is over: what it decided retired with it. Record this for the slice that changes it, or as a convention.");
        }

        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = await IndexAsync(projectKey, all, ct);
        var replaces = new List<StewardDecisionReplace>();
        foreach (var r in n.Replaces ?? Array.Empty<StewardReplacing>())
        {
            var old = all.FirstOrDefault(d => d.Number == r.Number);
            if (old is null) return Refuse("bad-request", $"There is no decision #{r.Number}.");
            var st = StatusOf(old, replaced);
            if (st.Name == Withdrawn) return Refuse("bad-request", $"#{r.Number} is withdrawn: there is nothing in force to replace.");
            if (st.Name == Retired) return Refuse("bad-request", $"#{r.Number} retired with its slice: the code is that decision now, and this one simply stands.");
            if (st.Name == Replaced) return Refuse("bad-request", $"#{r.Number} is already replaced by #{st.ReplacedBy[0].By}. Replace that one.");
            if (string.IsNullOrWhiteSpace(r.Why)) return Refuse("bad-request", $"Say in a line why #{r.Number} no longer holds.");
            if (replaces.All(x => x.Number != r.Number)) replaces.Add(new StewardDecisionReplace { Number = r.Number, Why = r.Why.Trim() });
        }

        var d = new StewardDecision
        {
            ProjectKey = projectKey, Number = await NextNumberAsync(projectKey, ct), Rule = rule,
            Title = string.IsNullOrWhiteSpace(n.Title) ? null : n.Title.Trim(), Source = StewardDecisionSource.Owner,
            PlanUnit = n.Unit, Outcomes = outcomes, Convention = n.Convention,
            Provisional = n.Provisional, ProvisionalUnit = n.ProvisionalUnit, Replaces = replaces,
        };
        _db.StewardDecisions.Add(d);
        await StewardDue.MarkAsync(_db, projectKey, replaces.Count > 0
            ? $"the owner recorded #{d.Number}, replacing {string.Join(", ", replaces.Select(r => $"#{r.Number}"))}"
            : $"the owner recorded #{d.Number}");
        await _db.SaveChangesAsync(ct);
        var exported = await ExportDecisionsAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("recorded", exported, new { number = d.Number });
    }

    /// <summary>The owner withdraws a decision, with nothing in its place.</summary>
    public async Task<StewardReply> OwnerWithdrawsDecisionAsync(string projectKey, int number, string? because, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        if (string.IsNullOrWhiteSpace(because)) return Refuse("bad-request", "Say in a line why it no longer holds.");
        var all = await RegisterAsync(projectKey, track: true, ct);
        var d = all.FirstOrDefault(x => x.Number == number);
        if (d is null) return Refuse("bad-request", $"There is no decision #{number}.");
        var st = StatusOf(d, await IndexAsync(projectKey, all, ct));
        if (st.Gone) return Refuse("bad-request", $"#{number} is {st.Name} already.");
        (d.WithdrawnAt, d.WithdrawnBecause, d.ChangedAt, d.DeliveredAt) = (DateTime.UtcNow, because.Trim(), DateTime.UtcNow, null);
        await StewardDue.MarkAsync(_db, projectKey, $"the owner withdrew #{number}");
        await _db.SaveChangesAsync(ct);
        var exported = await ExportDecisionsAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("withdrawn", exported);
    }

    // ---- reading it --------------------------------------------------------------------

    private static object Shape(StewardDecision d, Status st, Dictionary<int, string> titles) => new
    {
        number = d.Number,
        rule = d.Rule,
        title = d.Title,
        appliesTo = d.Convention ? "convention" : d.PlanUnit is not { } u ? "project"
            : $"{(titles.TryGetValue(u, out var t) ? t : $"slice {u}")}{(d.Outcomes is { Length: > 0 } o ? $": {o}" : "")}",
        unit = d.PlanUnit,
        outcomes = d.Outcomes,
        convention = d.Convention,
        status = st.Name,
        provisionalUntil = !d.Provisional ? null : d.ProvisionalUnit is { } pu ? $"slice {pu} is built" : "the owner says",
        replaces = d.Replaces.Count == 0 ? null : d.Replaces.OrderBy(r => r.Number).Select(r => new { number = r.Number, why = r.Why }),
        replacedBy = st.ReplacedBy.Count == 0 ? null : st.ReplacedBy.Select(r => new { number = r.By, why = r.Why }),
        withdrawn = d.WithdrawnAt is null ? null : new { on = d.WithdrawnAt.Value.ToLocalTime().ToString("yyyy-MM-dd"), because = d.WithdrawnBecause },
        on = d.MadeAt.ToLocalTime().ToString("yyyy-MM-dd"),
        cameFrom = d.Source switch
        {
            StewardDecisionSource.Question => "an answer",
            StewardDecisionSource.Slice => "a unit's decisions",
            StewardDecisionSource.Plan => "the plan",
            StewardDecisionSource.Owner => "the owner, directly",
            _ => "the owner's word",
        },
        key = d.QuestionKey,
        question = d.Question,
        offered = OfferedOf(d),
        where = d.SourceRef,
    };

    /// <summary>
    /// An answer is the option picked, in words. What it was picked over is half of what it decides,
    /// so every option offered comes back with it: the one picked, the steward's own pick, or neither.
    /// </summary>
    private static List<Offered>? OfferedOf(StewardDecision d)
    {
        if (d.Source != StewardDecisionSource.Question || string.IsNullOrEmpty(d.OptionsJson)) return null;
        var offered = JsonSerializer.Deserialize<List<BlockOption>>(d.OptionsJson, Json) ?? new();
        var picked = offered.FirstOrDefault(o => d.Rule.StartsWith(o.Label.Trim(), StringComparison.OrdinalIgnoreCase));
        var options = offered.Select(o => new Offered(o.Label, o.Detail, ReferenceEquals(o, picked), o.Recommended)).ToList();
        return options.Count > 0 ? options : null;
    }

    private async Task<Dictionary<int, string>> UnitTitlesAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var titles = new Dictionary<int, string>();
        if (!cfg.Steward.Plan.Configured) return titles;
        foreach (var u in await _git.PlanAsync(cfg.Name, ct))
        {
            // A spec's heading often names its own number ("Unit 3: the day"); say it once.
            var title = TicketGitService.UnitTitle(u);
            titles[u.Number] = Regex.IsMatch(title, $@"^(?:unit|slice)\s+0*{u.Number}\b", RegexOptions.IgnoreCase)
                ? $"slice {u.Number}{Regex.Replace(title, @"^\S+\s+\d+", "")}" : $"slice {u.Number}, {title}";
        }
        return titles;
    }

    /// <summary>
    /// What is decided. By default what is in force (provisional decisions included, and marked);
    /// with <paramref name="history"/>, replaced, withdrawn and retired decisions too, each with what
    /// replaced it. With a slice, the slice's own, its parent's if it is built on one, and the
    /// conventions, nothing else; with an outcome as well, only those of the slice's that apply to it. A query looks in
    /// the rule, the question and its options.
    /// </summary>
    public async Task<StewardReply> DecisionsAsync(string projectKey, string? query, int? unit = null, string? outcome = null,
        bool history = false, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        if (outcome is not null && unit is null) return Refuse("bad-request", "An outcome is one unit's: name the unit too.");

        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = await IndexAsync(projectKey, all, ct);
        var titles = await UnitTitlesAsync(cfg!, ct);
        var o = outcome?.Trim();
        // A slice built on another sees its parent's decisions too: it is cut from that work.
        var parent = unit is not null
            ? await _db.StewardUnits.AsNoTracking().Where(u => u.ProjectKey == projectKey && u.Unit == unit)
                .Select(u => u.ParentUnit).FirstOrDefaultAsync(ct)
            : null;
        var rows = all.Select(d => (d, st: StatusOf(d, replaced)))
            .Where(x => history || x.st.Name is InForce or Provisional)
            .Where(x => unit is null || x.d.PlanUnit is null
                || (x.d.PlanUnit == parent)
                || (x.d.PlanUnit == unit
                    && (o is null || x.d.Outcomes is null || x.d.Outcomes.Split(',').Any(y => y.Trim().Equals(o, StringComparison.OrdinalIgnoreCase)))))
            .ToList();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = rows.Where(x => $"#{x.d.Number} {x.d.Title} {x.d.Rule} {x.d.QuestionKey} {x.d.Question} {string.Join(" ", (OfferedOf(x.d) ?? new()).Select(y => $"{y.Label} {y.Detail}"))}"
                .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return new StewardReply("decisions", null, new
        {
            // The number the next decision a draft writes under "To decide" should take.
            next = await NextNumberAsync(projectKey, ct),
            count = rows.Count,
            decisions = rows.Select(x => Shape(x.d, x.st, titles)),
        });
    }

    /// <summary>The register as the Steward page draws it: every decision with its status.</summary>
    public async Task<StewardReply> RegisterViewAsync(string projectKey, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = await IndexAsync(projectKey, all, ct);
        var titles = await UnitTitlesAsync(cfg, ct);
        var built = await BuiltUnitsAsync(cfg, ct);
        return new StewardReply("register", null, new
        {
            next = await NextNumberAsync(projectKey, ct),
            units = titles.OrderBy(t => t.Key).Select(t => new { unit = t.Key, title = t.Value }),
            decisions = all.Select(d => Shape(d, StatusOf(d, replaced), titles)),
            // Provisional decisions whose unit is built: the owner confirms, replaces or lets each lapse.
            due = all.Where(d => d.Provisional && d.WithdrawnAt is null && !replaced.ContainsKey(d.Number)
                && d.ProvisionalUnit is { } u && built.Contains(u)).Select(d => d.Number),
            followUps = (await _db.StewardFollowUps.AsNoTracking().Where(f => f.ProjectKey == projectKey).OrderBy(f => f.At).ToListAsync(ct))
                .Select(f => new { f.Id, decision = f.Decision, f.Text, f.At, f.CitedBy, f.DroppedAt }),
            instructions = (await _db.StewardInstructions.AsNoTracking().Where(i => i.ProjectKey == projectKey)
                    .OrderByDescending(i => i.ClosedAt == null).ThenByDescending(i => i.At).Take(200).ToListAsync(ct))
                .Select(i => new { i.Id, i.Text, i.Until, i.At, i.ClosedAt, i.ClosedBy, i.ClosedNote }),
        });
    }

    /// <summary>
    /// What is in force, written out in the order it is read: the conventions, each open slice's
    /// decisions, then what is provisional. Nothing retired or replaced is written again, so the
    /// file grows with the work that is open, not with the project's age.
    /// </summary>
    private async Task<string?> RegisterTextAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var all = await RegisterAsync(cfg.Name, track: false, ct);
        if (all.Count == 0) return null;
        var replaced = await IndexAsync(cfg.Name, all, ct);
        var titles = await UnitTitlesAsync(cfg, ct);
        var rows = all.Select(d => (d, st: StatusOf(d, replaced))).ToList();

        string Line(StewardDecision d)
        {
            var head = d.Title is { Length: > 0 } t ? $"**#{d.Number}** {t}. " : $"**#{d.Number}** ";
            var from = d.Source == StewardDecisionSource.Question && d.QuestionKey is not null ? $"; answering `{d.QuestionKey}`: {d.Question}" : "";
            var outs = d.Outcomes is { Length: > 0 } o ? $"; for {o}" : "";
            var reps = d.Replaces.Count == 0 ? "" : $" Replaces {string.Join(", ", d.Replaces.OrderBy(r => r.Number).Select(r => $"#{r.Number} ({r.Why.TrimEnd('.')})"))}.";
            var until = !d.Provisional ? "" : d.ProvisionalUnit is { } pu ? $" Until slice {pu} is built." : " Until the owner says.";
            return $"- {head}{d.Rule.Replace("\n", " ")} ({d.MadeAt.ToLocalTime():yyyy-MM-dd}{outs}{from}){reps}{until}";
        }

        var sb = new StringBuilder("# Decisions\n\n")
            .Append("What this project's owner has decided, from the board's register of decisions. Each has one number for good; ")
            .Append("only what is in force is written here. A slice's decisions retire when it is released - its code is the decision then - "
                  + "and the rules every slice keeps are the conventions"
                  + (cfg.Steward.Conventions.Length > 0 ? $" (`{cfg.Steward.Conventions}`)" : "") + ". ")
            .Append("Written out by the ticket board each time the register changes; edits made here are overwritten.\n");
        var standing = rows.Where(x => x.st.Name == InForce).ToList();
        void Section(string heading, IEnumerable<StewardDecision> ds)
        {
            var list = ds.ToList();
            if (list.Count == 0) return;
            sb.Append($"\n## {heading}\n\n").Append(string.Join("\n", list.Select(Line))).Append('\n');
        }
        Section("In force: conventions", standing.Where(x => x.d.Convention).Select(x => x.d));
        foreach (var g in standing.Where(x => x.d.PlanUnit is not null).GroupBy(x => x.d.PlanUnit!.Value).OrderBy(g => g.Key))
            Section($"In force: {(titles.TryGetValue(g.Key, out var t) ? t : $"slice {g.Key}")}", g.Select(x => x.d));
        Section("Provisional", rows.Where(x => x.st.Name == Provisional).Select(x => x.d));
        return sb.ToString();
    }

    /// <summary>
    /// The register's own changes a run has not been handed: decisions the owner recorded directly,
    /// withdrew, or settled as replacing another. An answer and an instruction reach a run by their
    /// own roads, so they are not news twice.
    /// </summary>
    private Task<List<StewardDecision>> UndeliveredAsync(string projectKey, CancellationToken ct) =>
        _db.StewardDecisions.AsNoTracking().Include(d => d.Replaces)
            .Where(d => d.ProjectKey == projectKey && d.DeliveredAt == null).OrderBy(d => d.Number).ToListAsync(ct);

    private async Task DeliveredAsync(string projectKey, DateTime began, CancellationToken ct)
    {
        foreach (var d in await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey && d.DeliveredAt == null && d.ChangedAt <= began).ToListAsync(ct))
            d.DeliveredAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Write the decision an answer makes into the register, with the touched decisions it
    /// replaces. One that stopped holding while the question waited is left as it is: what
    /// replaced it already says so.
    /// </summary>
    private async Task<StewardDecision?> AnswerDecisionAsync(StewardQuestion q, IReadOnlyList<int> replaces, CancellationToken ct)
    {
        if (await _db.StewardDecisions.AnyAsync(d => d.ProjectKey == q.ProjectKey && d.QuestionKey == q.Key, ct)) return null;
        var d = FromQuestion(q, await NextNumberAsync(q.ProjectKey, ct), await AboutAsync(q, ct));
        if (replaces.Count > 0)
        {
            var all = await RegisterAsync(q.ProjectKey, track: false, ct);
            var map = await IndexAsync(q.ProjectKey, all, ct);
            foreach (var n in replaces)
                if (all.FirstOrDefault(x => x.Number == n) is { } old && StatusOf(old, map).Name is InForce or Provisional)
                    d.Replaces.Add(new StewardDecisionReplace { Number = n, Why = $"The owner's answer to {q.Key}: {Head(q.Question, 200)}" });
            // The answer reaches the next run anyway; what it stopped is news as well.
            if (d.Replaces.Count > 0) d.DeliveredAt = null;
        }
        _db.StewardDecisions.Add(d);
        return d;
    }

    /// <summary>Numbers kept comma separated, as a question keeps the decisions it touches.</summary>
    internal static List<int> Numbers(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();

    /// <summary>What the picked option said it would change: the option the answer starts with, by its label.</summary>
    private static List<int> ChangesOf(StewardQuestion q, string answer)
    {
        if (string.IsNullOrEmpty(q.OptionChangesJson)) return new();
        var map = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(q.OptionChangesJson, Json) ?? new();
        var hit = map.Where(kv => answer.Trim().StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
        return hit.Value ?? new();
    }
}
