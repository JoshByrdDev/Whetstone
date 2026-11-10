using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>What the owner switches a project to slice branches with: the base, and the conventions as they edited them.</summary>
public record StewardSwitch(string? Base, string? Conventions, string? Text, IReadOnlyList<int>? Keep);

/// <summary>
/// The migration (docs/slice-branches.md, "Moving a project that already exists", piece 8): one owner
/// action from the Steward page, with no tool behind it, that moves a project on the old shape - a
/// plan, an integration branch as its base, a register that only grows - to slices with a branch
/// each. It comes after the owner has finished the slices in flight and merged the integration
/// branch into the base by hand; it refuses until both are true.
/// <list type="bullet">
/// <item>The hub moves onto the base, and the conventions file the owner edited is committed there.</item>
/// <item>The register takes in what the project kept, one last time. Nothing imports again.</item>
/// <item>Every agreed slice is recorded as released - its work is in the base - so its decisions retire.
/// So does every slice of a past plan that a decision names.</item>
/// <item>The plan being worked becomes the slices' folder: its rows lose their plan key, so a draft
/// or an idea in it goes on as a slice of the new shape with the number it has.</item>
/// <item>The whole project's decisions the owner keeps become conventions; the rest retire.</item>
/// </list>
/// The project's settings are written by the caller once this answers "switched": where they live
/// is not this service's to know, as with starting a plan.
/// </summary>
public partial class StewardService
{
    private sealed record SwitchPlan(
        List<string> Refusals, string Base, string ConventionsPath,
        List<(string Plan, int Unit, string Title)> Released,
        List<StewardDecision> ProjectWide, string Draft);

    /// <summary>What the switch would do, and why it cannot yet. Builds the register if it never was; changes nothing else.</summary>
    public async Task<StewardReply> SwitchViewAsync(string projectKey, string? baseBranch = null, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var s = await PlanSwitchAsync(cfg, baseBranch, dryRun: true, ct);
        return new StewardReply(s.Refusals.Count == 0 ? "ready" : "not-yet", s.Refusals.Count == 0 ? null : string.Join(" ", s.Refusals), new
        {
            switched = cfg.SliceBranches,
            refusals = s.Refusals,
            from = cfg.BaseBranch,
            @base = s.Base,
            conventions = s.ConventionsPath,
            released = s.Released.Select(r => new { plan = r.Plan.Length > 0 ? r.Plan : null, unit = r.Unit, title = r.Title }),
            projectWide = s.ProjectWide.Select(d => new { number = d.Number, rule = d.Rule, title = d.Title }),
            draft = s.Draft,
        });
    }

    /// <summary>The owner switches the project. No tool reaches this.</summary>
    public async Task<StewardReply> OwnerSwitchesAsync(string projectKey, StewardSwitch body, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        // What there was is taken in first, so what the owner keeps and what retires covers all of it.
        if (!cfg.SliceBranches && cfg.MergeInsteadOfPr) await BuildRegisterAsync(cfg, ct);
        var s = await PlanSwitchAsync(cfg, body.Base, dryRun: true, ct);
        if (s.Refusals.Count > 0) return Refuse("not-yet", string.Join(" ", s.Refusals));

        var path = string.IsNullOrWhiteSpace(body.Conventions) ? s.ConventionsPath : body.Conventions.Replace('\\', '/').Trim().Trim('/');
        if (path.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return Refuse("bad-request", "The conventions file is a path inside the repo, like docs/conventions.md.");
        var keep = (body.Keep ?? Array.Empty<int>()).ToHashSet();
        if (keep.FirstOrDefault(n => s.ProjectWide.All(d => d.Number != n)) is var stray and > 0)
            return Refuse("bad-request", $"#{stray} is not one of the whole project's decisions in force.");
        var text = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text;

        var (sha, no) = await _git.SwitchBaseAsync(projectKey, s.Base, text is null ? null : path, text,
            $"Slice branches: {path}, the rules every slice keeps", dryRun: false, ct);
        if (no is not null) return Refuse("refused", no);

        var now = DateTime.UtcNow;
        var working = cfg.Steward.Plan.Key;
        // The plan being worked becomes the slices' folder: its rows go on under no key.
        if (working.Length > 0)
        {
            foreach (var d in await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey && d.PlanUnit != null
                         && (d.PlanKey == null || d.PlanKey == "") && d.RetiredAt == null).ToListAsync(ct))
                (d.RetiredAt, d.ChangedAt) = (now, now);
            foreach (var u in await _db.StewardUnits.Where(u => u.ProjectKey == projectKey && u.Plan == working).ToListAsync(ct)) u.Plan = "";
            foreach (var o in await _db.StewardOutcomes.Where(o => o.ProjectKey == projectKey && o.Plan == working).ToListAsync(ct)) o.Plan = "";
            foreach (var d in await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey && d.PlanKey == working).ToListAsync(ct)) d.PlanKey = "";
            await _db.SaveChangesAsync(ct);
        }
        foreach (var (plan, unit, _) in s.Released)
        {
            var row = await UnitRowAsync(projectKey, plan == working ? "" : plan, unit, ct);
            row.AgreedAt ??= now;
            row.ReleasedAt ??= now;
            row.ReleasedSha ??= sha;
        }
        foreach (var d in await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey).ToListAsync(ct))
        {
            if (s.ProjectWide.All(x => x.Number != d.Number)) continue;
            if (keep.Contains(d.Number)) (d.Convention, d.ChangedAt, d.DeliveredAt) = (true, now, null);
            else (d.RetiredAt, d.ChangedAt) = (now, now);
        }
        var state = await StewardDue.StateAsync(_db, projectKey);
        state.RegisterBuiltAt ??= now;
        await StewardDue.MarkAsync(_db, projectKey, $"the owner switched {cfg.DisplayName} to slice branches");
        await _db.SaveChangesAsync(ct);
        _heartbeat?.Wake();
        return new StewardReply("switched", null, new
        {
            @base = s.Base, from = cfg.BaseBranch, at = sha, conventions = text is null ? null : path,
            released = s.Released.Count, conventionsKept = keep.Count, retired = s.ProjectWide.Count - keep.Count,
        });
    }

    /// <summary>Once the settings say slice branches: the decisions file and the plan as read, in the new shape.</summary>
    public async Task<string?> AfterSwitchAsync(string projectKey, CancellationToken ct = default)
    {
        if (Config(projectKey) is not { SliceBranchesOn: true } cfg) return null;
        var exported = await ExportDecisionsAsync(cfg, ct);
        await WriteReadableAsync(cfg, ct);
        return exported;
    }

    private async Task<SwitchPlan> PlanSwitchAsync(ProjectConfig cfg, string? baseBranch, bool dryRun, CancellationToken ct)
    {
        var refusals = new List<string>();
        var projectKey = cfg.Name;
        var written = cfg.StewardAsWritten;
        var target = string.IsNullOrWhiteSpace(baseBranch)
            ? written.SyncFrom.Length > 0 ? written.SyncFrom : cfg.BaseBranch.Length > 0 ? cfg.BaseBranch : "main"
            : baseBranch.Trim();
        var conventions = written.Conventions.Length > 0 ? written.Conventions : "docs/conventions.md";
        var none = new SwitchPlan(refusals, target, conventions, new(), new(), "");

        if (cfg.SliceBranches) { refusals.Add($"{cfg.DisplayName} has switched already."); return none; }
        if (!cfg.MergeInsteadOfPr) refusals.Add("Slice branches are for a project the board merges into locally (mergeInsteadOfPr); this one is delivered by pull request.");
        if (cfg.IsTfvc) refusals.Add("A TFVC project keeps its plan: the fleet is not its only writer.");
        if (cfg.IsMultiRepo) refusals.Add("A project of several repos keeps its plan for now.");
        if (refusals.Count > 0) return none;

        if (await RunOpenAsync(projectKey)) refusals.Add("A steward run is open. Switch once it has ended.");
        var open = (await _tickets.ListAsync(projectKey, null))
            .Where(t => t.PlanUnit is not null && t.Phase != TicketPhase.Done).Select(t => t.Id).Order().ToList();
        if (open.Count > 0)
            refusals.Add($"{open.Count} ticket{(open.Count == 1 ? " of a slice is" : "s of slices are")} still open ({string.Join(", ", open.Take(8).Select(i => $"#{i}"))}). Finish the slices in flight on the old base first.");

        // Every agreed slice of the plan being worked has its work in the base by now, or is in flight.
        var released = new List<(string Plan, int Unit, string Title)>();
        var plan = written.Plan;
        if (plan.Configured)
        {
            var units = await _git.PlanOfAsync(projectKey, plan, ct);
            var built = await BuiltUnitsAsync(cfg, ct);
            var rows = await _db.StewardUnits.AsNoTracking().Where(u => u.ProjectKey == projectKey && u.Plan == plan.Key).ToListAsync(ct);
            var unbuilt = units.Where(u => u.Agreed && !built.Contains(u.Number) && rows.All(r => r.Unit != u.Number || r.ReleasedAt is null)).Select(u => u.Number).ToList();
            if (unbuilt.Count > 0)
                refusals.Add($"Slice{(unbuilt.Count == 1 ? "" : "s")} {string.Join(", ", unbuilt)} {(unbuilt.Count == 1 ? "is" : "are")} agreed and not built. Finish {(unbuilt.Count == 1 ? "it" : "them")} on the old base first.");
            released.AddRange(units.Where(u => u.Agreed).Select(u => (plan.Key, u.Number, TicketGitService.UnitTitle(u))));
        }
        // A past plan's slices are long in the base: each one a decision names retires with the rest.
        await EnsureRegisterAsync(cfg, ct);
        var all = await RegisterAsync(projectKey, track: false, ct);
        // (An unkeyed earlier plan's, under a keyed one, is retired by itself instead: its numbers are
        // the ones the slices go on with.)
        foreach (var g in all.Where(d => d.PlanUnit is not null && (d.PlanKey ?? "") != plan.Key && (d.PlanKey is { Length: > 0 } || plan.Key.Length == 0))
                     .Select(d => (Plan: d.PlanKey ?? "", Unit: d.PlanUnit!.Value)).Distinct().OrderBy(x => x.Plan).ThenBy(x => x.Unit))
            released.Add((g.Plan, g.Unit, $"unit {g.Unit} of {written.PlanByKey(g.Plan)?.DisplayName ?? (g.Plan.Length > 0 ? g.Plan : "an earlier plan")}"));

        var map = ReplacedIndex(all);
        var projectWide = all.Where(d => d.PlanUnit is null && !d.Convention && StatusOf(d, map).Name is InForce or Provisional).ToList();

        var (_, no) = await _git.SwitchBaseAsync(projectKey, target, null, null, "", dryRun: true, ct);
        if (no is not null) refusals.Add(no);

        return new SwitchPlan(refusals, target, conventions, released, projectWide, ConventionsDraft(projectWide));
    }

    /// <summary>
    /// The conventions file as first drafted: the whole project's decisions in force, one line each,
    /// a rule said twice said once. The owner shortens it before it is committed; a decision the
    /// owner does not keep retires.
    /// </summary>
    private static string ConventionsDraft(IEnumerable<StewardDecision> projectWide)
    {
        var sb = new StringBuilder(TicketGitService.ConventionsHeader.TrimEnd()).Append("\n\n");
        var seen = new HashSet<string>();
        foreach (var d in projectWide)
            if (seen.Add(Regex.Replace(d.Rule.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim()))
                sb.Append(ConventionLine(d)).Append('\n');
        return sb.ToString();
    }
}
