using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// The walk-through driven (docs/plans-as-slices.md, section 4). A slice's walk-through is its Z
/// outcome that says so - Z1 in the template - and the board, not the steward, files it, so it
/// cannot be forgotten: once every outcome outside the Z group is done, at the start of the next
/// steward run. Its QA drives the story end to end on the base branch, and a pass is what calls the
/// walk-through done; a slice is built only when it is.
/// </summary>
public partial class StewardService
{
    /// <summary>
    /// The id of a slice's walk-through outcome: the one in its Z group whose sentence says it is the
    /// walk-through. Read from the words, not assumed from the id, because a slice written before the
    /// template said so may use Z1 for something else entirely - and then it has no walk-through for
    /// the board to file, and the steward files its Z group as it always has.
    /// </summary>
    public static string? WalkthroughOf(StewardPlan plan, PlanUnit u) =>
        TicketGitService.UnitGroups(plan, u).SelectMany(g => g.Outcomes)
            .FirstOrDefault(o => o.Outcome.StartsWith('Z') && Regex.IsMatch(o.Text, @"walk-?through", RegexOptions.IgnoreCase))
            ?.Outcome;

    private async Task<StewardOutcome> OutcomeRowAsync(string projectKey, int unit, string outcome, CancellationToken ct)
    {
        var o = await _db.StewardOutcomes.FirstOrDefaultAsync(x => x.ProjectKey == projectKey
            && x.Unit == unit && x.Outcome == outcome, ct);
        if (o is null) _db.StewardOutcomes.Add(o = new StewardOutcome { ProjectKey = projectKey, Unit = unit, Outcome = outcome });
        return o;
    }

    /// <summary>
    /// Close the walk-throughs that passed, then file the ones that are due. Returns what it did,
    /// for the run's opening, or null when it did nothing. In propose mode it only says.
    /// </summary>
    private async Task<object?> WalkthroughsAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var p = cfg.Steward.Plan;
        if (!p.Configured) return null;
        var projectKey = cfg.Name;
        var propose = cfg.StewardMode == StewardMode.Propose;
        var all = await _tickets.ListAsync(projectKey, null);
        var closed = new List<string>();
        var filed = new List<string>();

        // Passed: QA drove every step and handed it to review. Done, and its outcome with it.
        foreach (var t in all.Where(t => t.Walkthrough && t.Phase == TicketPhase.NeedsReview && t.PlanUnit is not null))
        {
            var walk = string.IsNullOrWhiteSpace(t.PlanOutcomes) ? "Z1" : t.PlanOutcomes.Trim();
            if (propose) { closed.Add($"would close #{t.Id} and call slice {t.PlanUnit}'s {walk} done"); continue; }
            await _tickets.TransitionAsync(t.Id, TicketPhase.Done, "The walk-through passed: every step of the story driven end to end.", TransitionActor.System);
            var o = await OutcomeRowAsync(projectKey, t.PlanUnit!.Value, walk, ct);
            (o.State, o.MoreToFile, o.Note, o.UpdatedAt) = (StewardOutcomeState.Done, false, $"Driven end to end by #{t.Id}.", DateTime.UtcNow);
            await _db.SaveChangesAsync(ct);
            closed.Add($"#{t.Id}: slice {t.PlanUnit}'s walk-through passed, so {walk} is done");
        }

        // Due: every outcome outside the Z group done, and no walk-through filed yet.
        var plan = await _git.PlanAsync(projectKey, ct);
        var states = await StatesAsync(projectKey, ct);
        var rows = await _db.StewardUnits.Where(u => u.ProjectKey == projectKey).ToListAsync(ct);
        foreach (var u in plan.Where(u => u.Agreed))
        {
            if (WalkthroughOf(p, u) is not { } walk) continue;
            var row = rows.FirstOrDefault(r => r.Unit == u.Number);
            if (row?.ReleasedAt is not null) continue;
            if (row?.WalkthroughTicketId is { } had && all.Any(t => t.Id == had)) continue;
            var rest = u.Outcomes.Where(o => !o.StartsWith('Z')).ToList();
            if (rest.Count == 0 || rest.Any(o => states.FirstOrDefault(s => s.Unit == u.Number && s.Outcome == o)?.State != StewardOutcomeState.Done))
                continue;
            if (states.FirstOrDefault(s => s.Unit == u.Number && s.Outcome == walk)?.State == StewardOutcomeState.Done) continue;
            var story = SliceStory.Find(u.Text)?.Text;
            if (string.IsNullOrWhiteSpace(story)) { filed.Add($"slice {u.Number} is ready for its walk-through, but its spec has no story to drive"); continue; }
            if (propose) { filed.Add($"would file slice {u.Number}'s walk-through"); continue; }

            var steps = SliceStory.Sentences(story).Select((s, i) => $"{i + 1}. {s}");
            // On a project with slice branches the walk-through runs on the slice's own branch, so the
            // owner tests one slice with nothing else mixed in; the claim names it as the ticket's base.
            var where = SliceBranches.BaseFor(cfg, u.Number, row) is { } sliceBranch
                ? $"on the slice's own branch, {sliceBranch}, as it stands"
                : "on the base branch as it stands";
            var description = string.Join("\n\n", new[]
            {
                $"The walk-through of slice {u.Number} ({u.File}), driven end to end {where}. "
                    + "Filed by the board because every other outcome of the unit is done. It changes no code.",
                $"**The story**\n\n{story}",
                $"**Acceptance criteria:** each step below works, in order, as one person doing it, and is captured.\n\n{string.Join("\n", steps)}",
                "**Out of scope:** changing anything. A step that fails is reported by blocking this ticket with the step; "
                    + "the fix is filed against the outcome that step cites.",
                $"**Slice**\n\nSlice {u.Number}: {walk}.",
            });
            var made = await _tickets.CreateAsync(projectKey, $"Walk-through: {TicketGitService.UnitTitle(u)}", description, null, null,
                TicketPhase.NeedsQa, skipClassroomAnalysis: true);
            var ticket = await _db.Tickets.FirstAsync(t => t.Id == made.Id, ct);
            (ticket.Walkthrough, ticket.PlanUnit, ticket.PlanOutcomes, ticket.CreatedBy)
                = (true, u.Number, walk, TransitionActor.System);
            if (row is null) { _db.StewardUnits.Add(row = new StewardUnit { ProjectKey = projectKey, Unit = u.Number }); rows.Add(row); }
            row.WalkthroughTicketId = made.Id;
            var z = await OutcomeRowAsync(projectKey, u.Number, walk, ct);
            (z.State, z.MoreToFile, z.Note, z.UpdatedAt) = (StewardOutcomeState.InProgress, false, $"The walk-through is #{made.Id}.", DateTime.UtcNow);
            await _db.SaveChangesAsync(ct);
            filed.Add($"#{made.Id}: slice {u.Number}'s walk-through, straight to QA");
        }

        return closed.Count == 0 && filed.Count == 0 ? null : new { closed, filed };
    }
}
