using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// A slice built on another (docs/slice-branches.md, section 3, piece 4): the parent its spec
/// names, checked when it is agreed; its branch cut from that parent's and kept up with it; released
/// only after it; re-parented onto the base when the parent is released; and put to the owner when
/// the parent is abandoned.
/// <para>
/// One level, one parent, and the parent open: a slice that needs two unreleased slices waits for
/// one of them. Every rule here is the board's, refused in code, because a steward never agrees,
/// releases or abandons a slice.
/// </para>
/// </summary>
public partial class StewardService
{
    /// <summary>An open slice's branch, and the branch it keeps up with: its parent's, or null for the base.</summary>
    private record OpenSlice(int Unit, string Branch, string? From);

    /// <summary>
    /// Agreeing one more slice would open more than the project builds at once (docs/slice-branches.md,
    /// section 3): open is agreed with its branch, and neither released nor abandoned. Ideas and
    /// drafts never count.
    /// </summary>
    private static StewardReply? InFlight(ProjectConfig cfg, IEnumerable<StewardUnit> rows)
    {
        if (!cfg.SliceBranchesOn) return null;
        var open = rows.Where(r => r.Branch != null && r.ReleasedAt == null && r.AbandonedAt == null).Select(r => r.Unit).Order().ToList();
        var most = cfg.Steward.SlicesInFlight;
        return open.Count < most ? null
            : Refuse("in-flight", $"{open.Count} slice{(open.Count == 1 ? " is" : "s are")} open ({string.Join(", ", open)}), as many as {cfg.DisplayName} builds at once (slicesInFlight {most}). Release or abandon one before agreeing another.");
    }

    /// <summary>
    /// The project's open slices - agreed, cut, neither released nor abandoned - parents before the
    /// slices built on them, so a run brings a parent up to the base before its child takes it.
    /// Empty on a project without slice branches.
    /// </summary>
    private async Task<IReadOnlyList<OpenSlice>> OpenSlicesAsync(ProjectConfig cfg, CancellationToken ct)
    {
        if (!cfg.SliceBranchesOn) return Array.Empty<OpenSlice>();
        var rows = await _db.StewardUnits.AsNoTracking()
            .Where(u => u.ProjectKey == cfg.Name && u.Branch != null && u.ReleasedAt == null && u.AbandonedAt == null)
            .ToListAsync(ct);
        return rows.OrderBy(r => r.ParentUnit is null ? 0 : 1).ThenBy(r => r.Unit)
            .Select(r => new OpenSlice(r.Unit, r.Branch!,
                r.ParentUnit is { } p ? rows.FirstOrDefault(x => x.Unit == p)?.Branch : null))
            .ToList();
    }

    /// <summary>
    /// The parent a slice about to be agreed is built on, or why it cannot be agreed with the
    /// parents its spec names. A named slice already released is in the base and no parent at all.
    /// </summary>
    private static (StewardUnit? Parent, StewardReply? No) ParentOf(ProjectConfig cfg, int unit, string specText,
        IReadOnlyList<StewardUnit> rows)
    {
        if (!cfg.SliceBranchesOn) return (null, null);
        var named = SliceBranches.ParentsNamed(specText).Where(n => n != unit).ToList();
        var open = named.Where(n => rows.FirstOrDefault(r => r.Unit == n)?.ReleasedAt is null).ToList();
        if (open.Count == 0) return (null, null);
        if (open.Count > 1)
            return (null, Refuse("two-parents",
                $"Slice {unit} is built on slices {string.Join(" and ", open)}, none of them released. A slice is built on one other at most: " +
                $"wait until one of them is released, then name only the other in its header."));
        var parent = rows.FirstOrDefault(r => r.Unit == open[0]);
        if (parent?.AbandonedAt is not null)
            return (null, Refuse("parent-not-open", $"Slice {unit} is built on slice {open[0]}, which was abandoned. Take it out of the header, or name another."));
        if (parent?.AgreedAt is null || string.IsNullOrWhiteSpace(parent.Branch))
            return (null, Refuse("parent-not-open", $"Slice {unit} is built on slice {open[0]}, which is not open yet. Agree slice {open[0]} first."));
        if (parent.ParentUnit is { } grand)
            return (null, Refuse("chain",
                $"Slice {open[0]} is itself built on slice {grand}. One level only: wait until slice {grand} is released, then agree slice {unit}."));
        return (parent, null);
    }

    /// <summary>
    /// The owner abandons a slice unreleased, from the Steward page - a REST route with no tool
    /// behind it. Its spec is marked abandoned on the base, so nothing more is filed against it, and
    /// its branch is kept until the owner deletes it.
    /// <para>
    /// A slice built on it cannot go on silently (section 3). <paramref name="children"/> is the
    /// owner's answer for those: <c>recut</c> moves each onto the base, which only a child with
    /// nothing of its own yet can do without losing work, or <c>abandon</c> abandons each too.
    /// Without an answer, the reply is the question, with the slices it is about.
    /// </para>
    /// </summary>
    public async Task<StewardReply> OwnerAbandonsUnitAsync(string projectKey, int unit, string? children, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        if (!cfg.SliceBranchesOn) return Refuse("no-slice-branches", $"{cfg.DisplayName} does not have slice branches; a slice there is abandoned by editing its spec.");
        var p = cfg.Steward.Plan;
        var plan = await _git.PlanAsync(projectKey, ct);
        var u = plan.FirstOrDefault(x => x.Number == unit);
        var rows = await _db.StewardUnits.Where(x => x.ProjectKey == projectKey).ToListAsync(ct);
        var row = rows.FirstOrDefault(r => r.Unit == unit);
        if (u is null || !u.Agreed || row is null) return Refuse("unit-not-agreed", $"Slice {unit} is not agreed.");
        if (row.ReleasedAt is not null) return Refuse("already-released", $"Slice {unit} was released; it is the base's now.");
        if (await RunOpenAsync(projectKey)) return Refuse("run-open", "A steward run is in progress. Abandon the slice once it has ended.");

        var mine = (await _tickets.ListAsync(projectKey, null)).ToList();
        StewardReply? StillOpen(int n)
        {
            var open = mine.Where(t => t.PlanUnit == n && t.Phase != TicketPhase.Done).Select(t => t.Id).ToList();
            return open.Count == 0 ? null : Refuse("tickets-open",
                $"Slice {n} has {open.Count} ticket{(open.Count == 1 ? "" : "s")} open ({string.Join(", ", open.Take(8).Select(i => $"#{i}"))}). Close {(open.Count == 1 ? "it" : "them")} first: abandoning a slice leaves nothing half-built in it.",
                new { tickets = open });
        }
        if (StillOpen(unit) is { } busy) return busy;

        var kids = rows.Where(r => r.ParentUnit == unit && r.ReleasedAt is null && r.AbandonedAt is null).OrderBy(r => r.Unit).ToList();
        children = children?.Trim().ToLowerInvariant();
        if (kids.Count > 0 && children is not ("recut" or "abandon"))
            return Refuse("children",
                $"{(kids.Count == 1 ? $"Slice {kids[0].Unit} is" : $"Slices {string.Join(" and ", kids.Select(k => k.Unit))} are")} built on slice {unit}. " +
                "Say whether to re-cut " + (kids.Count == 1 ? "it" : "them") + " from the base, or abandon " + (kids.Count == 1 ? "it" : "them") + " too.",
                new { children = kids.Select(k => new { unit = k.Unit, branch = k.Branch }) });

        // Everything is checked before anything is written: a half-abandoned family is worse than none.
        var marks = new List<(StewardUnit Row, PlanUnit Unit, string Text)>();
        string? Mark(StewardUnit r)
        {
            var spec = plan.FirstOrDefault(x => x.Number == r.Unit);
            if (spec is null) return $"Slice {r.Unit}'s spec is not in the plan any more.";
            if (TicketGitService.MarkAbandoned(p, spec.Text, DateTime.Now) is not { } text)
                return $"The status line of {spec.File} is not written the way the board can rewrite. Mark it abandoned by hand and commit that.";
            marks.Add((r, spec, text));
            return null;
        }
        if (Mark(row) is { } cannot) return Refuse("not-by-rule", cannot);
        if (children == "abandon")
            foreach (var k in kids)
            {
                if (StillOpen(k.Unit) is { } kidBusy) return kidBusy;
                if (Mark(k) is { } kidCannot) return Refuse("not-by-rule", kidCannot);
            }
        else if (children == "recut")
            foreach (var k in kids)
                if (StillOpen(k.Unit) is { } kidBusy) return kidBusy;

        // Re-cutting moves branches, which cannot be undone by a refusal later: each is tried, and a
        // child that cannot move stops the whole abandonment before any spec is committed.
        var recut = new List<object>();
        if (children == "recut")
            foreach (var k in kids)
            {
                var (at, no) = string.IsNullOrWhiteSpace(k.Branch) || string.IsNullOrWhiteSpace(row.Branch)
                    ? (null, null)
                    : await _git.RecutSliceAsync(projectKey, k.Branch!, row.Branch!, ct);
                if (no is not null)
                    return Refuse("cannot-recut", $"{no} Nothing was abandoned{(recut.Count > 0 ? $"; {recut.Count} other slice{(recut.Count == 1 ? " was" : "s were")} re-cut already" : "")}.",
                        new { recut });
                k.ParentUnit = null;
                recut.Add(new { unit = k.Unit, branch = k.Branch, at });
            }

        var now = DateTime.UtcNow;
        var abandoned = new List<int>();
        foreach (var (r, spec, text) in marks)
        {
            var (sha, _, no) = await _git.CommitFileAsync(projectKey, spec.File, text, $"Slice {r.Unit}, abandoned by its owner", ct);
            if (no is not null)
            {
                await _db.SaveChangesAsync(ct);
                return Refuse("refused", abandoned.Count == 0 ? no : $"Slice{(abandoned.Count == 1 ? "" : "s")} {string.Join(", ", abandoned)} were abandoned, but {spec.File} was not: {no}");
            }
            r.AbandonedAt = now;
            abandoned.Add(r.Unit);
        }
        await StewardDue.MarkAsync(_db, projectKey, $"the owner abandoned slice{(abandoned.Count == 1 ? "" : "s")} {string.Join(", ", abandoned)}");
        await _db.SaveChangesAsync(ct);
        // What an abandoned slice decided retires with it, as a released one's does: none of it is
        // in the base, and nothing is left to keep it.
        await ExportDecisionsAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("abandoned", null, new { abandoned, recut, branchesKept = marks.Select(m => m.Row.Branch).Where(b => b is not null) });
    }

    /// <summary>
    /// The owner builds an open slice on another after agreeing it, from the Steward page - a REST
    /// route with no tool behind it. A parent is otherwise only read when a slice is agreed, so an
    /// owner who decides afterwards that a slice needs another's work had no way to say so: a
    /// <c>Built on</c> line added to the spec by hand was read by the steward and by nothing that
    /// moves a branch, and the slice went on keeping up with the base it was cut from.
    /// <para>
    /// The same rules as agreeing (section 3): one parent, open, itself on the base, and one level.
    /// Only a slice with nothing of its own - no open ticket, no work on its branch - can move,
    /// because every ticket it has was forked from the base. Its branch is moved onto the parent's
    /// tip, its header is given the line, and the board remembers the parent from then on.
    /// </para>
    /// </summary>
    public async Task<StewardReply> OwnerBuildsUnitOnAsync(string projectKey, int unit, int parentUnit, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        if (!cfg.SliceBranchesOn) return Refuse("no-slice-branches", $"{cfg.DisplayName} does not have slice branches, so a slice there is not built on another.");
        if (parentUnit == unit) return Refuse("bad-request", $"Slice {unit} cannot be built on itself.");
        var plan = await _git.PlanAsync(projectKey, ct);
        var u = plan.FirstOrDefault(x => x.Number == unit);
        var rows = await _db.StewardUnits.Where(x => x.ProjectKey == projectKey).ToListAsync(ct);
        var row = rows.FirstOrDefault(r => r.Unit == unit);
        if (u is null || !u.Agreed || row is null || string.IsNullOrWhiteSpace(row.Branch))
            return Refuse("unit-not-open", $"Slice {unit} is not agreed with a branch of its own. Name its parent in its header before agreeing it.");
        if (row.ReleasedAt is not null || row.AbandonedAt is not null)
            return Refuse("unit-not-open", $"Slice {unit} was {(row.ReleasedAt is not null ? "released" : "abandoned")}.");
        if (row.ParentUnit == parentUnit) return Refuse("already-built-on", $"Slice {unit} is already built on slice {parentUnit}.");
        if (row.ParentUnit is { } other) return Refuse("two-parents", $"Slice {unit} is already built on slice {other}. A slice is built on one other at most.");
        var named = SliceBranches.ParentsNamed(u.Text).Where(n => n != unit && n != parentUnit).ToList();
        if (named.Count > 0)
            return Refuse("two-parents", $"{u.File} already names slice {string.Join(" and ", named)} on its Built on line. Take that out first.");
        var kids = rows.Where(r => r.ParentUnit == unit && r.ReleasedAt is null && r.AbandonedAt is null).Select(r => r.Unit).Order().ToList();
        if (kids.Count > 0)
            return Refuse("chain", $"Slice{(kids.Count == 1 ? "" : "s")} {string.Join(" and ", kids)} {(kids.Count == 1 ? "is" : "are")} built on slice {unit}. One level only.");
        var (parent, noParent) = ParentOf(cfg, unit, $"Built on: slice {parentUnit}", rows);
        if (noParent is not null) return noParent;
        if (parent is null) return Refuse("parent-not-open", $"Slice {parentUnit} was released, so it is in the base already.");
        if (await RunOpenAsync(projectKey)) return Refuse("run-open", "A steward run is in progress. Try again once it has ended.");
        var open = (await _tickets.ListAsync(projectKey, null))
            .Where(t => t.PlanUnit == unit && t.Phase != TicketPhase.Done).Select(t => t.Id).ToList();
        if (open.Count > 0)
            return Refuse("tickets-open",
                $"Slice {unit} has {open.Count} ticket{(open.Count == 1 ? "" : "s")} open ({string.Join(", ", open.Take(8).Select(i => $"#{i}"))}), forked from the base. " +
                "A slice moves onto another only before anything is built in it.", new { tickets = open });

        // The spec's line is worked out before the branch moves, so a spec the board cannot rewrite
        // moves nothing.
        string? text = null;
        if (!SliceBranches.ParentsNamed(u.Text).Contains(parentUnit))
        {
            text = TicketGitService.MarkBuiltOn(cfg.Steward.Plan, u.Text, parentUnit);
            if (text is null)
                return Refuse("not-by-rule", $"The status line of {u.File} is not written the way the board can find. Add \"Built on: slice {parentUnit}\" to its header by hand, commit that, and try again.");
        }
        var (at, noMove) = await _git.MoveSliceOntoAsync(projectKey, row.Branch!, parent.Branch!, ct);
        if (noMove is not null) return Refuse("cannot-move", noMove);
        string? sha = null;
        if (text is not null)
        {
            var (made, _, noCommit) = await _git.CommitFileAsync(projectKey, u.File, text, $"Slice {unit}, built on slice {parentUnit} by its owner", ct);
            if (noCommit is not null)
            {
                // Nothing of its own was on it, so putting it back on the base loses nothing.
                var (_, noBack) = await _git.RecutSliceAsync(projectKey, row.Branch!, parent.Branch!, ct);
                return Refuse("refused", noBack is null
                    ? $"{u.File} was not committed, so {row.Branch} was put back on the base: {noCommit}"
                    : $"{u.File} was not committed ({noCommit}), and {row.Branch} could not be put back on the base: {noBack}");
            }
            sha = made;
        }
        row.ParentUnit = parentUnit;
        await StewardDue.MarkAsync(_db, projectKey, $"the owner built slice {unit} on slice {parentUnit}");
        await _db.SaveChangesAsync(ct);
        _heartbeat?.Wake();
        return new StewardReply("built-on", null, new { unit, builtOn = parentUnit, branch = row.Branch, at, spec = sha });
    }

    /// <summary>
    /// After <paramref name="released"/> is merged into the base, every open slice built on it takes
    /// the base - which now holds the parent - and becomes an ordinary slice (section 3). A merge
    /// that conflicts leaves the child for the next run to put to the owner, as any slice behind
    /// the base is.
    /// </summary>
    private async Task<IReadOnlyList<object>> ReparentChildrenAsync(string projectKey, StewardUnit released, CancellationToken ct)
    {
        var kids = await _db.StewardUnits.Where(x => x.ProjectKey == projectKey
            && x.ParentUnit == released.Unit && x.ReleasedAt == null && x.AbandonedAt == null).ToListAsync(ct);
        var said = new List<object>();
        foreach (var k in kids)
        {
            k.ParentUnit = null;
            await _db.SaveChangesAsync(ct);
            var r = string.IsNullOrWhiteSpace(k.Branch) ? null : await _git.SyncSliceAsync(projectKey, k.Branch!, false, null, ct);
            said.Add(new { unit = k.Unit, branch = k.Branch, answer = r?.Answer, files = r?.Files, why = r?.Why });
        }
        return said;
    }
}
