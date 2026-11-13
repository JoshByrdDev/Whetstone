using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>An idea the steward proposes to the owner: the slice file, and its whole text.</summary>
public record StewardIdeaProposal(string File, string Text);

/// <summary>
/// A plan is its slices (docs/plans-as-slices.md, section 1): ideas the owner writes, drafted one
/// at a time when their turn comes, agreed, built, released. This is the part of that the board
/// keeps: the plan as read, generated rather than maintained; the idea a steward proposes and the
/// owner commits; and the agreed slice whose number or file has moved.
/// </summary>
public partial class StewardService
{
    /// <summary>The options every idea proposal is answered by. The first commits it.</summary>
    public const string CommitIdea = "Commit this idea", LeaveIdeas = "Leave the ideas as they are";

    /// <summary>
    /// Why a proposed idea cannot be committed as asked, or null when it can. An idea comes after
    /// the last agreed slice, and replaces only an idea: a draft is the steward's to revise, and an
    /// agreed slice is never touched.
    /// </summary>
    private string? IdeaRefusal(StewardPlan p, IReadOnlyList<PlanUnit> plan, StewardIdeaProposal idea)
    {
        var name = idea.File?.Trim() ?? "";
        var m = Regex.Match(name, p.UnitFile, RegexOptions.IgnoreCase);
        if (!m.Success || name.Contains('/') || name.Contains('\\') || !int.TryParse(m.Groups[1].Value, out var number))
            return "Name the idea's file the way the plan's slices are named, number first, with no folder.";
        var lastAgreed = plan.Where(u => u.Agreed).Select(u => u.Number).DefaultIfEmpty(0).Max();
        if (number <= lastAgreed) return $"Slice {lastAgreed} is agreed, so an idea is numbered after it. An agreed slice's number never changes.";
        var path = $"{p.UnitsDir}/{name}";
        var unit = TicketGitService.ReadUnit(p, number, path, idea.Text ?? "");
        if (!unit.Idea) return "Its status line must say it is an idea (**Status: idea.**): a draft is written with steward_draft_unit, and only from an idea.";
        if (plan.FirstOrDefault(u => string.Equals(u.File, path, StringComparison.OrdinalIgnoreCase)) is { Idea: false } there)
            return $"{there.File} is {(there.Agreed ? "agreed" : "a draft")}, not an idea: an idea replaces only an idea.";
        return null;
    }

    /// <summary>Commit an idea the owner said yes to. Null when it is committed, else why not.</summary>
    private async Task<string?> CommitIdeaAsync(ProjectConfig cfg, StewardQuestion q, CancellationToken ct)
    {
        var p = cfg.Steward.Plan;
        var plan = await _git.PlanAsync(cfg.Name, ct);
        var idea = new StewardIdeaProposal(q.ProposedFile!, q.ProposedText!);
        if (IdeaRefusal(p, plan, idea) is { } no) return $"The idea was not committed: {no}";
        var (_, _, refusal) = await _git.CommitFileAsync(cfg.Name, $"{p.UnitsDir}/{idea.File}", idea.Text,
            $"An idea, {idea.File}, committed by its owner (answering {q.Key})", ct);
        return refusal;
    }

    // ---- the plan as read ----------------------------------------------------------------------

    // ---- an agreed slice that moved -------------------------------------------------------------

    /// <summary>
    /// Agreed slices whose number or file has changed since they were agreed, by number, with what
    /// is wrong. Remembers the file of every agreed slice it has not seen before, which is how a
    /// slice agreed by hand in its file is held to its number too.
    /// </summary>
    private async Task<Dictionary<int, string>> MovedSlicesAsync(string projectKey, StewardPlan p, IReadOnlyList<PlanUnit> plan, CancellationToken ct)
    {
        var rows = await _db.StewardUnits.Where(u => u.ProjectKey == projectKey).ToListAsync(ct);
        var moved = new Dictionary<int, string>();
        var changed = false;
        foreach (var u in plan.Where(u => u.Agreed))
        {
            var row = rows.FirstOrDefault(r => r.Unit == u.Number);
            if (row is null) { _db.StewardUnits.Add(row = new StewardUnit { ProjectKey = projectKey, Unit = u.Number }); rows.Add(row); }
            if (row.AgreedFile is null) { row.AgreedFile = u.File; changed = true; }
        }
        // An abandoned slice no longer says it is agreed, on purpose.
        foreach (var row in rows.Where(r => r.AgreedFile is not null && r.AbandonedAt is null))
        {
            var now = plan.FirstOrDefault(u => u.Number == row.Unit);
            if (now is null || !string.Equals(now.File, row.AgreedFile, StringComparison.OrdinalIgnoreCase) || !now.Agreed)
                moved[row.Unit] = now is null
                    ? $"Slice {row.Unit} was agreed as {row.AgreedFile}, and no file has that number now. Tickets and outcomes carry its number: put it back."
                    : !now.Agreed ? $"Slice {row.Unit} was agreed, and {now.File} no longer says so."
                    : $"Slice {row.Unit} was agreed as {row.AgreedFile}, and is {now.File} now. An agreed slice's file and number are fixed: tickets and outcomes carry them.";
        }
        if (changed) await _db.SaveChangesAsync(ct);
        return moved;
    }
}
