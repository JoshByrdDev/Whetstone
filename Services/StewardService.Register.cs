using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>A decision the owner records from the register page, and what it replaces.</summary>
public record StewardNewDecision(string? Rule, string? Title = null, int? Unit = null, IReadOnlyList<string>? Outcomes = null,
    bool Provisional = false, int? ProvisionalUnit = null, IReadOnlyList<StewardReplacing>? Replaces = null);

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
    public const string InForce = "inForce", Replaced = "replaced", Provisional = "provisional", Withdrawn = "withdrawn";

    private sealed record Status(string Name, IReadOnlyList<(int By, string Why)> ReplacedBy);

    private static Dictionary<int, List<(int By, string Why)>> ReplacedIndex(IEnumerable<StewardDecision> all) =>
        all.SelectMany(d => d.Replaces.Select(r => (r.Number, By: d.Number, r.Why)))
            .GroupBy(x => x.Number).ToDictionary(g => g.Key, g => g.Select(x => (x.By, x.Why)).OrderBy(x => x.By).ToList());

    private static Status StatusOf(StewardDecision d, Dictionary<int, List<(int By, string Why)>> replaced) =>
        d.WithdrawnAt is not null ? new(Withdrawn, Array.Empty<(int, string)>())
        : replaced.TryGetValue(d.Number, out var by) ? new(Replaced, by)
        : d.Provisional ? new(Provisional, Array.Empty<(int, string)>())
        : new(InForce, Array.Empty<(int, string)>());

    private Task<List<StewardDecision>> RegisterAsync(string projectKey, bool track, CancellationToken ct)
    {
        var q = _db.StewardDecisions.Include(d => d.Replaces).Where(d => d.ProjectKey == projectKey);
        return (track ? q : q.AsNoTracking()).OrderBy(d => d.Number).ToListAsync(ct);
    }

    /// <summary>
    /// The next free number: after the highest in the register, and after every number a spec of
    /// the plan being worked has already given a decision - an open one is the number it keeps when
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

    /// <summary>The first read or write of a project's register builds it from what the project already had.</summary>
    private async Task EnsureRegisterAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var s = await _db.StewardStates.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectKey == cfg.Name, ct);
        if (s?.RegisterBuiltAt is null) await BuildRegisterAsync(cfg, ct);
    }

    /// <summary>What a question was about: its unit when it asked about one, else its ticket's place in the plan, else the project.</summary>
    private async Task<(string? Plan, int? Unit, string? Outcomes)> AboutAsync(StewardQuestion q, CancellationToken ct)
    {
        if (q.PlanUnit is { } u) return (q.PlanKey, u, null);
        if (q.TicketId is { } id && await _db.Tickets.AsNoTracking().Where(t => t.Id == id && t.ProjectKey == q.ProjectKey)
                .Select(t => new { t.PlanKey, t.PlanUnit, t.PlanOutcomes }).FirstOrDefaultAsync(ct) is { PlanUnit: not null } t)
            return (t.PlanKey ?? "", t.PlanUnit, string.IsNullOrWhiteSpace(t.PlanOutcomes) ? null : t.PlanOutcomes);
        return (null, null, null);
    }

    private static StewardDecision FromQuestion(StewardQuestion q, int number, (string? Plan, int? Unit, string? Outcomes) about, string? rule = null) => new()
    {
        ProjectKey = q.ProjectKey, Number = number, Rule = rule ?? q.Answer ?? "", Source = StewardDecisionSource.Question,
        QuestionKey = q.Key, Question = q.Question, OptionsJson = q.OptionsJson,
        PlanKey = about.Unit is null ? null : about.Plan, PlanUnit = about.Unit, Outcomes = about.Outcomes,
        ImportKey = $"question:{q.Key}", MadeAt = q.AnsweredAt ?? q.AskedAt, DeliveredAt = DateTime.UtcNow,
    };

    // ---- building it from what there was ------------------------------------------------

    /// <summary>
    /// Take every decision the project kept before the register into it. Safe to run again: a
    /// decision already here by what it was read from is left alone.
    /// <para>
    /// A unit's decision keeps its number. One decision kept in two places becomes one entry: a
    /// spec's text wins over the answer or the owner's word that says the same, and a plan row that
    /// cites a unit's decision is that decision. Everything else takes the next number, the plan's
    /// rows first since they are oldest. No override is assumed: what may disagree is written down
    /// for the owner as pairs, and each pair waits until the owner settles it.
    /// </para>
    /// </summary>
    private async Task<object> BuildRegisterAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var projectKey = cfg.Name;
        var now = DateTime.UtcNow;
        var existing = await RegisterAsync(projectKey, track: false, ct);
        var importKeys = existing.Where(d => d.ImportKey is not null).Select(d => d.ImportKey!).ToHashSet();
        var taken = existing.Select(d => d.Number).ToHashSet();
        var asked = existing.Where(d => d.QuestionKey is not null).Select(d => d.QuestionKey!).ToHashSet();
        var added = new List<StewardDecision>();
        var merged = new List<string>();
        var passedOver = new List<string>();
        var renumbered = new List<(StewardDecision D, string Was)>();

        var questions = await _db.StewardQuestions.AsNoTracking()
            .Where(q => q.ProjectKey == projectKey && q.Answer != null).OrderBy(q => q.AnsweredAt).ToListAsync(ct);
        var byKey = questions.ToDictionary(q => q.Key);

        // 1. The specs' "Decided" sections. The plan being worked keeps its numbers; a past plan's
        //    number that is taken already is given a new one, and the report says so.
        var plans = new[] { cfg.Steward.Plan }.Where(p => p.Configured).Concat(cfg.Steward.PastPlans.Where(p => p.Configured)).ToList();
        var sliceNumbers = new Dictionary<(string Plan, int N), int>();
        var deferred = new List<StewardDecision>();   // slice decisions that need a new number
        foreach (var plan in plans)
        {
            var units = ReferenceEquals(plan, cfg.Steward.Plan) ? await _git.PlanAsync(projectKey, ct) : await _git.PlanOfAsync(projectKey, plan, ct);
            var agreedAt = await _db.StewardUnits.AsNoTracking().Where(u => u.ProjectKey == projectKey && u.Plan == plan.Key)
                .ToDictionaryAsync(u => u.Unit, u => u.AgreedAt, ct);
            foreach (var u in units)
            foreach (var item in DecisionText.Decided(u.Text))
            {
                var keyed = item.CitedKeys.FirstOrDefault(byKey.ContainsKey);
                var import = item.Number is { } n0 ? $"slice:{plan.Key}:{n0}" : keyed is null ? null : $"slice:{plan.Key}:{u.Number}:q:{keyed}";
                if (import is null)
                {
                    passedOver.Add($"{u.File}: a paragraph under \"Decided\" that numbers no decision and cites no question");
                    continue;
                }
                foreach (var k in item.CitedKeys.Where(byKey.ContainsKey))
                    if (asked.Add(k)) merged.Add($"the answer to {k} is the decision {u.File} keeps{(item.Number is { } c ? $" as #{c}" : "")}");
                if (item.Number is { } kept) sliceNumbers[(plan.Key, kept)] = kept;
                if (!importKeys.Add(import)) continue;
                var d = new StewardDecision
                {
                    ProjectKey = projectKey, Rule = item.Rule, Title = item.Title, Source = StewardDecisionSource.Slice,
                    PlanKey = plan.Key, PlanUnit = u.Number, SourceRef = u.File, ImportKey = import,
                    QuestionKey = keyed, Question = keyed is null ? null : byKey[keyed].Question,
                    MadeAt = item.On ?? (agreedAt.TryGetValue(u.Number, out var a) ? a : null) ?? now, DeliveredAt = now,
                };
                if (item.Number is { } number && taken.Add(number)) { d.Number = number; added.Add(d); }
                else
                {
                    if (item.Number is { } clash) renumbered.Add((d, $"#{clash} of {plan.DisplayName} ({u.File}) was taken already"));
                    deferred.Add(d);
                }
            }
        }

        // A question that asked for a unit's decisions is those decisions: they are where it is kept.
        foreach (var q in questions.Where(q => q.PlanUnit is not null && !string.IsNullOrEmpty(q.Decisions)))
        {
            var nums = q.Decisions!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var i) ? i : 0).ToHashSet();
            var mine = added.Where(d => d.PlanKey == (q.PlanKey ?? "") && d.PlanUnit == q.PlanUnit && nums.Contains(d.Number)).ToList();
            var already = existing.Any(d => d.PlanUnit == q.PlanUnit && nums.Contains(d.Number) && d.Source == StewardDecisionSource.Slice);
            if (mine.Count == 0 && !already) continue;
            foreach (var d in mine) (d.QuestionKey, d.Question) = (d.QuestionKey ?? q.Key, d.Question ?? q.Question);
            if (asked.Add(q.Key)) merged.Add($"the answer to {q.Key} is unit {q.PlanUnit}'s {string.Join(", ", nums.Order().Select(n => $"#{n}"))}");
        }

        // 2. The rest wait for numbers, which go out in this order: the plan's rows (the oldest),
        //    then everything else by the day it was made.
        var plansRows = new List<StewardDecision>();
        foreach (var plan in plans.Where(p => p.Master.Length > 0))
        {
            var text = await _git.FileAtBaseAsync(projectKey, plan.Master, ct);
            if (text is null) continue;
            var rows = DecisionText.PlanRows(text);
            if (rows is null) { passedOver.Add($"{plan.Master} has no decisions section"); continue; }
            foreach (var r in rows)
            {
                var cited = r.Cites.Where(n => sliceNumbers.ContainsKey((plan.Key, n)) || existing.Any(d => d.Number == n && d.Source == StewardDecisionSource.Slice)).ToList();
                if (cited.Count > 0)
                {
                    merged.Add($"a row of {plan.Master} is {string.Join(" and ", cited.Select(n => $"#{n}"))}, kept in its spec: \"{Head(r.Text, 80)}\"");
                    continue;
                }
                var import = $"plan:{plan.Key}:{Hash(r.Text)}";
                if (!importKeys.Add(import)) continue;
                plansRows.Add(new StewardDecision
                {
                    ProjectKey = projectKey, Rule = r.Text, Source = StewardDecisionSource.Plan, SourceRef = plan.Master,
                    ImportKey = import, MadeAt = r.On ?? now, DeliveredAt = now,
                });
            }
        }

        var later = new List<StewardDecision>(deferred);
        foreach (var q in questions)
        {
            if (asked.Contains(q.Key) || !importKeys.Add($"question:{q.Key}")) continue;
            // What a unit's settling wrote as its answer is a summary of decisions kept in the spec.
            if (Regex.IsMatch(q.Answer!, @"^Unit \d+: (?:agreed|decided)\b")) { merged.Add($"the answer to {q.Key} is a unit's settled decisions"); continue; }
            later.Add(FromQuestion(q, 0, await AboutAsync(q, ct)));
            asked.Add(q.Key);
        }

        var said = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.OwnerSaid).OrderBy(e => e.At).ToListAsync(ct);
        var unitsDirs = plans.Select(p => p.UnitsDir).Where(d => d.Length > 0).ToList();
        foreach (var e in said)
        {
            var import = $"said:{e.Id}";
            if (importKeys.Contains(import)) continue;
            var t = e.Text.Trim();
            string? why = Regex.IsMatch(t, @"^Ticket \d+: '[^']+' is cleared") ? "a flag cleared on one ticket, kept as a clearance"
                : Regex.IsMatch(t, @"^Unit \d+: (?:agreed|decided)\b") ? "a unit's settled decisions, kept in its spec"
                : Regex.IsMatch(t, @"\bagreed\b", RegexOptions.IgnoreCase) && unitsDirs.Any(d => t.Contains(d, StringComparison.OrdinalIgnoreCase))
                    ? "a unit agreed, whose decisions are kept in its spec"
                : NamedKey(t, byKey.Keys) is { } k ? $"the answer to {k}, said again"
                : null;
            importKeys.Add(import);
            if (why is not null) { merged.Add($"the owner's word of {e.At.ToLocalTime():yyyy-MM-dd} is {why}"); continue; }
            later.Add(new StewardDecision
            {
                ProjectKey = projectKey, Rule = t, Source = StewardDecisionSource.OwnerSaid, SourceRef = $"record line {e.Id}",
                ImportKey = import, MadeAt = e.At, DeliveredAt = now,
            });
        }

        var next = Math.Max(taken.DefaultIfEmpty(0).Max(), await NextNumberAsync(projectKey, ct) - 1) + 1;
        foreach (var d in plansRows.Concat(later.OrderBy(d => d.MadeAt)))
        {
            while (taken.Contains(next)) next++;
            d.Number = next;
            taken.Add(next);
            added.Add(d);
        }

        _db.StewardDecisions.AddRange(added);
        var state = await StewardDue.StateAsync(_db, projectKey);
        state.RegisterBuiltAt ??= now;
        await _db.SaveChangesAsync(ct);

        // 3. What may disagree, for the owner. Only pairs with something new in them.
        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = ReplacedIndex(all);
        var standing = all.Where(d => StatusOf(d, replaced).Name is InForce or Provisional).ToList();
        var fresh = added.Select(d => d.Number).ToHashSet();
        var pairs = DecisionText.Conflicts(
            standing.Select(d => new DecisionWords(d.Number, $"{d.Title} {d.Rule}", d.MadeAt, d.PlanUnit is null ? "" : $"{d.PlanKey}:{d.PlanUnit}")).ToList(),
            fresh);
        var known = (await _db.StewardDecisionConflicts.Where(c => c.ProjectKey == projectKey).Select(c => new { c.Older, c.Newer }).ToListAsync(ct))
            .Select(c => (c.Older, c.Newer)).ToHashSet();
        var conflicts = pairs.Where(p => known.Add((p.Older, p.Newer))).ToList();
        _db.StewardDecisionConflicts.AddRange(conflicts.Select(p => new StewardDecisionConflict
        {
            ProjectKey = projectKey, Older = p.Older, Newer = p.Newer, Why = p.Why,
        }));
        await _db.SaveChangesAsync(ct);

        return new
        {
            added = added.Count,
            bySource = added.GroupBy(d => d.Source.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            highestKept = added.Where(d => d.Source == StewardDecisionSource.Slice).Select(d => (int?)d.Number).Max(),
            merged,
            renumbered = renumbered.Select(r => $"{r.Was}, so it is #{r.D.Number} here"),
            passedOver,
            mayConflict = conflicts.Select(p => new { p.Older, p.Newer, p.Why }),
        };
    }

    /// <summary>A question's key the owner's words name: "key: ...", "answer to **key**", or the key in bold or code.</summary>
    private static string? NamedKey(string text, IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            var e = Regex.Escape(k);
            if (Regex.IsMatch(text, $@"^{e}\s*:|\*\*{e}\*\*|`{e}`", RegexOptions.IgnoreCase)) return k;
        }
        return null;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim())))[..16].ToLowerInvariant();

    /// <summary>The owner builds the register, or takes in what has arrived since it was built. Its report says what it merged and what may disagree.</summary>
    public async Task<StewardReply> OwnerBuildsRegisterAsync(string projectKey, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var report = await BuildRegisterAsync(cfg, ct);
        var exported = await ExportDecisionsAsync(cfg, ct);
        return new StewardReply("built", exported, report);
    }

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
        await EnsureRegisterAsync(cfg, ct);

        var plan = cfg.Steward.Plan;
        string? outcomes = null;
        if (n.Unit is { } unit)
        {
            var u = (await _git.PlanAsync(projectKey, ct)).FirstOrDefault(x => x.Number == unit);
            if (u is null) return Refuse("bad-request", $"The plan has no unit numbered {unit}.");
            var picked = (n.Outcomes ?? Array.Empty<string>()).Select(o => o.Trim().ToUpperInvariant()).Where(o => o.Length > 0).Distinct().ToList();
            if (picked.FirstOrDefault(o => !u.Outcomes.Contains(o, StringComparer.OrdinalIgnoreCase)) is { } missing)
                return Refuse("bad-request", $"Unit {unit} has no outcome {missing}.");
            outcomes = picked.Count == 0 ? null : string.Join(", ", picked);
        }

        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = ReplacedIndex(all);
        var replaces = new List<StewardDecisionReplace>();
        foreach (var r in n.Replaces ?? Array.Empty<StewardReplacing>())
        {
            var old = all.FirstOrDefault(d => d.Number == r.Number);
            if (old is null) return Refuse("bad-request", $"There is no decision #{r.Number}.");
            var st = StatusOf(old, replaced);
            if (st.Name == Withdrawn) return Refuse("bad-request", $"#{r.Number} is withdrawn: there is nothing in force to replace.");
            if (st.Name == Replaced) return Refuse("bad-request", $"#{r.Number} is already replaced by #{st.ReplacedBy[0].By}. Replace that one.");
            if (string.IsNullOrWhiteSpace(r.Why)) return Refuse("bad-request", $"Say in a line why #{r.Number} no longer holds.");
            if (replaces.All(x => x.Number != r.Number)) replaces.Add(new StewardDecisionReplace { Number = r.Number, Why = r.Why.Trim() });
        }

        var d = new StewardDecision
        {
            ProjectKey = projectKey, Number = await NextNumberAsync(projectKey, ct), Rule = rule,
            Title = string.IsNullOrWhiteSpace(n.Title) ? null : n.Title.Trim(), Source = StewardDecisionSource.Owner,
            PlanKey = n.Unit is null ? null : plan.Key, PlanUnit = n.Unit, Outcomes = outcomes,
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
        await EnsureRegisterAsync(cfg, ct);
        var all = await RegisterAsync(projectKey, track: true, ct);
        var d = all.FirstOrDefault(x => x.Number == number);
        if (d is null) return Refuse("bad-request", $"There is no decision #{number}.");
        var st = StatusOf(d, ReplacedIndex(all));
        if (st.Name is Withdrawn or Replaced) return Refuse("bad-request", $"#{number} is {st.Name} already.");
        (d.WithdrawnAt, d.WithdrawnBecause, d.ChangedAt, d.DeliveredAt) = (DateTime.UtcNow, because.Trim(), DateTime.UtcNow, null);
        await StewardDue.MarkAsync(_db, projectKey, $"the owner withdrew #{number}");
        await _db.SaveChangesAsync(ct);
        var exported = await ExportDecisionsAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("withdrawn", exported);
    }

    /// <summary>
    /// The owner settles a pair the import thought may disagree: the newer replaces the older, or
    /// both stand. Replacing writes the replacing onto the newer decision, which is where the
    /// register keeps it.
    /// </summary>
    public async Task<StewardReply> OwnerSettlesConflictAsync(string projectKey, int id, bool replaces, string? why, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var c = await _db.StewardDecisionConflicts.FirstOrDefaultAsync(x => x.Id == id && x.ProjectKey == projectKey, ct);
        if (c is null) return Refuse("bad-request", $"No pair {id} is waiting.");
        if (c.Settled is not null) return Refuse("bad-request", $"That pair was settled on {c.SettledAt:yyyy-MM-dd}.");
        if (replaces)
        {
            var all = await RegisterAsync(projectKey, track: true, ct);
            var map = ReplacedIndex(all);
            var older = all.FirstOrDefault(d => d.Number == c.Older);
            var newer = all.FirstOrDefault(d => d.Number == c.Newer);
            if (older is null || newer is null) return Refuse("bad-request", "One of the pair is no longer in the register.");
            if (StatusOf(older, map).Name is Replaced or Withdrawn) return Refuse("bad-request", $"#{older.Number} no longer holds already.");
            if (StatusOf(newer, map).Name is Replaced or Withdrawn) return Refuse("bad-request", $"#{newer.Number} no longer holds itself, so it replaces nothing.");
            newer.Replaces.Add(new StewardDecisionReplace { Number = older.Number, Why = string.IsNullOrWhiteSpace(why) ? "Settled by the owner from the import's report." : why.Trim() });
            (newer.ChangedAt, newer.DeliveredAt) = (DateTime.UtcNow, null);
            await StewardDue.MarkAsync(_db, projectKey, $"the owner said #{newer.Number} replaces #{older.Number}");
        }
        (c.Settled, c.SettledAt) = (replaces ? "replaced" : "both-stand", DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        var exported = replaces ? await ExportDecisionsAsync(cfg, ct) : null;
        if (replaces) _heartbeat?.Wake();
        return new StewardReply("settled", exported);
    }

    // ---- reading it --------------------------------------------------------------------

    private static object Shape(StewardDecision d, Status st, Dictionary<(string?, int), string> titles) => new
    {
        number = d.Number,
        rule = d.Rule,
        title = d.Title,
        appliesTo = d.PlanUnit is not { } u ? "project"
            : $"{(titles.TryGetValue((d.PlanKey, u), out var t) ? t : $"unit {u}")}{(d.Outcomes is { Length: > 0 } o ? $": {o}" : "")}",
        plan = d.PlanKey,
        unit = d.PlanUnit,
        outcomes = d.Outcomes,
        status = st.Name,
        provisionalUntil = !d.Provisional ? null : d.ProvisionalUnit is { } pu ? $"unit {pu} is built" : "the owner says",
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

    private async Task<Dictionary<(string?, int), string>> UnitTitlesAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var titles = new Dictionary<(string?, int), string>();
        if (!cfg.Steward.Plan.Configured) return titles;
        foreach (var u in await _git.PlanAsync(cfg.Name, ct))
        {
            // A spec's heading often names its own number ("Unit 3: the day"); say it once.
            var title = TicketGitService.UnitTitle(u);
            titles[(cfg.Steward.Plan.Key, u.Number)] = Regex.IsMatch(title, $@"^(?:unit|slice)\s+0*{u.Number}\b", RegexOptions.IgnoreCase)
                ? $"unit {u.Number}{Regex.Replace(title, @"^\S+\s+\d+", "")}" : $"unit {u.Number}, {title}";
        }
        return titles;
    }

    /// <summary>
    /// What is decided. By default what is in force (provisional decisions included, and marked);
    /// with <paramref name="history"/>, replaced and withdrawn decisions too, each with what replaced
    /// it. With a unit, that slice's decisions and the project's; with an outcome as well, only those
    /// of the slice's that apply to it. A query looks in the rule, the question and its options.
    /// </summary>
    public async Task<StewardReply> DecisionsAsync(string projectKey, string? query, int? unit = null, string? outcome = null,
        bool history = false, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        if (outcome is not null && unit is null) return Refuse("bad-request", "An outcome is one unit's: name the unit too.");
        await EnsureRegisterAsync(cfg!, ct);

        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = ReplacedIndex(all);
        var titles = await UnitTitlesAsync(cfg!, ct);
        var working = cfg!.Steward.Plan.Key;
        var o = outcome?.Trim();
        var rows = all.Select(d => (d, st: StatusOf(d, replaced)))
            .Where(x => history || x.st.Name is InForce or Provisional)
            .Where(x => unit is null || x.d.PlanUnit is null
                || (x.d.PlanUnit == unit && (x.d.PlanKey ?? "") == working
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

    /// <summary>The register as the Steward page draws it: every decision with its status, and the pairs waiting on the owner.</summary>
    public async Task<StewardReply> RegisterViewAsync(string projectKey, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        await EnsureRegisterAsync(cfg, ct);
        var all = await RegisterAsync(projectKey, track: false, ct);
        var replaced = ReplacedIndex(all);
        var titles = await UnitTitlesAsync(cfg, ct);
        var pairs = await _db.StewardDecisionConflicts.AsNoTracking()
            .Where(c => c.ProjectKey == projectKey && c.Settled == null).OrderBy(c => c.Older).ThenBy(c => c.Newer).ToListAsync(ct);
        return new StewardReply("register", null, new
        {
            next = await NextNumberAsync(projectKey, ct),
            plan = cfg.Steward.Plan.Configured ? cfg.Steward.Plan.Key : null,
            units = titles.Where(t => t.Key.Item1 == cfg.Steward.Plan.Key).OrderBy(t => t.Key.Item2).Select(t => new { unit = t.Key.Item2, title = t.Value }),
            decisions = all.Select(d => Shape(d, StatusOf(d, replaced), titles)),
            mayConflict = pairs.Select(c => new { c.Id, c.Older, c.Newer, c.Why }),
        });
    }

    /// <summary>
    /// The register written out whole, in the order it is read: what is in force for the whole
    /// project, then each slice's, then what is provisional, then history with what replaced each.
    /// </summary>
    private async Task<string?> RegisterTextAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var all = await RegisterAsync(cfg.Name, track: false, ct);
        if (all.Count == 0) return null;
        var replaced = ReplacedIndex(all);
        var titles = await UnitTitlesAsync(cfg, ct);
        var rows = all.Select(d => (d, st: StatusOf(d, replaced))).ToList();

        string Line(StewardDecision d)
        {
            var head = d.Title is { Length: > 0 } t ? $"**#{d.Number}** {t}. " : $"**#{d.Number}** ";
            var from = d.Source == StewardDecisionSource.Question && d.QuestionKey is not null ? $"; answering `{d.QuestionKey}`: {d.Question}" : "";
            var outs = d.Outcomes is { Length: > 0 } o ? $"; for {o}" : "";
            var reps = d.Replaces.Count == 0 ? "" : $" Replaces {string.Join(", ", d.Replaces.OrderBy(r => r.Number).Select(r => $"#{r.Number} ({r.Why.TrimEnd('.')})"))}.";
            var until = !d.Provisional ? "" : d.ProvisionalUnit is { } pu ? $" Until unit {pu} is built." : " Until the owner says.";
            return $"- {head}{d.Rule.Replace("\n", " ")} ({d.MadeAt.ToLocalTime():yyyy-MM-dd}{outs}{from}){reps}{until}";
        }

        var sb = new StringBuilder("# Decisions\n\n")
            .Append("What this project's owner has decided, from the board's register of decisions. Each has one number for good; ")
            .Append("a decision that changes is replaced by a newer one, listed under History with what replaced it. ")
            .Append("Written out by the ticket board each time the register changes; edits made here are overwritten.\n");
        var standing = rows.Where(x => x.st.Name == InForce).ToList();
        void Section(string heading, IEnumerable<StewardDecision> ds)
        {
            var list = ds.ToList();
            if (list.Count == 0) return;
            sb.Append($"\n## {heading}\n\n").Append(string.Join("\n", list.Select(Line))).Append('\n');
        }
        Section("In force: the whole project", standing.Where(x => x.d.PlanUnit is null).Select(x => x.d));
        foreach (var g in standing.Where(x => x.d.PlanUnit is not null).GroupBy(x => (x.d.PlanKey, x.d.PlanUnit!.Value))
                     .OrderBy(g => g.Key.PlanKey == cfg.Steward.Plan.Key ? 1 : 0).ThenBy(g => g.Key.PlanKey).ThenBy(g => g.Key.Value))
        {
            var name = titles.TryGetValue((g.Key.PlanKey, g.Key.Value), out var t) ? t : $"unit {g.Key.Value}";
            var of = g.Key.PlanKey == cfg.Steward.Plan.Key ? "" : $" of {cfg.Steward.PlanByKey(g.Key.PlanKey ?? "")?.DisplayName ?? g.Key.PlanKey}";
            Section($"In force: {name}{of}", g.Select(x => x.d));
        }
        Section("Provisional", rows.Where(x => x.st.Name == Provisional).Select(x => x.d));
        var past = rows.Where(x => x.st.Name is Replaced or Withdrawn).ToList();
        if (past.Count > 0)
        {
            sb.Append("\n## History\n\n");
            sb.Append(string.Join("\n", past.Select(x =>
                $"{Line(x.d).Replace($"**#{x.d.Number}**", $"~~#{x.d.Number}~~")} "
                + (x.st.Name == Withdrawn
                    ? $"Withdrawn {x.d.WithdrawnAt!.Value.ToLocalTime():yyyy-MM-dd}: {x.d.WithdrawnBecause}"
                    : $"Replaced by {string.Join(", ", x.st.ReplacedBy.Select(r => $"#{r.By}"))}."))));
            sb.Append('\n');
        }
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
    private async Task AnswerDecisionAsync(StewardQuestion q, IReadOnlyList<int> replaces, CancellationToken ct)
    {
        if (await _db.StewardDecisions.AnyAsync(d => d.ProjectKey == q.ProjectKey && d.QuestionKey == q.Key, ct)) return;
        var d = FromQuestion(q, await NextNumberAsync(q.ProjectKey, ct), await AboutAsync(q, ct));
        if (replaces.Count > 0)
        {
            var all = await RegisterAsync(q.ProjectKey, track: false, ct);
            var map = ReplacedIndex(all);
            foreach (var n in replaces)
                if (all.FirstOrDefault(x => x.Number == n) is { } old && StatusOf(old, map).Name is InForce or Provisional)
                    d.Replaces.Add(new StewardDecisionReplace { Number = n, Why = $"The owner's answer to {q.Key}: {Head(q.Question, 200)}" });
            // The answer reaches the next run anyway; what it stopped is news as well.
            if (d.Replaces.Count > 0) d.DeliveredAt = null;
        }
        _db.StewardDecisions.Add(d);
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
