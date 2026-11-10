using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// Releasing a slice that has a branch of its own (docs/slice-branches.md, "Releasing a slice"):
/// the owner presses, and the board makes the merge into the local base. Never a push - on a
/// project where a push is a deploy, that stays the owner's, as it always was.
/// <para>
/// It is reached only from <see cref="OwnerReleasesUnitAsync"/>, a REST route with no tool behind
/// it, after the refusals every release makes (every outcome done, which includes a walk-through
/// that passed; every release note that is something to do ticked off). What it adds is the git,
/// the refusals the git needs, and promoting the decisions the owner ticks into the conventions.
/// </para>
/// </summary>
public partial class StewardService
{
    private async Task<StewardReply> ReleaseSliceAsync(ProjectConfig cfg, PlanUnit u, StewardUnit row, string slice,
        IReadOnlyList<StewardEntry> notes, IReadOnlyList<int> promote, IReadOnlyList<Ticket> mine, CancellationToken ct)
    {
        var projectKey = cfg.Name;
        var p = cfg.Steward.Plan;
        var unit = u.Number;

        var open = mine.Where(t => t.PlanUnit == unit && t.Phase != TicketPhase.Done).Select(t => t.Id).ToList();
        if (open.Count > 0)
            return Refuse("tickets-open", $"Slice {unit} still has {open.Count} ticket{(open.Count == 1 ? "" : "s")} open ({string.Join(", ", open.Take(8).Select(i => $"#{i}"))}). A slice is released whole: finish or close them first.");
        // A run merges into the slice and keeps it up with the base; the release does both too.
        if (await RunOpenAsync(projectKey))
            return Refuse("run-open", "A steward run is in progress. Release the slice once it has ended.");

        var promoted = new List<StewardDecision>();
        if (promote.Count > 0)
        {
            if (cfg.Steward.Conventions.Length == 0)
                return Refuse("no-conventions", "The project names no conventions file (steward.conventions), so there is nowhere to promote a decision to.");
            await EnsureRegisterAsync(cfg, ct);
            var wanted = promote.Distinct().ToList();
            promoted = await _db.StewardDecisions
                .Where(d => d.ProjectKey == projectKey && wanted.Contains(d.Number)).ToListAsync(ct);
            foreach (var n in wanted)
            {
                var d = promoted.FirstOrDefault(x => x.Number == n);
                if (d is null) return Refuse("bad-request", $"There is no decision #{n}.");
                if (d.PlanUnit != unit || (d.PlanKey ?? "") != p.Key)
                    return Refuse("bad-request", $"#{n} is not a decision of slice {unit}. Releasing a slice promotes only its own.");
                if (d.WithdrawnAt is not null) return Refuse("bad-request", $"#{n} was withdrawn, so it is not a rule to keep.");
                if (d.PromotedAt is not null) return Refuse("bad-request", $"#{n} is in the conventions already.");
            }
            promoted = wanted.Select(n => promoted.First(d => d.Number == n)).ToList();
        }

        var prep = await _git.PrepareSliceReleaseAsync(projectKey, slice, ct);
        if (prep.Refusal is not null || prep.SliceTip is null)
            return Refuse(prep.Conflicted.Count > 0 ? "conflict" : "refused", prep.Refusal ?? $"could not ready {slice} for release",
                prep.Conflicted.Count > 0 ? new { files = prep.Conflicted } : null);

        // The slice now holds everything the base has, so its tip is the tree the release makes:
        // the merge checks run there, on a tree of the board's own, and a failure refuses.
        var checks = cfg.Steward.MergeChecks
            .Where(c => c.WhenChanged.Length == 0 || prep.Files.Any(f => c.WhenChanged.Any(w =>
                f.StartsWith(w.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))).ToList();
        if (checks.Count > 0)
        {
            var tree = await _git.CheckTreeAsync(projectKey, slice, ct);
            if (tree.Dir is null)
                return Refuse("check-could-not-run", $"A tree at the tip of {slice} could not be made to run the merge checks in, so nothing is proved. Nothing was merged.",
                    new { output = tree.Error });
            try
            {
                var env = new Dictionary<string, string> { [BaseRefVariable] = prep.BaseBranch };
                foreach (var c in checks)
                {
                    var run = await _git.RunCheckAsync(tree.Dir, c.Run, TimeSpan.FromMinutes(c.TimeoutMinutes), env, ct);
                    if (run.Ok) continue;
                    var why = c.MachineFault.Length > 0 && Regex.IsMatch(run.Output, c.MachineFault, RegexOptions.IgnoreCase)
                        ? $"'{c.Name}' did not get as far as checking anything, so nothing is proved either way."
                        : run.TimedOut ? $"'{c.Name}' did not finish in {c.TimeoutMinutes} minutes on what the release would make."
                        : $"'{c.Name}' fails on what the release would make.";
                    return Refuse("check-failed", $"{why} Nothing was merged; {slice} has been brought up to {prep.BaseBranch}.",
                        new { check = c.Name, failing = run.Failures ?? Array.Empty<string>(), output = run.Output });
                }
            }
            finally
            {
                await tree.Release();
            }
        }

        var title = TicketGitService.UnitTitle(u);
        var body = $"Releases {slice} ({u.File}) into {prep.BaseBranch}.";
        if (promoted.Count > 0)
            body += $"\n\nPromoted to {cfg.Steward.Conventions}: {string.Join(", ", promoted.Select(d => $"#{d.Number}"))}.";
        var lines = promoted.Select(ConventionLine).ToList();
        var (sha, no) = await _git.ReleaseSliceAsync(projectKey, slice, prep.SliceTip, $"Slice {unit}: {title}\n\n{body}",
            cfg.Steward.Conventions, lines, ct);
        if (sha is null) return Refuse("refused", no ?? $"could not merge {slice} into {prep.BaseBranch}");

        var now = DateTime.UtcNow;
        foreach (var n in notes.Where(n => n.HandledAt is null)) n.HandledAt = now;
        foreach (var d in promoted) (d.PromotedAt, d.ChangedAt) = (now, now);
        (row.ReleasedAt, row.ReleasedSha) = (now, sha);
        await StewardDue.MarkAsync(_db, projectKey, $"the owner released slice {unit}");
        await _db.SaveChangesAsync(ct);
        if (promoted.Count > 0) await ExportDecisionsAsync(cfg, ct);
        await WriteReadableAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("released", null, new
        {
            at = sha, slice, into = prep.BaseBranch, merged = true,
            promoted = promoted.Select(d => d.Number), conventions = promoted.Count > 0 ? cfg.Steward.Conventions : null,
        });
    }

    /// <summary>A decision as one line of the conventions file, citing where it came from.</summary>
    public static string ConventionLine(StewardDecision d) =>
        $"- {Regex.Replace(d.Rule.Trim(), @"\s+", " ")} (#{d.Number})";

    /// <summary>
    /// The steward marks a decision of an open slice as one it thinks outlives the slice, or takes
    /// the mark back. Only a suggestion: what reaches the conventions is what the owner ticks when
    /// releasing (docs/slice-branches.md, section 4, "Promoting at release").
    /// </summary>
    public async Task<StewardReply> SuggestPromoteAsync(string projectKey, int decision, string? why, bool withdraw = false,
        CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (!cfg.SliceBranchesOn || cfg.Steward.Conventions.Length == 0)
            return Refuse("no-conventions", "This project has no conventions file to promote into. Nothing is suggested.");
        why = why?.Trim() ?? "";
        if (!withdraw && why.Length == 0)
            return Refuse("bad-request", "Say in a line why it outlives its slice: a rule about money, data, or how every page behaves.");
        if (why.Length > FollowUpMaxChars) return Refuse("bad-request", $"Why is a line, at most {FollowUpMaxChars} characters.");
        await EnsureRegisterAsync(cfg, ct);
        var d = await _db.StewardDecisions.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Number == decision, ct);
        if (d is null) return Refuse("bad-request", $"There is no decision #{decision}.");
        if (d.PlanUnit is not { } unit) return Refuse("bad-request", $"#{decision} is not a slice's decision, so there is no release to promote it at.");
        if (d.WithdrawnAt is not null) return Refuse("bad-request", $"#{decision} was withdrawn.");
        if (d.PromotedAt is not null) return Refuse("bad-request", $"#{decision} is in the conventions already.");
        var released = await _db.StewardUnits.AnyAsync(x => x.ProjectKey == projectKey && x.Plan == (d.PlanKey ?? "")
            && x.Unit == unit && x.ReleasedAt != null, ct);
        if (released) return Refuse("bad-request", $"Slice {unit} is released; #{decision} can be promoted from the Steward page, not suggested.");
        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed(withdraw ? $"take back the suggestion to promote #{decision}" : $"suggest promoting #{decision}: {Head(why, 200)}");
        (d.PromoteSuggestedAt, d.PromoteWhy) = withdraw ? ((DateTime?)null, (string?)null) : (DateTime.UtcNow, why);
        await _db.SaveChangesAsync(ct);
        return new StewardReply(withdraw ? "withdrawn" : "suggested", null, new { decision });
    }
}
